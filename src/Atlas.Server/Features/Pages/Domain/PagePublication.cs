using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>One temporal publication of a revision. ActiveTo NULL is the published revision.</summary>
public class PagePublication
{
    public virtual Guid Id { get; set; } = Guid.NewGuid();
    public virtual Guid PageId { get; set; }
    public virtual Guid RevisionId { get; set; }
    public virtual Guid? PublisherId { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
    /// <summary>What the publication changed, as the publisher put it: one trimmed line of at most 500 characters, or null.</summary>
    /// <remarks>Not serialized with the entity (BackupPage.Publications is read with UnmappedMemberHandling.Disallow, so a 1.5 reader
    /// would reject the member): backup format 4 carries it as BackupPage.ChangeSummaries, written only when some summary exists.</remarks>
    [JsonIgnore]
    public virtual string? ChangeSummary { get; set; }
    /// <summary>Transport of the publication (ContentChannels), without a default like PageRevision.Channel: publishing creates no
    /// revision, so this is where an mcp publication is recorded. Never serialized: the backup format does not change, a restore is import.</summary>
    [JsonIgnore]
    public virtual string Channel { get; set; } = null!;
    /// <summary>The personal API token of an api or mcp publication; null otherwise. Never serialized: tokens are not backed up.</summary>
    [JsonIgnore]
    public virtual Guid? ApiTokenId { get; set; }
}
