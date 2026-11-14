namespace Atlas.Server.Features.Collaboration.Contracts;

/// <param name="CanRead">3.6-I Živě: a reader of a live page joins the live document read-only (receives updates and presence; the hub
/// refuses its writes with [READ_ONLY]); a writer reads too.</param>
public sealed record CollaboratorAuthorization(bool CanWrite, string SecurityStamp, bool CanRead = false)
{
    public static readonly CollaboratorAuthorization Denied = new(false, "");
}
