namespace Atlas.Server.Features.Skills.Commands;

// Kdo co umí: the writes (docs/skills-design.md); their requests and results are in SkillContracts.

public sealed record RateSkill(Guid ContactId, RateSkillRequest Request);

/// <summary>Merges topic <paramref name="TagId"/> into <paramref name="IntoId"/>: its name becomes an alias, its ratings and pages move.</summary>
public sealed record MergeTopics(Guid TagId, Guid IntoId);

/// <summary>Makes <paramref name="TagId"/> part of <paramref name="ParentId"/> (<paramref name="Kind"/> part_of) or related to it (related),
/// or (<paramref name="Remove"/>) not any more.</summary>
public sealed record LinkTopics(Guid TagId, Guid ParentId, bool Remove = false, string Kind = "part_of");
