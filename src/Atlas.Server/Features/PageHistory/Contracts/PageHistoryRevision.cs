namespace Atlas.Server.Features.PageHistory.Contracts;

public sealed record PageHistoryRevision(Guid Id, Guid PageId, int Version, string Title, Guid AuthorId, string AuthorName,
    DateTime ActiveFrom, DateTime? ActiveTo, bool IsCurrent, bool IsPublished, DateTime? LastPublishedAt,
    string ContentJson, string Markdown)
{
    /// <summary>Change summary of the last publication of this revision; null when it had none.</summary>
    public string? ChangeSummary { get; init; }
    /// <summary>As PageHistoryItem: the channel of the change and of its last publication, the tokens only for their owner and wiki administrators.</summary>
    public string? Channel { get; init; }
    public ApiTokenRef? ApiToken { get; init; }
    public string? PublicationChannel { get; init; }
    public ApiTokenRef? PublicationApiToken { get; init; }
    /// <summary>As PageHistoryItem (3.6-C): the revision's version, its label and whether it is a change with AI.</summary>
    public Guid GroupId { get; init; }
    public string? Label { get; init; }
    public bool IsAi { get; init; }
}
