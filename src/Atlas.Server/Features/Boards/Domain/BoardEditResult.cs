namespace Atlas.Server.Features.Boards.Domain;

/// <summary>A batch applied: the new title and settings and every item it changed, stamped with the batch's sequence; or a
/// rejection (<see cref="BoardRejection"/>) with nothing changed.</summary>
public sealed record BoardEditResult(string? Rejection, string Title, IReadOnlyList<BoardItemState> Changed, int LiveItemCount,
    string Settings = BoardProps.Empty);
