namespace Atlas.Server.Features.Skills.Queries;

// Mapa znalostí: the reads (docs/skills-design.md); their results are in SkillContracts.

/// <summary>What one person (a directory contact) knows, as the reader may see it.</summary>
public sealed record GetContactSkills(Guid ContactId, bool AiScopeOnly = false);

/// <summary>Who knows a topic: <paramref name="Context"/> (a space, a tag or an organization, „Orion“) narrows the pages that count and
/// ranks its people first; <paramref name="Role"/> filters by the contact's function.</summary>
public sealed record FindExperts(string Topic, string? Context = null, string? Role = null, int Limit = 10, bool AiScopeOnly = false);

/// <summary>Who knows what as a graph, optionally only the topics of readable pages of one space.</summary>
public sealed record GetSkillGraph(Guid? SpaceId = null, bool AiScopeOnly = false);

/// <summary>Topics by part of a name or an alias, the closest first (the exact one, a prefix, then similar spellings).</summary>
public sealed record SuggestTopics(string? Query, bool AiScopeOnly = false);
