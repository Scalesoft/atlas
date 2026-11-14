namespace Atlas.Server.Features.Administration.Queries;

/// <summary>Session claims travel as plain strings so the handler never sees HttpContext.</summary>
public sealed record GetProfileAccount(string? AuthTime, string? AuthenticationMethod);
