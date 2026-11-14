using Atlas.Server.Features.Projector.Contracts;

namespace Atlas.Server.Features.Projector.Commands;

/// <summary>Marks or unmarks a page as a catalog of its space (spec §1.4); an editor of the page.</summary>
public sealed record SetCatalogPage(Guid PageId, bool IsCatalog);

/// <summary>A new page in <paramref name="SpaceId"/> with the source's content as the actor reads it, every requirement reset (spec §1.5).</summary>
public sealed record CreateDeploymentFromPage(Guid SourcePageId, Guid SpaceId, Guid? ParentId, string Title);

// The block commands (spec §4, wrapped by MCP in R4): each writes the page's working revision through the shared page save, at
// ExpectedVersion, and answers with the version after it.

public sealed record UpdateRequirement(Guid PageId, string BlockId, RequirementChange Fields, int ExpectedVersion);

/// <summary>Several <see cref="UpdateRequirement"/> changes in one save: one version step, all or none.</summary>
public sealed record UpdateRequirements(Guid PageId, IReadOnlyList<RequirementBlockChange> Updates, int ExpectedVersion);

/// <summary>The keys from the space's catalog with what they require and the page lacks, dependencies first, at one position.
/// <paramref name="ModuleKeys"/> add whole modules of the catalog, each under its heading (plan 3.4 §6).</summary>
public sealed record InsertRequirements(Guid PageId, IReadOnlyList<string> Keys, RequirementPosition Position, int ExpectedVersion,
    IReadOnlyList<string>? ModuleKeys = null);

/// <summary>One new block at a position: empty (a title paragraph, a new id), then given <paramref name="Fields"/> as
/// <see cref="UpdateRequirement"/> gives them, in the same save; empty fields leave it empty.</summary>
public sealed record CreateRequirement(Guid PageId, RequirementChange Fields, RequirementPosition Position, int ExpectedVersion);

/// <summary>The blocks of another page (a deployment or a preset) as the actor reads it, by key with what they require and the page
/// lacks (every definition of the source when <paramref name="Keys"/> is null or empty), dependencies first, reset as copies, at one
/// position: <see cref="InsertRequirements"/> with that page as the catalog.</summary>
public sealed record InsertFromDeployment(Guid PageId, Guid SourcePageId, IReadOnlyList<string>? Keys, RequirementPosition Position, int ExpectedVersion);

public sealed record MoveRequirement(Guid PageId, string BlockId, RequirementPosition Position, int ExpectedVersion);

/// <summary>Removes the block with its content and nested blocks.</summary>
public sealed record RemoveRequirement(Guid PageId, string BlockId, int ExpectedVersion);
