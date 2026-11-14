namespace Atlas.Server.Features.Boards.Domain;

/// <summary>
/// What a batch is applied to, as the handler read it under the board's row lock: the title, this board's items the batch
/// touches (BoardEditing.TouchedIds), the number of live items on the whole board, touched ids that belong to another board,
/// the page targets of the batch that the author may read, the settings the board changed (canonical, without defaults) and, for a
/// batch that changes parents (BoardEditing.ChangesParents), every parent link of the board's rows, deleted ones included (item →
/// its group or frame), which nesting is checked against; and the objects the batch places (BoardEditing.ObjectTargets) that the author
/// may read, with their types.
/// </summary>
public sealed record BoardState(
    string Title,
    IReadOnlyCollection<BoardItemState> Items,
    int LiveItemCount,
    IReadOnlySet<Guid> ForeignIds,
    IReadOnlySet<Guid> ReadablePages,
    string Settings = BoardProps.Empty,
    IReadOnlyDictionary<Guid, Guid>? Parents = null,
    IReadOnlyDictionary<Guid, string>? ReadableObjects = null);
