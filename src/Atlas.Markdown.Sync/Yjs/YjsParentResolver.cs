namespace Atlas.Markdown.Sync;

internal enum YParentKind : byte
{
    /// <summary>Yjs garbage-collects the struct on integration (it links to a GC struct or a non-type parent).</summary>
    Gc = 0,
    /// <summary>A named root type (<see cref="YParent.Root"/>).</summary>
    Root = 1,
    /// <summary>A shared type already in the document index (<see cref="YParent.Id"/> is its type id).</summary>
    Type = 2,
    /// <summary>A shared type created by the same payload (<see cref="YParent.Id"/> is its struct index).</summary>
    PayloadType = 3,
}

/// <summary>Where a struct integrates: its parent type and, for a map entry (an element attribute), the key.</summary>
internal readonly record struct YParent(YParentKind Kind, int Id, string? Root, string? Sub)
{
    public static YParent Gc { get; } = new(YParentKind.Gc, 0, null, null);
}

/// <summary>
/// Resolves where every struct of a payload integrates, with the semantics of Yjs <c>Item.getMissing</c>: an item with
/// an origin inherits parent and key from its left neighbour, otherwise from its right neighbour, otherwise it names
/// them; a neighbour that is a GC struct, or a parent that is not a live shared type, makes Yjs garbage-collect it.
/// Anything Yjs would park as pending is rejected instead: clock gaps, references to unknown clocks, a reference to a
/// later struct of the same client and reference cycles between clients. Delete ranges must stay inside the known
/// state. Clocks already in the index resolve against the index (Yjs ignores the re-sent part of a payload).
/// </summary>
internal static class YjsParentResolver
{
    private const int NoDependency = -2;
    private const int IndexedDependency = -1;
    private const byte Unvisited = 0;
    private const byte Visiting = 1;
    private const byte Resolved = 2;

    public static YParent[] Resolve(YjsUpdate update, YjsDocumentIndex index)
    {
        var structs = update.Structs;
        foreach (var range in update.Clients)
            if (range.Count > 0 && range.Start > index.GetClock(range.Client))
                throw new ContentRejectedException(ContentRejectionReason.ClockGap,
                    $"client {range.Client} starts at {range.Start}, known {index.GetClock(range.Client)}");
        var dependencies = new int[structs.Length * 3];
        for (var item = 0; item < structs.Length; item++)
        {
            ref readonly var current = ref structs[item];
            dependencies[item * 3] = Dependency(update, index, item, current.Origin);
            dependencies[item * 3 + 1] = Dependency(update, index, item, current.RightOrigin);
            dependencies[item * 3 + 2] = Dependency(update, index, item, current.ParentId);
        }
        var parents = new YParent[structs.Length];
        var state = new byte[structs.Length];
        var stack = new Stack<int>();
        for (var item = 0; item < structs.Length; item++)
        {
            if (state[item] != Unvisited) continue;
            state[item] = Visiting;
            stack.Push(item);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                var pending = PendingDependency(structs, dependencies, state, top);
                if (pending >= 0)
                {
                    if (state[pending] == Visiting)
                        throw new ContentRejectedException(ContentRejectionReason.UnknownReference, "cyclic references between clients");
                    state[pending] = Visiting;
                    stack.Push(pending);
                    continue;
                }
                parents[top] = Compute(update, index, dependencies, parents, top);
                state[top] = Resolved;
                stack.Pop();
            }
        }
        foreach (var range in update.DeleteSet)
        {
            var known = index.GetClock(range.Client);
            if (update.TryGetClient(range.Client, out var own) && own.Count > 0) known = Math.Max(known, own.End);
            if ((ulong)range.Clock + range.Length > known)
                throw new ContentRejectedException(ContentRejectionReason.DeleteOutOfRange,
                    $"delete {range.Client}:{range.Clock}+{range.Length} beyond {known}");
        }
        return parents;
    }

    private static int Dependency(YjsUpdate update, YjsDocumentIndex index, int item, YId? reference)
    {
        if (reference is not { } id) return NoDependency;
        if (id.Clock < index.GetClock(id.Client)) return IndexedDependency;
        var target = update.FindStruct(id);
        if (target < 0)
            throw new ContentRejectedException(ContentRejectionReason.UnknownReference, $"reference to unknown id {id.Client}:{id.Clock}");
        if (target >= item && update.Structs[target].Client == update.Structs[item].Client)
            // Yjs integrates a client's structs in clock order, so this would wait forever.
            throw new ContentRejectedException(ContentRejectionReason.UnknownReference, $"forward reference to {id.Client}:{id.Clock}");
        return target;
    }

    private static int PendingDependency(YStruct[] structs, int[] dependencies, byte[] state, int item)
    {
        // A struct also waits for its client's previous struct, as in the Yjs integration loop.
        if (item > 0 && structs[item - 1].Client == structs[item].Client && state[item - 1] != Resolved) return item - 1;
        for (var slot = 0; slot < 3; slot++)
        {
            var dependency = dependencies[item * 3 + slot];
            if (dependency >= 0 && state[dependency] != Resolved) return dependency;
        }
        return -1;
    }

    private static YParent Compute(YjsUpdate update, YjsDocumentIndex index, int[] dependencies, YParent[] parents, int item)
    {
        ref readonly var current = ref update.Structs[item];
        if (current.Content == YContentKind.Gc) return YParent.Gc;
        if (current.Origin is { } origin)
        {
            var left = Locate(update, index, dependencies[item * 3], parents, origin);
            if (left.Kind == YParentKind.Gc ||
                current.RightOrigin is { } right && Locate(update, index, dependencies[item * 3 + 1], parents, right).Kind == YParentKind.Gc)
                return YParent.Gc;
            return left;
        }
        if (current.RightOrigin is { } rightOrigin) return Locate(update, index, dependencies[item * 3 + 1], parents, rightOrigin);
        if (current.ParentRoot is { } root) return new YParent(YParentKind.Root, 0, root, current.ParentSub);
        var dependency = dependencies[item * 3 + 2];
        if (dependency >= 0)
        {
            ref readonly var parent = ref update.Structs[dependency];
            return parent.Content == YContentKind.Type && parents[dependency].Kind != YParentKind.Gc
                ? new YParent(YParentKind.PayloadType, dependency, null, current.ParentSub)
                : YParent.Gc;
        }
        index.TryLocate(current.ParentId!.Value, out var located, out var typeId);
        return located.Kind != YParentKind.Gc && typeId >= 0 ? new YParent(YParentKind.Type, typeId, null, current.ParentSub) : YParent.Gc;
    }

    /// <summary>The parent a referenced neighbour integrated under; <see cref="YParent.Gc"/> if it is a GC struct.</summary>
    private static YParent Locate(YjsUpdate update, YjsDocumentIndex index, int dependency, YParent[] parents, YId id)
    {
        if (dependency >= 0) return update.Structs[dependency].Content == YContentKind.Gc ? YParent.Gc : parents[dependency];
        index.TryLocate(id, out var located, out _);
        return located;
    }
}
