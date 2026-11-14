using System.Text;

namespace Atlas.Yjs;

/// <summary>A shared type of a <see cref="YjsDocumentTree"/>: a named root, an XmlElement (a ProseMirror node) or an XmlText.</summary>
internal sealed class YTreeType(YTypeRef kind, string? name, YTreeItem? item)
{
    public YTypeRef Kind { get; } = kind;
    /// <summary>The node name of an element, the name of a root.</summary>
    public string? Name { get; } = name;
    /// <summary>The item that created the type; null for a root.</summary>
    public YTreeItem? Item { get; } = item;
    public YTreeItem? Start { get; set; }
    /// <summary>Attributes: the rightmost item of each key (its value is current unless deleted).</summary>
    public Dictionary<string, YTreeItem>? Map { get; set; }
    public bool IsRoot => Item is null;
}

/// <summary>One integrated struct: an item linked into its parent, or a GC range (<see cref="Parent"/> null).</summary>
internal sealed class YTreeItem
{
    public required ulong Client { get; init; }
    public required uint Clock { get; init; }
    public uint Length { get; set; }
    public YContentKind Content { get; set; }
    public YId? Origin { get; set; }
    public YId? RightOrigin { get; init; }
    public YTreeItem? Left { get; set; }
    public YTreeItem? Right { get; set; }
    public YTreeType? Parent { get; set; }
    public string? ParentSub { get; set; }
    public bool Deleted { get; set; }
    /// <summary>The type a <see cref="YContentKind.Type"/> item created.</summary>
    public YTreeType? Type { get; set; }
    /// <summary>A format's key.</summary>
    public string? Key { get; init; }
    /// <summary>An Any item's last value (lib0 Any), or a format's JSON text (UTF-8).</summary>
    public byte[]? Value { get; init; }
    /// <summary>A string item's characters, when the tree keeps text.</summary>
    public string? Text { get; set; }
    public bool IsGc => Parent is null;
    public uint End => Clock + Length;
}

/// <summary>
/// A managed replica of the Yjs document the guard admitted: every item linked into its parent's list or map exactly as Yjs
/// integrates it (Item.getMissing and Item.integrate of yjs 13.6, including YATA conflict resolution, splits and the delete set),
/// so the guard can read the ProseMirror document an update produces. It keeps lengths, formats and attribute values, and the
/// characters only when asked (<paramref name="text"/>, for <c>YjsDocumentMaterializer</c> (Atlas.Markdown.Sync)). Payloads must already have
/// passed <c>YjsParentResolver</c> (Atlas.Markdown.Sync) (no gaps, unknown references or cycles).
/// An update is applied with a journal: the guard validates the result and then commits it or rolls it back exactly.
/// Not thread-safe: callers hold the per-document gate.
/// </summary>
/// <remarks>
/// Adjacent items of one client merge after a payload is kept, as Yjs merges them after a transaction (Item.mergeWith): typing
/// grows a run instead of adding an item per keystroke, and deleted runs collapse.
/// shortcut: deleting a type does not mark its children deleted (Yjs does); visibility is read from the ancestors instead,
/// which yields the same document.
/// </remarks>
internal sealed class YjsDocumentTree(bool text = false)
{
    private readonly Dictionary<ulong, List<YTreeItem>> _clients = [];
    private readonly Dictionary<string, YTreeType> _roots = new(StringComparer.Ordinal);
    private List<Action>? _journal;
    private long _scanned;
    /// <summary>Items a payload added or deleted: candidates for merging once it is kept.</summary>
    private readonly List<YTreeItem> _touched = [];
    private long _bytes, _pendingBytes;

    public int ItemCount { get; private set; }

    public bool KeepsText => text;

    public YTreeType? Root(string name) => _roots.GetValueOrDefault(name);

    public uint GetState(ulong client) => _clients.TryGetValue(client, out var items) && items.Count > 0 ? items[^1].End : 0;

