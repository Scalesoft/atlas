using System.Text;

namespace Atlas.Yjs;

/// <summary>
/// One local transaction of a Y.Doc (yjs 13.6.33) on a <see cref="YjsDocumentTree"/> that keeps text: the shared-type operations
/// y-prosemirror calls (XmlFragment/XmlElement insert, delete, setAttribute, removeAttribute; Y.Text insert, delete, applyDelta,
/// toDelta) ported function by function, their items integrated as Item.integrate does, then the transaction's struct merge and
/// Y.encodeStateAsUpdate. Format values keep JavaScript identity (<see cref="FormatValue"/>), as the ports compare them with ===.
/// Search markers are left out: they only speed up the walks, and a text with formats has none (ContentFormat.integrate clears them).
/// Not thread-safe.
/// </summary>
internal sealed class YDocument
{
    private readonly Dictionary<YTreeItem, object?> _formatValues = new(ReferenceEqualityComparer.Instance);
    private readonly IReadOnlyDictionary<ulong, uint> _before;

    public YDocument(YjsDocumentTree tree, uint clientId)
    {
        if (!tree.KeepsText) throw new ArgumentException("The tree must keep the text.", nameof(tree));
        Tree = tree;
        ClientId = clientId;
        _before = tree.Clients.ToDictionary(pair => pair.Key, pair => tree.GetState(pair.Key));
    }

    public YjsDocumentTree Tree { get; }

    public uint ClientId { get; }

    /// <summary>The state vector before the transaction (Y.encodeStateVector).</summary>
    public IReadOnlyDictionary<ulong, uint> BeforeState => _before;

    /// <summary>Doc.get(name, Y.XmlFragment): the root type, created when the document has none.</summary>
    public YTreeType GetXmlFragment(string name) => Tree.Root(name) ?? Tree.CreateRoot(name);

    // ---- Item ----------------------------------------------------------------------------------------------------------

    private static YId LastId(YTreeItem item) => new(item.Client, item.End - 1);

    private static YId Id(YTreeItem item) => new(item.Client, item.Clock);

    /// <summary>yjs 13.6.33 Item.countable (via the content's isCountable).</summary>
    private static bool Countable(YTreeItem item) => item.Content is not (YContentKind.Format or YContentKind.Deleted or YContentKind.Gc);

    /// <summary>new Item(createID(ownClientId, getState(store, ownClientId)), left, left.lastId, right, right.id, parent, parentSub, content).integrate(transaction, 0).</summary>
    private YTreeItem Integrate(YTreeItem? left, YTreeItem? right, YTreeType parent, string? parentSub, YContentKind content, uint length,
        string? text = null, string? key = null, byte[]? value = null, YTypeRef typeRef = default, string? name = null)
    {
        var item = new YTreeItem
        {
            Client = ClientId,
            Clock = Tree.GetState(ClientId),
            Length = length,
            Content = content,
            Origin = left is null ? null : LastId(left),
            RightOrigin = right is null ? null : Id(right),
            Left = left,
            Right = right,
            Parent = parent,
            ParentSub = parentSub,
            Key = key,
            Value = value,
            Text = text,
        };
        Tree.IntegrateLocal(item, typeRef, name);
        return item;
    }

    /// <summary>yjs 13.6.33 Item.delete: marks the item deleted, then ContentType.delete deletes the type's children and map values.</summary>
    private void Delete(YTreeItem item)
    {
        if (item.Deleted) return;
        Tree.DeleteLocal(item);
        if (item.Type is not { } type) return;
        for (var child = type.Start; child is not null; child = child.Right)
            if (!child.Deleted) Delete(child);
        if (type.Map is { } map)
            foreach (var value in map.Values.ToArray())
                if (!value.Deleted) Delete(value);
    }

    /// <summary>A format item's ContentFormat.value; one object per item, as JSON.parse made it once (or as the local write passed it).</summary>
    public object? FormatValue(YTreeItem item)
    {
        if (!_formatValues.TryGetValue(item, out var value))
            _formatValues[item] = value = Js.ParseJson(item.Value!);
        return value;
    }

    // ---- AbstractType (types/AbstractType.js) ----------------------------------------------------------------------------

