namespace Atlas.Server.Features.PageSource.Domain;

/// <summary>Append-only page audit fact "page.source-edited" in PageAccessEvent, pinned to the revision the replace created. Insert only; the table forbids updates and deletes.</summary>
public class PageSourceEditEvent
{
    public const string SourceEdited = "page.source-edited";

    public virtual long Id { get; set; }
    public virtual Guid PageId { get; set; }
    public virtual Guid RevisionId { get; set; }
    public virtual Guid ActorId { get; set; }
    public virtual string Action { get; set; } = SourceEdited;
}
