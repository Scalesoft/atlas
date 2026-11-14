namespace Atlas.Server.Features.Boards.Domain;

/// <summary>An item as the rules see it: a value, so a rejected batch cannot leave a half-changed one behind. Props is the
/// canonical JSON of the kind's props (BoardProps); ParentId the group or frame the item belongs to.</summary>
public sealed record BoardItemState(
    Guid Id,
    string Kind,
    double X,
    double Y,
    double Width,
    double Height,
    int Z,
    string? Text,
    string? Color,
    Guid? TargetResourceId,
    Guid? FromItemId,
    Guid? ToItemId,
    long Version,
    bool IsDeleted,
    string Props = BoardProps.Empty,
    Guid? ParentId = null);