    /// <summary>yjs 13.6.33 typeListToArray: the content of the countable, live items (y-prosemirror's documents hold only types).</summary>
    public List<YTreeType> TypeListToArray(YTreeType parent)
    {
        var children = new List<YTreeType>();
        for (var item = parent.Start; item is not null; item = item.Right)
        {
            if (item.Deleted || !Countable(item)) continue;
            children.Add(item.Type ?? throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent,
                $"{parent.Name ?? "the root fragment"}: holds {item.Content} content"));
        }
        return children;
    }

    /// <summary>AbstractType._length: the live countable length.</summary>
    public static int Length(YTreeType type)
    {
        var length = 0;
        for (var item = type.Start; item is not null; item = item.Right)
            if (!item.Deleted && Countable(item)) length += (int)item.Length;
        return length;
    }

    /// <summary>yjs 13.6.33 typeListInsertGenericsAfter, for content that is all shared types (y-prosemirror inserts nothing else).</summary>
    private void TypeListInsertGenericsAfter(YTreeType parent, YTreeItem? referenceItem, IReadOnlyList<YPrelim> content)
    {
        var left = referenceItem;
        var right = referenceItem is null ? parent.Start : referenceItem.Right;
        foreach (var c in content)
        {
            left = c is YPrelimElement element
                ? Integrate(left, right, parent, null, YContentKind.Type, 1, typeRef: YTypeRef.XmlElement, name: element.NodeName)
                : Integrate(left, right, parent, null, YContentKind.Type, 1, typeRef: YTypeRef.XmlText);
            IntegrateType(left.Type!, c);
        }
    }

    /// <summary>yjs 13.6.33 typeListInsertGenerics (without search markers).</summary>
    public void TypeListInsertGenerics(YTreeType parent, int index, IReadOnlyList<YPrelim> content)
    {
        if (index > Length(parent)) throw new InvalidOperationException("Length exceeded!");
        if (index == 0)
        {
            TypeListInsertGenericsAfter(parent, null, content);
            return;
        }
        var n = parent.Start;
        for (; n is not null; n = n.Right)
        {
            if (!n.Deleted && Countable(n))
            {
                if (index <= n.Length)
                {
                    if (index < n.Length) Tree.CleanStart(new YId(n.Client, n.Clock + (uint)index));
                    break;
                }
                index -= (int)n.Length;
            }
        }
        TypeListInsertGenericsAfter(parent, n, content);
    }

    /// <summary>yjs 13.6.33 typeListDelete (without search markers).</summary>
    public void TypeListDelete(YTreeType parent, int index, int length)
    {
        if (length == 0) return;
        var n = parent.Start;
        for (; n is not null && index > 0; n = n.Right)
        {
            if (!n.Deleted && Countable(n))
            {
                if (index < n.Length) Tree.CleanStart(new YId(n.Client, n.Clock + (uint)index));
                index -= (int)n.Length;
            }
        }
        while (length > 0 && n is not null)
        {
            if (!n.Deleted)
            {
                if (length < n.Length) Tree.CleanStart(new YId(n.Client, n.Clock + (uint)length));
                Delete(n);
                length -= (int)n.Length;
            }
            n = n.Right;
        }
        if (length > 0) throw new InvalidOperationException("array length exceeded");
    }

    /// <summary>yjs 13.6.33 typeMapSet with a JSON value (ContentAny).</summary>
    public void TypeMapSet(YTreeType parent, string key, object? value)
    {
        var left = parent.Map?.GetValueOrDefault(key);
        var any = new Lib0Writer();
        any.WriteAny(value);
        Integrate(left, null, parent, key, YContentKind.Any, 1, value: any.ToArray());
    }

    /// <summary>yjs 13.6.33 typeMapDelete.</summary>
    public void TypeMapDelete(YTreeType parent, string key)
    {
        if (parent.Map?.GetValueOrDefault(key) is { } c) Delete(c);
    }

    /// <summary>yjs 13.6.33 typeMapGetAll: the live attribute values (the last value of each, as lib0 decoded it).</summary>
    public JsObject TypeMapGetAll(YTreeType parent)
    {
        var res = new JsObject();
        if (parent.Map is null) return res;
        foreach (var (key, value) in parent.Map)
        {
            if (value.Deleted) continue;
            res[key] = value.Content == YContentKind.Any ? Js.ReadAny(value.Value!)
                : throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent, $"{parent.Name}: the value of {key} is {value.Content} content");
        }
        return res;
    }

    /// <summary>
    /// ContentType.integrate → type._integrate of a prelim type: YXmlElement inserts its prelim content (YXmlFragment._integrate) and
    /// then sets its prelim attributes; YText runs its pending operations.
    /// </summary>
    private void IntegrateType(YTreeType type, YPrelim prelim)
    {
        switch (prelim)
        {
            case YPrelimElement element:
                TypeListInsertGenerics(type, 0, element.Content);
                foreach (var key in element.Attributes.Keys) TypeMapSet(type, key, element.Attributes[key]);
                break;
            case YPrelimText text:
                foreach (var delta in text.Pending) ApplyDelta(type, delta);
                break;
        }
    }

    // ---- YText (types/YText.js) -----------------------------------------------------------------------------------------

    /// <summary>yjs 13.6.33 equalAttrs of YText.js: === or, for two objects, lib0 object.equalFlat.</summary>
    private static bool EqualAttrs(object? a, object? b) => Js.StrictEquals(a, b) || (Js.IsObject(a) && Js.IsObject(b) && EqualFlat(a, b));

    /// <summary>lib0 0.2.117 object.equalFlat.</summary>
    private static bool EqualFlat(object? a, object? b)
    {
        if (Js.StrictEquals(a, b)) return true;
        var keys = Js.Keys(a).ToArray();
        if (keys.Length != Js.Keys(b).Count()) return false;
        foreach (var key in keys)
        {
            var value = Js.Get(a, key);
            var bValue = Js.Get(b, key);
            if ((value is JsUndefined && !Js.Keys(b).Contains(key, StringComparer.Ordinal)) || !Js.StrictEquals(bValue, value)) return false;
        }
        return true;
    }

    /// <summary>yjs 13.6.33 ItemTextListPosition.forward.</summary>
    private void Forward(TextPosition pos)
    {
        var right = pos.Right ?? throw new InvalidOperationException("Unexpected case");
        if (right.Content == YContentKind.Format)
        {
            if (!right.Deleted) UpdateCurrentAttributes(pos.CurrentAttributes, right);
        }
        else if (!right.Deleted) pos.Index += (int)right.Length;
        pos.Left = right;
        pos.Right = right.Right;
    }

    /// <summary>yjs 13.6.33 findNextPosition.</summary>
    private TextPosition FindNextPosition(TextPosition pos, int count)
    {
        while (pos.Right is not null && count > 0)
        {
            var right = pos.Right;
            if (right.Content == YContentKind.Format)
            {
                if (!right.Deleted) UpdateCurrentAttributes(pos.CurrentAttributes, right);
            }
            else if (!right.Deleted)
            {
                if (count < right.Length) Tree.CleanStart(new YId(right.Client, right.Clock + (uint)count));
                pos.Index += (int)right.Length;
                count -= (int)right.Length;
            }
            pos.Left = right;
            pos.Right = right.Right;
        }
        return pos;
    }

    /// <summary>yjs 13.6.33 findPosition (search markers left out, see the class).</summary>
    private TextPosition FindPosition(YTreeType parent, int index) => FindNextPosition(new TextPosition(null, parent.Start, 0, new()), index);

    /// <summary>yjs 13.6.33 insertNegatedAttributes.</summary>
    private void InsertNegatedAttributes(YTreeType parent, TextPosition currPos, OrderedDictionary<string, object?> negatedAttributes)
    {
        while (currPos.Right is { } right && (right.Deleted || (right.Content == YContentKind.Format
            && EqualAttrs(negatedAttributes.TryGetValue(right.Key!, out var negated) ? negated : JsUndefined.Value, FormatValue(right)))))
        {
            if (!right.Deleted) negatedAttributes.Remove(right.Key!);
            Forward(currPos);
        }
        foreach (var (key, val) in negatedAttributes)
        {
            var nextFormat = IntegrateFormat(currPos.Left, currPos.Right, parent, key, val);
            currPos.Right = nextFormat;
            Forward(currPos);
        }
    }

    private YTreeItem IntegrateFormat(YTreeItem? left, YTreeItem? right, YTreeType parent, string key, object? value)
    {
        var item = Integrate(left, right, parent, null, YContentKind.Format, 1, key: key, value: Encoding.UTF8.GetBytes(Js.Stringify(value)));
        _formatValues[item] = value;
        return item;
    }

    /// <summary>yjs 13.6.33 updateCurrentAttributes.</summary>
    private void UpdateCurrentAttributes(OrderedDictionary<string, object?> currentAttributes, YTreeItem format)
    {
        var value = FormatValue(format);
        if (value is null) currentAttributes.Remove(format.Key!);
        else currentAttributes[format.Key!] = value;
    }

    /// <summary>yjs 13.6.33 minimizeAttributeChanges.</summary>
    private void MinimizeAttributeChanges(TextPosition currPos, JsObject attributes)
    {
        while (currPos.Right is { } right)
        {
            if (!right.Deleted && !(right.Content == YContentKind.Format
                && EqualAttrs(attributes.TryGetValue(right.Key!, out var attribute) && attribute is not JsUndefined ? attribute : null, FormatValue(right))))
                break;
            Forward(currPos);
        }
    }

    /// <summary>yjs 13.6.33 insertAttributes.</summary>
    private OrderedDictionary<string, object?> InsertAttributes(YTreeType parent, TextPosition currPos, JsObject attributes)
    {
        var negatedAttributes = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in attributes.Keys)
        {
            var val = attributes[key];
            var currentVal = currPos.CurrentAttributes.TryGetValue(key, out var current) ? current : null;
            if (!EqualAttrs(currentVal, val))
            {
                negatedAttributes[key] = currentVal;
                currPos.Right = IntegrateFormat(currPos.Left, currPos.Right, parent, key, val);
                Forward(currPos);
            }
        }
        return negatedAttributes;
    }

    /// <summary>yjs 13.6.33 insertText of a string.</summary>
    private void InsertText(YTreeType parent, TextPosition currPos, string text, JsObject attributes)
    {
        foreach (var key in currPos.CurrentAttributes.Keys)
            if (!attributes.TryGetValue(key, out var value) || value is JsUndefined) attributes[key] = null;
        MinimizeAttributeChanges(currPos, attributes);
        var negatedAttributes = InsertAttributes(parent, currPos, attributes);
        var index = currPos.Index;
        var right = Integrate(currPos.Left, currPos.Right, parent, null, YContentKind.String, (uint)text.Length, text: text);
        currPos.Right = right;
        currPos.Index = index;
        Forward(currPos);
        InsertNegatedAttributes(parent, currPos, negatedAttributes);
    }

    /// <summary>yjs 13.6.33 formatText.</summary>
    private void FormatText(YTreeType parent, TextPosition currPos, int length, JsObject attributes)
    {
        MinimizeAttributeChanges(currPos, attributes);
        var negatedAttributes = InsertAttributes(parent, currPos, attributes);
        while (currPos.Right is { } right && (length > 0 || (negatedAttributes.Count > 0 && (right.Deleted || right.Content == YContentKind.Format))))
        {
            if (!right.Deleted)
            {
                if (right.Content == YContentKind.Format)
                {
                    var key = right.Key!;
                    var value = FormatValue(right);
                    if (attributes.TryGetValue(key, out var attr) && attr is not JsUndefined)
                    {
                        if (EqualAttrs(attr, value)) negatedAttributes.Remove(key);
                        else
                        {
                            if (length == 0) break;
                            negatedAttributes[key] = value;
                        }
                        Delete(right);
                    }
                    else currPos.CurrentAttributes[key] = value;
                }
                else
                {
                    if (length < right.Length) Tree.CleanStart(new YId(right.Client, right.Clock + (uint)length));
                    length -= (int)right.Length;
                }
            }
            Forward(currPos);
        }
        if (length > 0)
        {
            currPos.Right = Integrate(currPos.Left, currPos.Right, parent, null, YContentKind.String, (uint)length, text: new string('\n', length));
            Forward(currPos);
        }
        InsertNegatedAttributes(parent, currPos, negatedAttributes);
    }

    /// <summary>yjs 13.6.33 cleanupFormattingGap.</summary>
    private int CleanupFormattingGap(YTreeItem start, YTreeItem? curr, OrderedDictionary<string, object?> startAttributes,
        OrderedDictionary<string, object?> currAttributes)
    {
        var end = start;
        var endFormats = new Dictionary<string, YTreeItem>(StringComparer.Ordinal);
        while (end is not null && (!Countable(end) || end.Deleted))
        {
            if (!end.Deleted && end.Content == YContentKind.Format) endFormats[end.Key!] = end;
            end = end.Right;
        }
        var cleanups = 0;
        var reachedCurr = false;
        YTreeItem? item = start;
        while (item != end)
        {
            if (curr == item) reachedCurr = true;
            if (!item!.Deleted && item.Content == YContentKind.Format)
            {
                var key = item.Key!;
                var value = FormatValue(item);
                var startAttrValue = startAttributes.TryGetValue(key, out var started) ? started : null;
                if (endFormats.GetValueOrDefault(key) != item || Js.StrictEquals(startAttrValue, value))
                {
                    Delete(item);
                    cleanups++;
                    if (!reachedCurr && Js.StrictEquals(currAttributes.TryGetValue(key, out var currValue) ? currValue : null, value)
                        && !Js.StrictEquals(startAttrValue, value))
                    {
                        if (startAttrValue is null) currAttributes.Remove(key);
                        else currAttributes[key] = startAttrValue;
                    }
                }
                if (!reachedCurr && !item.Deleted) UpdateCurrentAttributes(currAttributes, item);
            }
            item = item.Right;
        }
        return cleanups;
    }

    /// <summary>yjs 13.6.33 deleteText.</summary>
    private TextPosition DeleteText(TextPosition currPos, int length)
    {
        var startAttrs = new OrderedDictionary<string, object?>(currPos.CurrentAttributes, StringComparer.Ordinal);
        var start = currPos.Right;
        while (length > 0 && currPos.Right is { } right)
        {
            if (!right.Deleted && right.Content is YContentKind.Type or YContentKind.Embed or YContentKind.String)
            {
                if (length < right.Length) Tree.CleanStart(new YId(right.Client, right.Clock + (uint)length));
                length -= (int)right.Length;
                Delete(right);
            }
            Forward(currPos);
        }
        if (start is not null) CleanupFormattingGap(start, currPos.Right, startAttrs, currPos.CurrentAttributes);
        return currPos;
    }

    /// <summary>yjs 13.6.33 YText.insert without attributes (the inserted text takes the attributes in force).</summary>
    public void TextInsert(YTreeType text, int index, string value)
    {
        if (value.Length <= 0) return;
        var pos = FindPosition(text, index);
        var attributes = new JsObject();
        foreach (var (key, current) in pos.CurrentAttributes) attributes[key] = current;
        InsertText(text, pos, value, attributes);
    }

    /// <summary>yjs 13.6.33 YText.delete.</summary>
    public void TextDelete(YTreeType text, int index, int length)
    {
        if (length == 0) return;
        DeleteText(FindPosition(text, index), length);
    }

    /// <summary>yjs 13.6.33 YText.applyDelta (sanitize on) of inserts and retains.</summary>
    public void ApplyDelta(YTreeType text, IReadOnlyList<YDeltaOp> delta)
    {
        var currPos = new TextPosition(null, text.Start, 0, new(StringComparer.Ordinal));
        foreach (var op in delta)
        {
            if (op.Insert is { } insert)
            {
                if (insert.Length > 0) InsertText(text, currPos, insert, op.Attributes ?? new JsObject());
            }
            else FormatText(text, currPos, op.Retain, op.Attributes ?? new JsObject());
        }
    }

    /// <summary>yjs 13.6.33 YText.toDelta (no snapshots): one insert per run between visible formats, with the attributes in force.</summary>
    public List<(string Insert, JsObject? Attributes)> ToDelta(YTreeType text)
    {
        var ops = new List<(string, JsObject?)>();
        var currentAttributes = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var str = new StringBuilder();
        for (var n = text.Start; n is not null; n = n.Right)
        {
            if (n.Deleted) continue;
            switch (n.Content)
            {
                case YContentKind.String:
                    str.Append(n.Text);
                    break;
                case YContentKind.Type or YContentKind.Embed:
                    throw new ContentRejectedException(ContentRejectionReason.UnsupportedContent, "an embed in a text");
                case YContentKind.Format:
                    PackStr();
                    UpdateCurrentAttributes(currentAttributes, n);
                    break;
            }
        }
        PackStr();
        return ops;

        void PackStr()
        {
            if (str.Length == 0) return;
            JsObject? attributes = null;
            foreach (var (key, value) in currentAttributes) (attributes ??= new JsObject())[key] = value;
            ops.Add((str.ToString(), attributes));
            str.Clear();
        }
    }

    // ---- Encoding (utils/encoding.js, UpdateEncoderV1) ------------------------------------------------------------------

    /// <summary>
    /// The end of the transaction (cleanupTransactions: the new structs merged; no gc) and yjs 13.6.33 Y.encodeStateAsUpdate(doc,
    /// stateVector before the transaction): writeClientsStructs, then writeDeleteSet(createDeleteSetFromStructStore(store)).
    /// </summary>
    public byte[] EncodeUpdate()
    {
        foreach (var (client, _) in Tree.Clients) Tree.MergeNewStructs(client, _before.GetValueOrDefault(client));
        return EncodeStateAsUpdate(_before);
    }

    /// <summary>yjs 13.6.33 encodeStateAsUpdate (writeStateAsUpdate with an UpdateEncoderV1).</summary>
    public byte[] EncodeStateAsUpdate(IReadOnlyDictionary<ulong, uint> targetStateVector)
    {
        var encoder = new Lib0Writer();
        WriteClientsStructs(encoder, targetStateVector);
        WriteDeleteSet(encoder);
        return encoder.ToArray();
    }

    /// <summary>yjs 13.6.33 writeClientsStructs: the clients with new structs, higher client ids first.</summary>
    private void WriteClientsStructs(Lib0Writer encoder, IReadOnlyDictionary<ulong, uint> sv)
    {
        var sm = new List<(ulong Client, uint Clock)>();
        foreach (var (client, structs) in Tree.Clients)
        {
            if (structs.Count == 0) continue;
            var clock = sv.GetValueOrDefault(client);
            if (Tree.GetState(client) > clock) sm.Add((client, clock));
        }
        encoder.WriteVarUint((ulong)sm.Count);
        foreach (var (client, clock) in sm.OrderByDescending(entry => entry.Client)) WriteStructs(encoder, Tree.Clients[client], client, clock);
    }

    /// <summary>yjs 13.6.33 writeStructs.</summary>
    private void WriteStructs(Lib0Writer encoder, List<YTreeItem> structs, ulong client, uint clock)
    {
        clock = Math.Max(clock, structs[0].Clock);
        var startNewStructs = structs.FindIndex(item => item.Clock <= clock && clock < item.End);
        encoder.WriteVarUint((ulong)(structs.Count - startNewStructs));
        encoder.WriteVarUint(client);
        encoder.WriteVarUint(clock);
        var firstStruct = structs[startNewStructs];
        WriteItem(encoder, firstStruct, clock - firstStruct.Clock);
        for (var i = startNewStructs + 1; i < structs.Count; i++) WriteItem(encoder, structs[i], 0);
    }

    /// <summary>yjs 13.6.33 Item.write (GC.write for a GC) and the content's write.</summary>
    private static void WriteItem(Lib0Writer encoder, YTreeItem item, uint offset)
    {
        if (item.IsGc)
        {
            encoder.Write(0);
            encoder.WriteVarUint(item.Length - offset);
            return;
        }
        YId? origin = offset > 0 ? new YId(item.Client, item.Clock + offset - 1) : item.Origin;
        var rightOrigin = item.RightOrigin;
        var parentSub = item.ParentSub;
        encoder.Write((byte)(((byte)item.Content & 0x1F) | (origin is null ? 0 : 0x80) | (rightOrigin is null ? 0 : 0x40) | (parentSub is null ? 0 : 0x20)));
        if (origin is { } left)
        {
            encoder.WriteVarUint(left.Client);
            encoder.WriteVarUint(left.Clock);
        }
        if (rightOrigin is { } right)
        {
            encoder.WriteVarUint(right.Client);
            encoder.WriteVarUint(right.Clock);
        }
        if (origin is null && rightOrigin is null)
        {
            var parent = item.Parent!;
            if (parent.Item is not { } parentItem)
            {
                encoder.WriteVarUint(1);
                encoder.WriteVarString(parent.Name!);
            }
            else
            {
                encoder.WriteVarUint(0);
                encoder.WriteVarUint(parentItem.Client);
                encoder.WriteVarUint(parentItem.Clock);
            }
            if (parentSub is not null) encoder.WriteVarString(parentSub);
        }
        switch (item.Content)
        {
            case YContentKind.Deleted:
                encoder.WriteVarUint(item.Length - offset);
                break;
            case YContentKind.String:
                encoder.WriteVarString(offset == 0 ? item.Text! : item.Text![(int)offset..]);
                break;
            case YContentKind.Format when offset == 0:
                encoder.WriteVarString(item.Key!);
                encoder.WriteVarUint((ulong)item.Value!.Length);
                encoder.Write(item.Value);
                break;
            case YContentKind.Type when offset == 0:
                encoder.WriteVarUint((ulong)item.Type!.Kind);
                if (item.Type.Kind is YTypeRef.XmlElement or YTypeRef.XmlHook) encoder.WriteVarString(item.Type.Name!);
                break;
            case YContentKind.Any when offset == 0 && item.Length == 1:
                encoder.WriteVarUint(1);
                encoder.Write(item.Value);
                break;
            default:
                throw new InvalidOperationException($"The replica cannot write {item.Content} content at offset {offset}.");
        }
    }

    /// <summary>
    /// yjs 13.6.33 writeDeleteSet(createDeleteSetFromStructStore): every client's deleted clocks, adjacent ones joined, higher client
    /// ids first. An item under a deleted type counts as deleted, as ContentType.delete deleted it in Yjs.
    /// </summary>
    private void WriteDeleteSet(Lib0Writer encoder)
    {
        var deletedTypes = new Dictionary<YTreeType, bool>(ReferenceEqualityComparer.Instance);
        var clients = new List<(ulong Client, List<(uint Clock, uint Length)> Ranges)>();
        foreach (var (client, structs) in Tree.Clients)
        {
            var ranges = new List<(uint Clock, uint Length)>();
            for (var i = 0; i < structs.Count; i++)
            {
                if (!IsDeleted(structs[i])) continue;
                var clock = structs[i].Clock;
                var length = structs[i].Length;
                while (i + 1 < structs.Count && IsDeleted(structs[i + 1])) length += structs[++i].Length;
                ranges.Add((clock, length));
            }
            if (ranges.Count > 0) clients.Add((client, ranges));
        }
        encoder.WriteVarUint((ulong)clients.Count);
        foreach (var (client, ranges) in clients.OrderByDescending(entry => entry.Client))
        {
            encoder.WriteVarUint(client);
            encoder.WriteVarUint((ulong)ranges.Count);
            foreach (var (clock, length) in ranges)
            {
                encoder.WriteVarUint(clock);
                encoder.WriteVarUint(length);
            }
        }

        bool IsDeleted(YTreeItem item) => item.Deleted || (item.Parent is { } parent && IsTypeDeleted(parent));

        bool IsTypeDeleted(YTreeType type)
        {
            if (type.Item is not { } owner) return false;
            if (!deletedTypes.TryGetValue(type, out var deleted)) deletedTypes[type] = deleted = IsDeleted(owner);
            return deleted;
        }
    }

    /// <summary>yjs 13.6.33 ItemTextListPosition.</summary>
    private sealed class TextPosition(YTreeItem? left, YTreeItem? right, int index, OrderedDictionary<string, object?> currentAttributes)
    {
        public YTreeItem? Left { get; set; } = left;
        public YTreeItem? Right { get; set; } = right;
        public int Index { get; set; } = index;
        public OrderedDictionary<string, object?> CurrentAttributes { get; } = currentAttributes;
    }
}

/// <summary>A shared type created before it is integrated (new Y.XmlElement / new Y.XmlText), as y-prosemirror builds them.</summary>
internal abstract class YPrelim;

/// <summary>new Y.XmlElement(nodeName) with its prelim attributes (_prelimAttrs) and content (_prelimContent).</summary>
internal sealed class YPrelimElement(string nodeName) : YPrelim
{
    public string NodeName { get; } = nodeName;
    public JsObject Attributes { get; } = new();
    public List<YPrelim> Content { get; } = [];
}

/// <summary>new Y.XmlText() and the applyDelta calls it keeps pending (_pending).</summary>
internal sealed class YPrelimText : YPrelim
{
    public List<IReadOnlyList<YDeltaOp>> Pending { get; } = [];
}

/// <summary>One delta operation: an insert of text, or a retain of <see cref="Retain"/> characters; with attributes.</summary>
internal sealed record YDeltaOp(string? Insert, int Retain, JsObject? Attributes);
