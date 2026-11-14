namespace Atlas.Server.Features.Access.Contracts;

/// <summary>One level a kind uses (discover, read, write or manage) and what it allows there, in the kind's own words.</summary>
public sealed record AccessLevelInfo(string Level, string Description);
