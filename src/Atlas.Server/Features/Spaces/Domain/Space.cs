using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>A space: the unit of access. Metadata changes are versioned into SpaceMetadataRevision with the row (ContentRowEffects).</summary>
public class Space
{
    public virtual Guid Id { get; set; }
    public virtual string Name { get; set; } = "";
    public virtual string Icon { get; set; } = "book-open";
    public virtual string Color { get; set; } = "blue";
    public virtual string Description { get; set; } = "";
    /// <remarks>Not serialized with the entity: space.json readers of 1.0 reject unknown members. Like space grants, a sidebar
    /// setting is not content, so a backup leaves it out: a space the restore creates starts with None, an existing one keeps its rule.</remarks>
    [JsonIgnore]
    public virtual SpacePinPolicy PinPolicy { get; set; } = SpacePinPolicy.None;
    /// <summary>In the trash since (2026-10-27 unified trash spec): the space is then found by nobody but the space trash, and
    /// its pages trashed with it share <see cref="TrashBatchId"/>. The ZIP backup carries it in spaces/&lt;id&gt;/trash.json.</summary>
    [JsonIgnore]
    public virtual DateTime? TrashedAt { get; set; }
    [JsonIgnore]
    public virtual Guid? TrashedById { get; set; }
    [JsonIgnore]
    public virtual Guid? TrashBatchId { get; set; }
    /// <summary>Permanently deleted (PurgeSpaceHandler): a tombstone of the trash that the access history still names.</summary>
    [JsonIgnore]
    public virtual DateTime? PurgedAt { get; set; }
    [JsonIgnore]
    public virtual bool IsTrashed => TrashedAt is not null;
    /// <summary>Space setting „Komentáře“ (comments spec §2): whether readers may post new comments on its objects.</summary>
    [JsonIgnore]
    public virtual bool CommentsEnabled { get; set; } = true;
    /// <summary>3.6-I: the „Publikace“ its pages inherit (how pages are published in the space); a page may have its own.</summary>
    [JsonIgnore]
    public virtual Pages.Domain.PublicationMode PublicationMode { get; set; } = Pages.Domain.PublicationMode.Manual;
    /// <summary>5.2 „Ověřování obsahu“: the interval (3, 6, 12 months) its pages inherit; null = verification is off in the space.</summary>
    [JsonIgnore]
    public virtual short? VerificationIntervalMonths { get; set; }
}
