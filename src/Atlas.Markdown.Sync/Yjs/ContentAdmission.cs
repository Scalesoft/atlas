using Atlas.Yjs;

namespace Atlas.Markdown.Sync;

/// <summary>Verdict of an <see cref="IDocumentContentGuard"/>. A rejected payload is neither stored nor broadcast.</summary>
public sealed record ContentAdmission(ContentRejectionReason? Reason, string? Detail)
{
    public static ContentAdmission Accepted { get; } = new(null, null);

    public bool IsAccepted => Reason is null;

    public static ContentAdmission Rejected(ContentRejectionReason reason, string detail) => new(reason, detail);
}
