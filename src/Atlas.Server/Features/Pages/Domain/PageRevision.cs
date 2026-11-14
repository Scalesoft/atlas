namespace Atlas.Server.Features.Pages.Domain;

/// <summary>One temporal revision of a page's title and body. ActiveTo NULL is the current revision.</summary>
public class PageRevision
{
    public virtual Guid Id { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual int Version { get; set; }
    public virtual string Title { get; set; } = "";
    public virtual string ContentJson { get; set; } = "{}";
    public virtual string Markdown { get; set; } = "";
    public virtual Guid AuthorId { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
    /// <summary>Transport of the change (ContentChannels): deliberately without a default, so a path that forgets it fails on INSERT.</summary>
    public virtual string Channel { get; set; } = null!;
    /// <summary>The personal API token of an api or mcp change; null otherwise. The author is still the token owner.</summary>
    public virtual Guid? ApiTokenId { get; set; }
    /// <summary>Informational author-supplied flag for this revision only.</summary>
    public virtual bool AiAssisted { get; set; }
    /// <summary>The assistant conversation of an assistant change; null otherwise. With the channel and token it is the source of an AI version (3.6-C rule 5).</summary>
    public virtual Guid? AiConversationId { get; set; }

    // 3.6-C: checkpoints stay immutable revisions; the history groups them into versions (RevisionVersions). A revision placed by
    // no rule (creation, rename, import) is a version of its own, so its writer need not know about groups.
    private Guid m_groupId;
    private DateTime m_groupStartedAt;
    /// <summary>The version this revision belongs to: the id of its first revision.</summary>
    public virtual Guid GroupId { get => m_groupId == Guid.Empty ? Id : m_groupId; set => m_groupId = value; }
    public virtual DateTime GroupStartedAt { get => m_groupStartedAt == default ? ActiveFrom : m_groupStartedAt; set => m_groupStartedAt = value; }
    /// <summary>This revision's position in its version: the newest one counts the version's checkpoints.</summary>
    public virtual int CheckpointCount { get; set; } = 1;
    /// <summary>The version ends with this revision: an explicit action (restore, source, named version) or a large change.</summary>
    public virtual bool IsExplicit { get; set; }
    /// <summary>The description of a named version („Uložit verzi“); null otherwise.</summary>
    public virtual string? Label { get; set; }
    /// <summary>The newest revision of the previous version: what the version's changes are measured against. Null for the first.</summary>
    public virtual Guid? BaseRevisionId { get; set; }
    /// <summary>Its version ended exactly where it started (a character typed and deleted again): the listed revision whose title and
    /// document this one has. The history lists no such version (GetPageHistoryHandler); null otherwise, and for every revision before it.</summary>
    public virtual Guid? SameAsRevisionId { get; set; }
    /// <summary>RevisionChanges JSON of the version up to this revision against <see cref="BaseRevisionId"/>; null before 3.6.</summary>
    public virtual string? ChangeSummary { get; set; }
}
