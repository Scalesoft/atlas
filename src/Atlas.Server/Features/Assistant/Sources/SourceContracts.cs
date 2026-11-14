using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Assistant.Sources;

/// <summary>
/// A stable reference to citable text (spec RAG §1, AI-11): the adapter's type and version, the source's identity, the exact
/// revision and an optional anchor inside it. The URL is not the identity. Only the adapter of <see cref="Type"/> and
/// <see cref="Version"/> interprets <see cref="Id"/>, <see cref="Revision"/> and <see cref="Anchor"/>; to everything else they
/// are opaque. Two references are equal when all five parts are, so a reference names one citable piece of text: for the
/// page adapter one chunk of one published revision in one index generation (<see cref="PageSourceAdapter"/>).
/// </summary>
public sealed record SourceReference(string Type, int Version, string Id, string Revision, string? Anchor);

/// <summary>
/// An authorized source: only what the actor may read right now. <see cref="Text"/> goes into the prompt, the rest into the view
/// and the dependency. <see cref="Rank"/> is the adapter's relevance, higher first, comparable across adapters only roughly:
/// the common flow merges by it (<see cref="SourceRegistry.RetrieveAsync"/>). <see cref="HeadingId"/> is the section anchor of
/// the view (<c>AiSourceView.HeadingId</c>); it is not part of the identity, which may name several chunks of one section.
/// </summary>
public sealed record AuthorizedSource(SourceReference Reference, string Title, string? SpaceName, Guid? SpaceId,
    string? HeadingTitle, string Text, double Rank, ChunkLocator? Chunk, string? HeadingId = null);

/// <summary>What the UI shows of a stored source the actor may read now (<see cref="IKnowledgeSourceAdapter.DescribeAsync"/>): the
/// current title, the page and space of a page source (null for other types), and the section heading while the source is the
/// revision the answer used.</summary>
public sealed record SourceDescription(string Title, Guid? PageId, Guid? SpaceId, string? SpaceName, string? HeadingTitle);

/// <summary>The chunk of the knowledge index a page source was read from (the typed columns of its dependency).</summary>
public sealed record ChunkLocator(Guid GenerationId, Guid PageId, Guid RevisionId, int Ordinal);

/// <summary>
/// <see cref="Available"/>: the actor may read the exact revision now. <see cref="Changed"/>: the actor may read the source, but it
/// has a newer published revision. <see cref="Unavailable"/>: anything else, including an unknown type or version (spec RAG §1:
/// a source no adapter vouches for is refused), a deleted source, lost access, the trash, an unpublished page, a template and a
/// space excluded from AI.
/// </summary>
public enum SourceStatus { Available, Changed, Unavailable }

/// <summary>
/// The scope of a question (plan phase 3 R3); it only narrows. <c>wiki</c>: everything the actor may read; <c>space</c>: one space
/// (<see cref="SpaceId"/>); <c>page</c>: the published revision of one page (<see cref="PageId"/>), with or without a match.
/// </summary>
public sealed record AnswerScope(string Kind, Guid? SpaceId, Guid? PageId)
{
    public const string WikiKind = "wiki";
    public const string SpaceKind = "space";
    public const string PageKind = "page";

    public static readonly AnswerScope Wiki = new(WikiKind, null, null);

    public static AnswerScope ForSpace(Guid spaceId) => new(SpaceKind, spaceId, null);

    public static AnswerScope ForPage(Guid pageId, Guid? spaceId = null) => new(PageKind, spaceId, pageId);
}

/// <summary>One retrieval of the preparation transaction (plan phase 3 §7 step 5): <see cref="Actor"/> is the account
/// <c>IContentAccess.LockActorAsync</c> returned, <see cref="Vector"/> the question's embedding made by the endpoint before the
/// transaction (<see cref="ISemanticQuery.TryEmbedAsync(string, TimeSpan, CancellationToken)"/>; null = lexical).
/// <see cref="Take"/> sources in all, at most <see cref="PerSource"/> of one source (one page; a focused short-section window of a
/// page takes up to four adjacent chunks regardless), out of <see cref="Pages"/> pages considered.</summary>
public sealed record AnswerRetrievalRequest(UserAccount Actor, string Question, AnswerScope Scope, SemanticQuery? Vector, int Take, int PerSource,
    int Pages = PageSourceAdapter.PagesConsidered)
{
    public static AnswerRetrievalRequest For(UserAccount actor, string question, AnswerScope scope, SemanticQuery? vector, Answering.AnswerEffort effort)
        => new(actor, question, scope, vector, effort.Take, effort.PerPage, effort.Pages);

    /// <summary>The rerank pool of an answer: <see cref="AnswerRerank.Depth"/> chunks out of twice the effort's pages, at most the effort's
    /// chunks of a page; the reranker picks the effort's <see cref="Answering.AnswerEffort.Take"/> of them.</summary>
    public static AnswerRetrievalRequest Pool(UserAccount actor, string question, AnswerScope scope, SemanticQuery? vector, Answering.AnswerEffort effort)
        => new(actor, question, scope, vector, AnswerRerank.Depth, effort.PerPage, effort.Pages * 2);
}

