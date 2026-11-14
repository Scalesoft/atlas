namespace Atlas.Server.Features.PageHistory.Contracts;

public sealed record PageHistoryItem(Guid Id, int Version, string Title, Guid AuthorId, string AuthorName,
    DateTime ActiveFrom, DateTime? ActiveTo, bool IsCurrent, bool IsPublished, DateTime? LastPublishedAt)
{
    /// <summary>Change summary of the last publication of this revision (the one LastPublishedAt names); null when it had none.</summary>
    public string? ChangeSummary { get; init; }
    /// <summary>Transport of the change: web, api, mcp, import or system; null for a revision from before Atlas 2.0.</summary>
    public string? Channel { get; init; }
    /// <summary>The token of an api or mcp change, for its owner and wiki administrators only.</summary>
    public ApiTokenRef? ApiToken { get; init; }
    /// <summary>Transport of the last publication of this revision (the one LastPublishedAt names); null when unpublished or from before 2.0.</summary>
    public string? PublicationChannel { get; init; }
    public ApiTokenRef? PublicationApiToken { get; init; }
    /// <summary>3.6-C: the version (group of checkpoints) this item belongs to; in the version list the item is the version's newest revision.</summary>
    public Guid GroupId { get; init; }
    /// <summary>What the item's changes are measured against: the newest revision of the previous version (of the previous step in a
    /// version's steps); null for the page's first.</summary>
    public Guid? PreviousRevisionId { get; init; }
    /// <summary>When the version started; <see cref="ActiveFrom"/> is its last change.</summary>
    public DateTime StartedAt { get; init; }
    /// <summary>Saved checkpoints of the version (in a version's steps: this step's position in it).</summary>
    public int Checkpoints { get; init; } = 1;
    /// <summary>The description of a named version („Uložit verzi“).</summary>
    public string? Label { get; init; }
    /// <summary>The version ended by an explicit action (restore, source, import, a named version) or a large change.</summary>
    public bool IsExplicit { get; init; }
    /// <summary>A change with AI (MCP, the personal API, the assistant): <see cref="Channel"/> and <see cref="ApiToken"/> name its source,
    /// the author is the person behind it.</summary>
    public bool IsAi { get; init; }
    /// <summary>What the version changed; null for revisions from before 3.6.</summary>
    public RevisionChanges? Changes { get; init; }
}
