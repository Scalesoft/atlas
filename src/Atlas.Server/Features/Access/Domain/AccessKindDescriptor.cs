using Atlas.Server.Features.Access.Contracts;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>What a kind is called and which levels of the staircase it uses, lowest first, each with what it allows.</summary>
public sealed record AccessKindDescriptor(string Id, string One, string Many, string Icon, string? Parent, string? Plugin,
    IReadOnlyList<AccessLevelInfo> Levels);
