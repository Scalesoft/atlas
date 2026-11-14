namespace Atlas.Server.Features.PageHistory.Contracts;

/// <summary>What a version of the page changed against the version before it (3.6-C „Přehledy změn v historii“), computed when its
/// checkpoints are saved (RevisionVersions) so the history never downloads two revisions to count it. Lines are the Markdown's
/// non-blank lines, orientational: a moved line is no change.</summary>
public sealed record RevisionChanges
{
    public int Added { get; init; }
    public int Removed { get; init; }
    /// <summary>Changed top-level blocks of the document.</summary>
    public int Blocks { get; init; }
    /// <summary>Headings of the sections with a change, in document order (removed text under its old heading).</summary>
    public IReadOnlyList<string> Sections { get; init; } = [];
    /// <summary>Requirements whose state changed (state codes as in the document), every one: a block's history reads them.</summary>
    public IReadOnlyList<RequirementStateChange> States { get; init; } = [];
    /// <summary>The requirement blocks whose attributes or title this checkpoint changed against the revision right before it (not the
    /// version's start, so each author keeps their own blocks): who changed a block last reads the newest revision that names it.</summary>
    public IReadOnlyList<string> Touched { get; init; } = [];
    public int RequirementsAdded { get; init; }
    public int RequirementsRemoved { get; init; }
    public IReadOnlyList<string> ContactsAdded { get; init; } = [];
    public IReadOnlyList<string> ContactsRemoved { get; init; } = [];
    public int AttachmentsAdded { get; init; }
    public int AttachmentsRemoved { get; init; }
    /// <summary>The title before the version when it changed the title; null otherwise.</summary>
    public string? TitleBefore { get; init; }
    /// <summary>Who wrote the version's checkpoints (the saver and the people whose live updates it holds), with how many each: their share of it.</summary>
    public IReadOnlyList<RevisionContributor> Contributors { get; init; } = [];
}

/// <param name="BlockId">The block's id; null in a summary saved before it was kept.</param>
public sealed record RequirementStateChange(string Title, string From, string To, string? BlockId = null);

/// <summary>A person who wrote checkpoints of a version. The name is filled when the history is read, never stored.</summary>
public sealed record RevisionContributor(Guid Id, int Checkpoints)
{
    public string? Name { get; init; }
}