/// <summary>What the common flow stores of a retrieval: the merged sources in prompt order ([1] first) and how they were found
/// (<see cref="AnswerRetrieval"/>; the message's <c>Retrieval</c> column).</summary>
public sealed record AnswerSourceSet(IReadOnlyList<AuthorizedSource> Sources, string Retrieval);

public static class AnswerRetrieval
{
    public const string Hybrid = "hybrid";
    public const string Lexical = "lexical";
}

/// <summary>
/// The typed columns of a stored source dependency (<c>AiSourceDependency</c>, plan phase 3 R4): the type <c>page</c> keeps the
/// real FK <see cref="PageId"/>/<see cref="RevisionId"/> and its chunk (<see cref="GenerationId"/>, <see cref="ChunkOrdinal"/>);
/// every other type has <see cref="ExternalKey"/> and <see cref="ExternalRevision"/> only. <see cref="HeadingId"/> is the view's
/// anchor. The adapter maps both ways (<see cref="IKnowledgeSourceAdapter.Columns"/>, <see cref="IKnowledgeSourceAdapter.Reference"/>),
/// so the common flow never branches on the type.
/// </summary>
public sealed record SourceColumns(string SourceType, int SourceVersion, Guid? PageId, Guid? RevisionId, string? ExternalKey,
    string? ExternalRevision, Guid? GenerationId, int? ChunkOrdinal, string? HeadingId);

/// <summary>
/// A trusted server adapter of one source type (spec RAG §1, AI-11): extraction and authorization of its sources belong to it,
/// the common flow (retrieval merge, prompt, citation validation, revocation gate, history) does not change for a new type.
/// No generic plugin registry: the adapters are a list from DI (<see cref="SourceRegistry"/>).
/// </summary>
public interface IKnowledgeSourceAdapter
{
    /// <summary>The type of <see cref="SourceReference.Type"/>, e.g. <c>page</c>.</summary>
    string Type { get; }

    /// <summary>The version of <see cref="SourceReference.Version"/>; a stored reference of another version is refused.</summary>
    int Version { get; }

    /// <summary>Retrieval in the scope with the access rule inside. Called in the preparation transaction after
    /// <c>IContentAccess.LockActorAsync</c>; at most <see cref="AnswerRetrievalRequest.Take"/> sources in relevance order.</summary>
    Task<IReadOnlyList<AuthorizedSource>> RetrieveAsync(AnswerRetrievalRequest request, CancellationToken ct);

    /// <summary>The text of exactly the stored references (step 2 of the answer, the prompt), only while it is current: null when
    /// the text is gone or no longer the source's current revision. No access decision: call it after
    /// <see cref="AuthorizeAsync"/> or <see cref="StatusAsync"/> in the same transaction. Every reference given has an entry.</summary>
    Task<IReadOnlyDictionary<SourceReference, string?>> LoadTextAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct);

    /// <summary>The check before the model and in the revocation gate (plan phase 3 R2 steps 1–3): locks what the decision needs
    /// for the rest of the transaction and decides. With <paramref name="requireCurrentRevision"/> (a <c>context</c> dependency) a
    /// newer published revision is <see cref="SourceStatus.Changed"/>; without it (a <c>history</c> dependency: access and
    /// publication only) it is <see cref="SourceStatus.Available"/>. Called in a transaction. Every reference given has an entry.</summary>
    Task<IReadOnlyDictionary<SourceReference, SourceStatus>> AuthorizeAsync(IReadOnlyList<SourceReference> refs, bool requireCurrentRevision, CancellationToken ct);

    /// <summary>The status for reading history (plan phase 3 R4, Q4): in batch, under the readers' shared locks only, never a
    /// write lock. A newer published revision is <see cref="SourceStatus.Changed"/>. Every reference given has an entry.</summary>
    Task<IReadOnlyDictionary<SourceReference, SourceStatus>> StatusAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct);

    /// <summary>What the UI shows of stored references (plan phase 3 Q4: reading history, the view of a completed answer): the
    /// current title, the space and the section. It decides nothing: call it only for references <see cref="StatusAsync"/> or
    /// <see cref="AuthorizeAsync"/> found <see cref="SourceStatus.Available"/> or <see cref="SourceStatus.Changed"/> in the same
    /// transaction. Of a source with a newer revision only what the current revision shows (never a heading or text of the older
    /// one). Every reference given has an entry; null = nothing to show any more.</summary>
    Task<IReadOnlyDictionary<SourceReference, SourceDescription?>> DescribeAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct);

    /// <summary>The typed dependency columns of a source this adapter returned.</summary>
    SourceColumns Columns(AuthorizedSource source);

    /// <summary>The reference of stored dependency columns of this adapter's type and version; null when they no longer name a
    /// source (a deleted page revision nulls the page columns), which makes the dependency unavailable.</summary>
    SourceReference? Reference(SourceColumns columns);
}
