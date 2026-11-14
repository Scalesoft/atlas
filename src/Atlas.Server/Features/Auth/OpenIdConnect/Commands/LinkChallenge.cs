namespace Atlas.Server.Features.Auth.OpenIdConnect.Commands;

public sealed record LinkChallenge(Guid UserId, string SecurityStamp);
