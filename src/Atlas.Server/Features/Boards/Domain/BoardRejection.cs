namespace Atlas.Server.Features.Boards.Domain;

/// <summary>The codes of a refused batch (Problem Details <c>code</c>): 400 for an invalid change, 409 for the others.</summary>
public static class BoardRejection
{
    public const string Invalid = "invalid_board_change";
    public const string ItemExists = "item_exists";
    public const string ItemMissing = "item_missing";
    public const string ItemDeleted = "item_deleted";
}
