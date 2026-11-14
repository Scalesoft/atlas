namespace Atlas.Server.Features.Comments.Domain;

/// <summary>
/// A comment on an object (comments spec §2): Markdown by its author on the object's flat timeline, optionally anchored at a place
/// inside it (a page heading, a board item). Only the author edits it; the author or a manager of the object deletes it, which keeps
/// the row so the timeline does not lie and removes its text for good.
/// </summary>
// shortcut: no edit history and no replies; decide from how comments are used (spec §1, §2).
public class Comment
{
    public virtual Guid Id { get; set; }
    /// <summary>The object (ContentResource) it belongs to, whichever view shows it.</summary>
    public virtual Guid ObjectId { get; set; }
    public virtual Guid AuthorId { get; set; }
    /// <summary>Atlas Markdown; empty once deleted.</summary>
    public virtual string Body { get; set; } = "";
    /// <summary>A heading id of a page or an item id of a board; null for the object as a whole.</summary>
    public virtual string? Anchor { get; set; }
    public virtual DateTime CreatedAt { get; set; }
    public virtual DateTime? EditedAt { get; set; }
    public virtual DateTime? DeletedAt { get; set; }
    public virtual Guid? DeletedById { get; set; }

    public virtual bool IsDeleted => DeletedAt is not null;

    public virtual bool MayEdit(Guid actorId) => !IsDeleted && AuthorId == actorId;

    /// <param name="manages">The actor manages the object (CommentGate).</param>
    public virtual bool MayDelete(Guid actorId, bool manages) => !IsDeleted && (AuthorId == actorId || manages);

    /// <param name="body">Validated by <see cref="CommentText.Body"/>.</param>
    public virtual void Edit(string body, DateTime at)
    {
        Body = body;
        EditedAt = at;
    }

    public virtual void Delete(Guid by, DateTime at)
    {
        Body = "";
        Anchor = null;
        DeletedAt = at;
        DeletedById = by;
    }
}
