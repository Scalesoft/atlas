using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>A page: identity, placement and trash state. The title and the body live in PageRevision.</summary>
public class Page
{
    public virtual Guid Id { get; set; }
    public virtual Guid SpaceId { get; set; }
    public virtual Guid? ParentId { get; set; }
    public virtual Guid OwnerId { get; set; }
    public virtual int Version { get; set; } = 1;
    public virtual Guid? PublishedRevisionId { get; set; }
    public virtual int SortOrder { get; set; }
    public virtual bool IsTrashed { get; set; }
    [JsonIgnore]
    public virtual bool IsTemplate { get; set; }
    public virtual DateTime? TrashedAt { get; set; }
    public virtual Guid? TrashedById { get; set; }
    public virtual Guid? TrashBatchId { get; set; }
    /// <summary>Permanently deleted (unified trash spec §2): a tombstone in the trash that the immutable access history still names; its
    /// content is gone (PagePurge) and nothing lists or restores it.</summary>
    [JsonIgnore]
    public virtual DateTime? PurgedAt { get; set; }
    /// <summary>False: the space's default access applies. True: only the page's own grants (and the space Admin).</summary>
    /// <remarks>Not serialized with the entity (page.json readers of 1.0 reject unknown members): backup format 2 carries it as
    /// BackupPage.OwnAccess, written only when true, so archives without own-access pages stay format 1. Item grants are not backed
    /// up (like space grants): a restored own-access page stays restricted without grants; an existing page keeps its value and
    /// grants unless the restore moves it to another space (D7). See the WP3 plan, S4/S5.</remarks>
    [JsonIgnore]
    public virtual bool OwnAccess { get; set; }
    /// <summary>The template the page was created from (PageTemplateRelations); null before 3.1 or without one. Not serialized with the
    /// entity, as <see cref="OwnAccess"/>: the installation backup (Git, ZIP) carries the column, the wiki archive does not.</summary>
    [JsonIgnore]
    public virtual Guid? TemplatePageId { get; set; }
    /// <summary>3.6-I „Publikace“ of this page; null inherits the space's (<see cref="Spaces.Domain.Space.PublicationMode"/>). A setting,
    /// not content: not serialized with the entity, as <see cref="OwnAccess"/>; the installation backup carries the column.</summary>
    [JsonIgnore]
    public virtual PublicationMode? PublicationMode { get; set; }
    /// <summary>„Řadit abecedně“: the tree shows this page's direct children by title (PageOrganization TitleOrder). A setting of the tree,
    /// not serialized with the entity, as <see cref="OwnAccess"/>; the installation backup carries the column.</summary>
    [JsonIgnore]
    public virtual bool SortChildrenByTitle { get; set; }
}
