using System.Text.Json.Nodes;

namespace Atlas.Server.Features.Entities.Queries;

/// <summary>The entity query of plan §14.3: entities of <paramref name="Kind"/> merged by identity within a scope, <see cref="PageSize"/> a page.</summary>
/// <param name="Scope"><c>instance</c> (the whole wiki), <c>space</c>, <c>subtree</c> (a page and everything under it) or <c>resource</c>;
/// all but the instance name their <paramref name="ScopeId"/>.</param>
/// <param name="Q">Words that one of the entity's readable occurrences has in some field, diacritics and case aside.</param>
/// <param name="All">The whole result at once (exports), at most <see cref="ExportLimit"/> entities, each with all its sources.</param>
/// <param name="Identity">Only the entity of this identity key, with all its sources (Zobrazit všechny výskyty).</param>
public sealed record ListEntities(string Kind, string Scope, Guid? ScopeId, string? Q, int Page = 1, bool All = false, string? Identity = null)
{
    public const int PageSize = 50;
    public const int ExportLimit = 5_000;
    /// <summary>The sources a listed entity carries (PERF2-1); all of them come with <see cref="Identity"/> or an export.</summary>
    public const int SourceLimit = 5;
    public static readonly string[] Scopes = ["instance", "space", "subtree", "resource"];
}

/// <summary>A page of merged entities; <see cref="Total"/> counts only entities with an occurrence the actor may read.</summary>
public sealed record EntityList(IReadOnlyList<EntityItem> Items, int Total, int Page, int PageSize);

/// <param name="Attributes">Merged from the readable occurrences only. A directory contact that holds the identity is the whole card:
/// pages naming the same person add sources, never fields or list items. Without one, the first page occurrence's fields (the newest
/// title order) and the lists (e-mails, phones) of all of them.</param>
/// <param name="Sources">The first <see cref="ListEntities.SourceLimit"/> in a list; all of them for one identity or in an export.</param>
/// <param name="SourceCount">All the readable sources.</param>
/// <param name="ContactId">The directory contact of the person (contacts spec §2): the one to reference from a page or a board; null when
/// it exists only as text in pages, which atlas_upsert_contact links.</param>
public sealed record EntityItem(string IdentityKey, string Kind, JsonObject Attributes, IReadOnlyList<EntitySource> Sources, int SourceCount, Guid? ContactId = null);

/// <summary>Where the entity occurs: a page (its revision the actor reads, the heading as anchor) or a directory contact.</summary>
/// <param name="RevisionKind"><c>working</c> only for a person the page's published revision lacks (its writers' draft); <c>published</c> otherwise.</param>
public sealed record EntitySource(Guid ResourceId, string ResourceType, string Title, Guid? SpaceId, string? SpaceName, string? Anchor, string RevisionKind);
