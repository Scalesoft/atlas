using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Commands;

public sealed record CommitImport(Guid Id, ConfirmWikiImport Request)
{
    /// <summary>Request carries the review token capability secret; redact it independently of ConfirmWikiImport's own ToString() (e.g. message-bus tracing that logs this outer command directly).</summary>
    public override string ToString()
    {
        return $"CommitImport {{ Id = {Id}, Request = {Request} }}";
    }
}