    /// <summary>
    /// Integrates a resolved payload. Returns the elements (and roots) whose attributes or children it changed. With
    /// <paramref name="journal"/>, the change stays revocable until <see cref="Commit"/> or <see cref="Rollback"/>.
    /// </summary>
    /// <param name="bounded">Refuse a payload that leaves more items than <see cref="YjsLimits.ItemBudget"/> (not for stored state).</param>
    public HashSet<YTreeType> Apply(ReadOnlySpan<byte> payload, YjsUpdate update, bool journal, bool bounded = true)
    {
        if (_journal is not null) throw new InvalidOperationException("The previous payload is neither committed nor rolled back.");
        if (journal) _journal = [];
        _scanned = 0;
        _touched.Clear();
        var changed = new HashSet<YTreeType>();
        var structs = update.Structs;
        var clients = update.Clients;
        var next = new int[clients.Length];
        var ranges = new Dictionary<ulong, int>(clients.Length);
        for (var range = 0; range < clients.Length; range++) ranges[clients[range].Client] = range;
        var stack = new Stack<int>();
        for (var range = 0; range < clients.Length; range++)
        {
            while (next[range] < clients[range].Count)
            {
                stack.Push(clients[range].First + next[range]);
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    var own = ranges[structs[top].Client];
                    if (clients[own].First + next[own] > top)
                    {
                        stack.Pop();
                        continue;
                    }
                    var missing = Missing(structs[top], clients, ranges, next);
                    if (missing >= 0)
                    {
                        if (stack.Count > structs.Length)
                            throw new ContentRejectedException(ContentRejectionReason.UnknownReference, "cyclic references between clients");
                        stack.Push(missing);
                        continue;
                    }
                    Integrate(payload, structs[top], changed);
                    next[own]++;
                    stack.Pop();
                }
            }
        }
        foreach (var range in update.DeleteSet) DeleteRange(range, changed);
        _pendingBytes = _bytes + payload.Length;
        if (bounded && ItemCount > YjsLimits.ItemBudget(_pendingBytes))
            throw new ContentRejectedException(ContentRejectionReason.TooManyItems, $"the document would hold {ItemCount} items in {_pendingBytes} bytes");
        if (!journal) Keep();
        return changed;
    }

    /// <summary>Keeps the journaled payload.</summary>
    public void Commit()
    {
        _journal = null;
        Keep();
    }

    /// <summary>Restores the tree to its state before the journaled payload.</summary>
    public void Rollback()
    {
        Forget();
        if (_journal is not { } journal) return;
        _journal = null;
        for (var index = journal.Count - 1; index >= 0; index--) journal[index]();
    }

    private void Keep()
    {
        _bytes = _pendingBytes;
        foreach (var item in _touched)
        {
            var items = _clients[item.Client];
            var index = FindIndex(items, item.Clock); // the item, or the run it was merged into
            if (index + 1 < items.Count) TryMerge(items, index);
            if (index > 0) TryMerge(items, index - 1);
        }
        Forget();
    }

    private void Forget()
    {
        _touched.Clear();
        if (_touched.Capacity > 4096) _touched.TrimExcess(); // a large payload's candidates
    }

    /// <summary>Item.mergeWith: an item absorbs its right neighbour of the same client when Yjs would merge them.</summary>
    private void TryMerge(List<YTreeItem> items, int index)
    {
        var left = items[index];
        var right = items[index + 1];
        if (left.IsGc || right.IsGc)
        {
            if (!left.IsGc || !right.IsGc) return;
        }
        else if (left.Right != right || left.Deleted != right.Deleted || right.Origin != new YId(left.Client, left.End - 1)
            || left.RightOrigin != right.RightOrigin
            // Live runs of text merge; deleted content of any kind but a shared type is invisible (Yjs turns it into ContentDeleted).
            || (left.Deleted ? left.Content == YContentKind.Type || right.Content == YContentKind.Type
                : left.Content != YContentKind.String || right.Content != YContentKind.String))
            return;
        else
        {
            left.Right = right.Right;
            if (right.Right is not null) right.Right.Left = left;
            if (right.ParentSub is { } key && left.Parent!.Map is { } map && map.GetValueOrDefault(key) == right) map[key] = left;
            if (text) left.Text = left.Deleted ? null : left.Text + right.Text;
        }
        left.Length += right.Length;
        items.RemoveAt(index + 1);
        ItemCount--;
    }

    /// <summary>Every client's structs in clock order (the struct store's lists), for encoding.</summary>
    internal IReadOnlyDictionary<ulong, List<YTreeItem>> Clients => _clients;

    /// <summary>
    /// yjs 13.6.33 Item.integrate of a local item (<see cref="YDocument"/>): its left, right, origins, parent and key are set; YATA
    /// links it, the struct store takes it, its type is created, a new map value deletes the previous one.
    /// </summary>
    internal void IntegrateLocal(YTreeItem item, YTypeRef typeRef = default, string? name = null)
    {
        _scanned = 0;
        var parent = item.Parent!;
        Place(item, parent);
        AddStruct(item);
        if (item.Content == YContentKind.Type) item.Type = new YTreeType(typeRef, name, item);
        if (item.ParentSub is not null && item.Right is null && item.Left is not null) Delete(item.Left, LocalChanges);
        if (parent.Item?.Deleted == true) Delete(item, LocalChanges);
        Forget();
        LocalChanges.Clear();
    }

    /// <summary>yjs 13.6.33 Item.markDeleted of a local delete (<see cref="YDocument"/> recurses into a deleted type's content).</summary>
    internal void DeleteLocal(YTreeItem item)
    {
        Delete(item, LocalChanges);
        Forget();
        LocalChanges.Clear();
    }

    /// <summary>Doc.get of a root the document does not have yet: a type without an item, nothing written.</summary>
    internal YTreeType CreateRoot(string name) => _roots[name] = new YTreeType(YTypeRef.XmlFragment, name, null);

    /// <summary>yjs 13.6.33 getItemCleanStart.</summary>
    internal YTreeItem CleanStart(YId id) => GetItemCleanStart(id);

    private HashSet<YTreeType> LocalChanges { get; } = [];

    /// <summary>
    /// yjs 13.6.33 cleanupTransactions (the struct merge of a client's new structs, from <paramref name="beforeClock"/>) with
    /// tryToMergeWithLefts and Item.mergeWith, as a document without gc merges them: strings join, deleted content stays.
    /// </summary>
    internal void MergeNewStructs(ulong client, uint beforeClock)
    {
        if (!_clients.TryGetValue(client, out var structs) || structs.Count == 0) return;
        var firstChangePos = Math.Max(beforeClock >= GetState(client) ? structs.Count : FindIndex(structs, beforeClock), 1);
        for (var i = structs.Count - 1; i >= firstChangePos;) i -= 1 + TryToMergeWithLefts(structs, i);
    }

    /// <summary>yjs 13.6.33 tryToMergeWithLefts.</summary>
    private int TryToMergeWithLefts(List<YTreeItem> structs, int pos)
    {
        var right = structs[pos];
        var left = structs[pos - 1];
        var i = pos;
        while (i > 0 && left.Deleted == right.Deleted && left.IsGc == right.IsGc && MergeWith(left, right))
        {
            if (right.ParentSub is { } key && right.Parent!.Map is { } map && map.GetValueOrDefault(key) == right) map[key] = left;
            right = left;
            if (--i > 0) left = structs[i - 1];
        }
        var merged = pos - i;
        if (merged > 0)
        {
            structs.RemoveRange(pos + 1 - merged, merged);
            ItemCount -= merged;
        }
        return merged;
    }

    /// <summary>yjs 13.6.33 Item.mergeWith (GC.mergeWith for GCs) for a document without gc.</summary>
    private bool MergeWith(YTreeItem left, YTreeItem right)
    {
        if (left.IsGc)
        {
            left.Length += right.Length;
            return true;
        }
        if (right.Origin != new YId(left.Client, left.End - 1) || left.Right != right || left.RightOrigin != right.RightOrigin
            || left.Client != right.Client || left.End != right.Clock || left.Deleted != right.Deleted || left.Content != right.Content)
            return false;
        switch (left.Content)
        {
            case YContentKind.String:
                if (text) left.Text += right.Text;
                break;
            case YContentKind.Deleted:
                break;
            case YContentKind.Any or YContentKind.Json:
                // The replica keeps one value of such content; the binding writes them one per item, which never meet here.
                throw new InvalidOperationException("Merging array content is not supported.");
            default:
                return false;
        }
        left.Right = right.Right;
        if (left.Right is not null) left.Right.Left = left;
        left.Length += right.Length;
        return true;
    }

    private void Record(Action undo) => _journal?.Add(undo);

    /// <summary>The next struct of the payload this one waits for (Yjs integrates a struct once its references exist), or -1.</summary>
    private int Missing(in YStruct current, YClientRange[] clients, Dictionary<ulong, int> ranges, int[] next)
    {
        foreach (var reference in (ReadOnlySpan<YId?>)[current.Origin, current.RightOrigin, current.ParentId])
        {
            if (reference is not { } id || id.Client == current.Client || id.Clock < GetState(id.Client)) continue;
            if (!ranges.TryGetValue(id.Client, out var range) || next[range] >= clients[range].Count)
                throw new ContentRejectedException(ContentRejectionReason.UnknownReference, $"reference to unknown id {id.Client}:{id.Clock}");
            return clients[range].First + next[range];
        }
        return -1;
    }

    private void Integrate(ReadOnlySpan<byte> payload, in YStruct current, HashSet<YTreeType> changed)
    {
        var state = GetState(current.Client);
        if (current.End <= state) return;
        if (current.Clock > state) throw new ContentRejectedException(ContentRejectionReason.ClockGap, $"client {current.Client} starts at {current.Clock}, known {state}");
        var offset = state - current.Clock;
        var item = new YTreeItem
        {
            Client = current.Client,
            Clock = current.Clock + offset,
            Length = current.Length - offset,
            Content = current.Content,
            Origin = current.Origin,
            RightOrigin = current.RightOrigin,
            ParentSub = current.ParentSub,
            Key = current.Content == YContentKind.Format ? current.Name : null,
            Value = current.Content switch
            {
                YContentKind.Any => payload.Slice(current.ValueOffset, current.ValueLength).ToArray(),
                YContentKind.Format => new Lib0Reader(payload.Slice(current.ValueOffset, current.ValueLength)).ReadUtf8().ToArray(),
                _ => null,
            },
        };
        if (text && current.Content == YContentKind.String)
        {
            var characters = Encoding.UTF8.GetString(new Lib0Reader(payload.Slice(current.ValueOffset, current.ValueLength)).ReadUtf8());
            item.Text = offset > 0 ? Splice(characters, (int)offset).Right : characters;
        }
        if (current.Content == YContentKind.Gc)
        {
            Gc(item);
            return;
        }
        // Item.getMissing: neighbours (split so the origin ends and the right origin starts an item), then the parent.
        var left = current.Origin is { } origin ? GetItemCleanEnd(origin) : null;
        var right = current.RightOrigin is { } rightOrigin ? GetItemCleanStart(rightOrigin) : null;
        YTreeType? parent;
        if (left?.IsGc == true || right?.IsGc == true) parent = null;
        else if (current.Origin is not null || current.RightOrigin is not null)
        {
            var neighbour = left ?? right!;
            parent = neighbour.Parent;
            item.ParentSub = neighbour.ParentSub;
        }
        else if (current.ParentRoot is { } root)
        {
            if (!_roots.TryGetValue(root, out parent))
            {
                _roots[root] = parent = new YTreeType(YTypeRef.XmlFragment, root, null);
                Record(() => _roots.Remove(root));
            }
        }
        else
        {
            var owner = GetItem(current.ParentId!.Value);
            parent = owner.IsGc || owner.Content != YContentKind.Type ? null : owner.Type;
        }
        if (offset > 0)
        {
            left = GetItemCleanEnd(new YId(item.Client, item.Clock - 1));
            item.Origin = new YId(left.Client, left.End - 1);
        }
        if (parent is null)
        {
            Gc(item);
            return;
        }
        item.Parent = parent;
        item.Left = left;
        item.Right = right;
        Place(item, parent);
        AddStruct(item);
        if (item.Content == YContentKind.Type)
        {
            item.Type = new YTreeType(current.TypeRef, current.Name, item);
            if (current.TypeRef == YTypeRef.XmlElement) changed.Add(item.Type);
        }
        if (item.Content == YContentKind.Deleted) item.Deleted = true;
        Changed(parent, changed);
        // The new current value of an attribute replaces the previous one; a value left of a newer one, or under a deleted
        // parent, is deleted at once.
        if (item.ParentSub is not null && item.Right is null && item.Left is not null) Delete(item.Left, changed);
        if (parent.Item?.Deleted == true || (item.ParentSub is not null && item.Right is not null)) Delete(item, changed);
    }

    /// <summary>Item.integrate: YATA conflict resolution against concurrent inserts at the same position, then linking.</summary>
    private void Place(YTreeItem item, YTreeType parent)
    {
        if ((item.Left is null && (item.Right is null || item.Right.Left is not null)) || (item.Left is not null && item.Left.Right != item.Right))
        {
            var left = item.Left;
            YTreeItem? o;
            if (left is not null) o = left.Right;
            else if (item.ParentSub is not null)
            {
                o = parent.Map?.GetValueOrDefault(item.ParentSub);
                while (o?.Left is not null) o = o.Left;
            }
            else o = parent.Start;
            var conflicting = new HashSet<YTreeItem>(ReferenceEqualityComparer.Instance);
            var beforeOrigin = new HashSet<YTreeItem>(ReferenceEqualityComparer.Instance);
            while (o is not null && o != item.Right)
            {
                if (++_scanned > YjsLimits.MaxConflictScan)
                    throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "too many concurrent inserts at one position");
                beforeOrigin.Add(o);
                conflicting.Add(o);
                if (item.Origin == o.Origin)
                {
                    if (o.Client < item.Client)
                    {
                        left = o;
                        conflicting.Clear();
                    }
                    else if (item.RightOrigin == o.RightOrigin) break;
                }
                else if (o.Origin is { } other && beforeOrigin.Contains(GetItem(other)))
                {
                    if (!conflicting.Contains(GetItem(other)))
                    {
                        left = o;
                        conflicting.Clear();
                    }
                }
                else break;
                o = o.Right;
            }
            item.Left = left;
        }
        if (item.Left is not null)
        {
            var left = item.Left;
            var right = left.Right;
            item.Right = right;
            left.Right = item;
            Record(() => left.Right = right);
        }
        else
        {
            YTreeItem? first;
            if (item.ParentSub is not null)
            {
                first = parent.Map?.GetValueOrDefault(item.ParentSub);
                while (first?.Left is not null) first = first.Left;
            }
            else
            {
                first = parent.Start;
                parent.Start = item;
                Record(() => parent.Start = first);
            }
            item.Right = first;
        }
        if (item.Right is { } next)
        {
            var before = next.Left;
            next.Left = item;
            Record(() => next.Left = before);
        }
        else if (item.ParentSub is not null) SetValue(parent, item.ParentSub, item);
    }

    /// <summary>The current item of an attribute key.</summary>
    private void SetValue(YTreeType parent, string key, YTreeItem item)
    {
        var map = parent.Map ??= new(StringComparer.Ordinal);
        var had = map.TryGetValue(key, out var previous);
        map[key] = item;
        Record(() =>
        {
            if (had) map[key] = previous!;
            else map.Remove(key);
        });
    }

    private void Gc(YTreeItem item)
    {
        item.Content = YContentKind.Gc;
        item.Text = null;
        item.Parent = null;
        item.Deleted = true;
        AddStruct(item);
    }

    private void Delete(YTreeItem item, HashSet<YTreeType> changed)
    {
        if (item.Deleted) return;
        item.Deleted = true;
        _touched.Add(item);
        Record(() => item.Deleted = false);
        Changed(item.Parent!, changed);
    }

    /// <summary>A text change changes the element holding the text.</summary>
    private static void Changed(YTreeType type, HashSet<YTreeType> changed)
    {
        if (type.Kind == YTypeRef.XmlText)
        {
            if (type.Item?.Parent is { } element) changed.Add(element);
        }
        else changed.Add(type);
    }

    /// <summary>readAndApplyDeleteSet for clocks the tree has (the resolver refused the others).</summary>
    private void DeleteRange(YDeleteRange range, HashSet<YTreeType> changed)
    {
        if (!_clients.TryGetValue(range.Client, out var items) || range.Clock >= GetState(range.Client)) return;
        var end = (ulong)range.Clock + range.Length;
        var index = FindIndex(items, range.Clock);
        var item = items[index];
        if (!item.Deleted && item.Clock < range.Clock)
        {
            Split(items, index, range.Clock - item.Clock);
            index++;
        }
        while (index < items.Count)
        {
            item = items[index++];
            if (item.Clock >= end) break;
            if (item.Deleted) continue;
            if (end < item.End) Split(items, index - 1, (uint)(end - item.Clock));
            Delete(item, changed);
        }
    }

    private void AddStruct(YTreeItem item)
    {
        if (ItemCount >= YjsLimits.MaxIndexSegments) throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "document tree is full");
        if (!_clients.TryGetValue(item.Client, out var items))
        {
            _clients[item.Client] = items = [];
            Record(() => _clients.Remove(item.Client));
        }
        else if (items[^1].End != item.Clock) throw new InvalidOperationException("Yjs tree integration out of clock order.");
        items.Add(item);
        ItemCount++;
        _touched.Add(item);
        Record(() =>
        {
            items.RemoveAt(items.Count - 1);
            ItemCount--;
        });
    }

    private YTreeItem GetItem(YId id)
    {
        if (!_clients.TryGetValue(id.Client, out var items) || id.Clock >= GetState(id.Client))
            throw new ContentRejectedException(ContentRejectionReason.UnknownReference, $"reference to unknown id {id.Client}:{id.Clock}");
        return items[FindIndex(items, id.Clock)];
    }

    private YTreeItem GetItemCleanEnd(YId id)
    {
        var item = GetItem(id);
        if (id.Clock != item.End - 1 && !item.IsGc)
        {
            var items = _clients[id.Client];
            Split(items, FindIndex(items, id.Clock), id.Clock - item.Clock + 1);
        }
        return item;
    }

    private YTreeItem GetItemCleanStart(YId id)
    {
        var item = GetItem(id);
        if (item.Clock >= id.Clock || item.IsGc) return item;
        var items = _clients[id.Client];
        var index = FindIndex(items, id.Clock);
        Split(items, index, id.Clock - item.Clock);
        return items[index + 1];
    }

    /// <summary>splitItem: the item keeps its first <paramref name="diff"/> clocks, a new item right of it the rest.</summary>
    private void Split(List<YTreeItem> items, int index, uint diff)
    {
        var left = items[index];
        var right = new YTreeItem
        {
            Client = left.Client,
            Clock = left.Clock + diff,
            Length = left.Length - diff,
            Content = left.Content,
            Origin = new YId(left.Client, left.Clock + diff - 1),
            RightOrigin = left.RightOrigin,
            Left = left,
            Right = left.Right,
            Parent = left.Parent,
            ParentSub = left.ParentSub,
            Deleted = left.Deleted,
            Key = left.Key,
            Value = left.Value,
        };
        var characters = left.Text;
        if (characters is not null) (left.Text, right.Text) = Splice(characters, (int)diff);
        left.Right = right;
        if (right.Right is not null) right.Right.Left = right;
        left.Length = diff;
        items.Insert(index + 1, right);
        ItemCount++;
        Record(() =>
        {
            left.Text = characters;
            left.Length += right.Length;
            left.Right = right.Right;
            if (right.Right is not null) right.Right.Left = left;
            items.RemoveAt(index + 1);
            ItemCount--;
        });
        if (right.ParentSub is not null && right.Right is null) SetValue(right.Parent!, right.ParentSub, right);
    }

    /// <summary>
    /// ContentString.splice: the first <paramref name="offset"/> UTF-16 units and the rest. Yjs replaces the halves of a surrogate
    /// pair it splits with U+FFFD on both sides, so every replica holds the same (valid) strings.
    /// </summary>
    private static (string Left, string Right) Splice(string characters, int offset)
        => char.IsHighSurrogate(characters[offset - 1])
            ? (string.Concat(characters.AsSpan(0, offset - 1), "\uFFFD"), string.Concat("\uFFFD", characters.AsSpan(offset + 1)))
            : (characters[..offset], characters[offset..]);

    private static int FindIndex(List<YTreeItem> items, uint clock)
    {
        var low = 0;
        var high = items.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var item = items[middle];
            if (item.Clock <= clock)
            {
                if (clock < item.End) return middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        throw new ContentRejectedException(ContentRejectionReason.UnknownReference, $"clock {clock} is not in the document");
    }
}
