using Atlas.Server.Features.Projector.Contracts;

namespace Atlas.Server.Features.Projector.Queries;

/// <summary>The definitions of the published catalog pages of the space the actor may read, a repeated key once (spec §4).
/// <paramref name="Bodies"/> false leaves out each block's content (what a list needs, P2-6); <paramref name="Keys"/> keeps only those keys
/// (the bodies of the blocks a panel inserts).</summary>
public sealed record GetProjectorCatalog(Guid SpaceId, bool Bodies = true, IReadOnlyList<string>? Keys = null);

/// <summary>The catalog insertion of <paramref name="Keys"/> into a page that holds <paramref name="PresentKeys"/>
/// (<paramref name="NotApplicableKeys"/> of them marked not_applicable there), dependencies first.</summary>
public sealed record ResolveProjectorRequirements(Guid SpaceId, IReadOnlyList<string> Keys, IReadOnlyList<string> PresentKeys,
    IReadOnlyList<string>? NotApplicableKeys = null);

/// <summary>A page's blocks, summary and "waiting on", from its working revision for an editor and its publication otherwise.
/// <paramref name="RecordOpening"/> false for a caller that records the one opening of its call itself (S2-7).</summary>
public sealed record GetPageRequirements(Guid PageId, bool RecordOpening = true);

/// <param name="Filter"><c>missing</c> (also when null or empty), <c>inbound-missing</c> or <c>all</c>.</param>
public sealed record GetProjectorOverview(Guid SpaceId, string? Filter = null)
{
    public string Filter { get; } = string.IsNullOrEmpty(Filter) ? "missing" : Filter;
}

public sealed record GetProjectorWaiting(Guid SpaceId);

/// <summary>„Závisí na“ across pages (plan 3.1 §2.3 point 5): at most <see cref="Limit"/> keyed blocks of the space's deployment pages the
/// actor may read whose title, key or page title holds every word of <paramref name="Q"/> (case and diacritics aside), ordered as the overview;
/// the picker's own page (<paramref name="ExcludePageId"/>) is left out before the limit, so its blocks never crowd out the others.</summary>
public sealed record SearchSpaceRequirements(Guid SpaceId, string? Q, Guid? ExcludePageId = null)
{
    public const int Limit = 50;
}

/// <summary>The requirement definitions of one page as the actor reads it (a deployment or a preset to insert from, plan 3.1 §2.3),
/// a repeated key once, with bodies.</summary>
public sealed record GetPageDefinitions(Guid PageId);

/// <summary>The space's page templates the actor may list that hold requirement blocks: the deployment presets (plan 3.1 §2.3). Only
/// with <see cref="Details"/> (the settings page "Sady a katalog", MCP) each preset's content and the catalog are read for the summary,
/// products, modules and differences; the pickers need the light answer.</summary>
public sealed record GetProjectorPresets(Guid SpaceId, bool Details = false);

/// <summary>Every requirement of the space's deployment pages the actor may read, filtered: <paramref name="States"/> any of them,
/// <paramref name="Party"/> contained in the party or in the organization of its contact (case and diacritics aside), the date within <paramref name="DateFrom"/>–<paramref name="DateTo"/>
/// (YYYY-MM-DD, inclusive; undated rows out once either is given), <paramref name="Module"/> a catalog module's keys; one page of
/// <paramref name="Limit"/> (1–200) rows from <paramref name="Offset"/>, ordered as the overview.</summary>
/// <paramref name="PartyContactId"/> keeps the blocks whose party is that contact, whatever text they show.
public sealed record QueryRequirements(Guid SpaceId, IReadOnlyList<string>? States = null, string? Party = null, string? DateFrom = null, string? DateTo = null,
    string? Module = null, int Offset = 0, int Limit = 50, Guid? PartyContactId = null, string? Text = null);

/// <summary>A block's history read from the text, newest first (<see cref="Contracts.RequirementHistory"/>), in the revision the actor is
/// shown (404 for a page they may not read); at most <paramref name="Limit"/> entries, all without one.</summary>
public sealed record GetRequirementHistory(Guid PageId, string BlockId, int? Limit = null)
{
    public const int MaxLimit = 200;
}

/// <summary>The prerequisites of other pages (<c>key@pageId</c>) as the actor reads them: unknown when they may not.</summary>
public sealed record GetRequirementPrerequisites(IReadOnlyList<string> References)
{
    public const int MaxReferences = 500;
}

/// <summary>„Co to blokuje“: the blocks of other pages the actor may read that require block <paramref name="BlockId"/> of the page.</summary>
public sealed record GetRequirementDependents(Guid PageId, string BlockId);

/// <summary>The cycle block <paramref name="Key"/> of the page would close by requiring <paramref name="Requires"/>, through other pages,
/// over the page's revision the actor reads (an editor's working one); what a picker asks before it writes.</summary>
public sealed record CheckRequirementCycle(Guid PageId, string Key, IReadOnlyList<string> Requires);
