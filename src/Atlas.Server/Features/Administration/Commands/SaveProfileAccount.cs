using Atlas.Server.Features.Administration.Contracts;

namespace Atlas.Server.Features.Administration.Commands;

public sealed record SaveProfileAccount(string? AuthTime, string? AuthenticationMethod, UpdateOwnAccount Request);
