namespace Atlas.Server.Platform.Identity;

/// <summary>What a page gate found. The order of the values is the order of the checks: the first that applies is the state,
/// so a page that moved and is in the trash is Moved, and a trashed page the account may not see is Trashed.</summary>
public enum PageOpeningState
{
    /// <summary>No page has this id, or its row is gone by the time it is locked.</summary>
    Missing,

    /// <summary>The page is in another space than the one locked for it: it moved between the look at its space and the
    /// space lock, or it is not a page of the space that was entered.</summary>
    Moved,

    /// <summary>The page is in the trash. It is not decided for the account.</summary>
    Trashed,

    /// <summary>The account may not learn that the page exists (PageAccessOutcome.Missing).</summary>
    Hidden,

    /// <summary>The account reads the page but may not perform the operation (PageAccessOutcome.Forbidden).</summary>
    Forbidden,

    /// <summary>The page is locked, read as it is under its lock, and the operation is allowed.</summary>
    Open
}
