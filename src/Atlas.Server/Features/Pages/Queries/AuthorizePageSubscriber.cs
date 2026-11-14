namespace Atlas.Server.Features.Pages.Queries;

/// <summary>A live subscriber of a page, decided for the account its connection authenticated with (not a request actor).</summary>
public sealed record AuthorizePageSubscriber(Guid PageId, Guid UserId, string SecurityStamp, Guid? SessionId);
