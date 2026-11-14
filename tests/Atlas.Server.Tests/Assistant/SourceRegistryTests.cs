using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.Assistant.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>The registry without a database (AI-11): unknown types and versions are refused, adapters are merged by rank and
/// every operation goes to the adapter of the reference's type and version only.</summary>
public sealed class SourceRegistryTests
{
    private static readonly UserAccount Reader = new() { Id = Guid.NewGuid(), Username = "reader", DisplayName = "reader" };

    [Fact]
    public async Task Unknown_type_or_version_is_unavailable()
    {
        var notes = new SyntheticNotes();
        var note = new SyntheticNote("n-1", "Poznámka", "Heslo k wifi mění IT.", 3, new HashSet<Guid> { Reader.Id }, 0.5);
        notes.Put(note);
        var registry = new SourceRegistry([new SyntheticNoteAdapter(notes, new Actor(Reader))]);
        var known = SyntheticNoteAdapter.Reference(note);
        var otherVersion = known with { Version = 2 };
        var unknownType = new SourceReference("board", 1, "b-1", "1", null);
        var page = new SourceReference(PageSourceAdapter.SourceType, PageSourceAdapter.SourceVersion, Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), null);
        SourceReference[] refs = [known, otherVersion, unknownType, page, known];

        var authorized = await registry.AuthorizeAsync(refs, true, default);
        var status = await registry.StatusAsync(refs, default);
        var texts = await registry.LoadTextAsync(refs, default);

        Assert.Equal(4, authorized.Count);
        Assert.Equal(SourceStatus.Available, authorized[known]);
        Assert.All([otherVersion, unknownType, page], reference =>
        {
            Assert.Equal(SourceStatus.Unavailable, authorized[reference]);
            Assert.Equal(SourceStatus.Unavailable, status[reference]);
            Assert.Null(texts[reference]);
        });
        Assert.Equal("Heslo k wifi mění IT.", texts[known]);
        // Stored columns of a type or version no adapter serves name no source; a new revision of the note is "changed".
        Assert.Null(registry.Reference(new SourceColumns("board", 1, null, null, "b-1", "1", null, null, null)));
        Assert.Null(registry.Reference(registry.Columns(new AuthorizedSource(known, "Poznámka", null, null, null, "", 1, null)) with { SourceVersion = 2 }));
        Assert.Equal(known, registry.Reference(registry.Columns(new AuthorizedSource(known, "Poznámka", null, null, null, "", 1, null))));
        notes.Put(note with { Revision = 4 });
        Assert.Equal(SourceStatus.Changed, (await registry.AuthorizeAsync([known], true, default))[known]);
        Assert.Equal(SourceStatus.Available, (await registry.AuthorizeAsync([known], false, default))[known]);
        Assert.Null((await registry.LoadTextAsync([known], default))[known]);
        notes.Revoke("n-1", Reader.Id);
        Assert.Equal(SourceStatus.Unavailable, (await registry.StatusAsync([known], default))[known]);
        // Two adapters for one type and version are a configuration error, not a choice.
        Assert.Throws<InvalidOperationException>(() => new SourceRegistry([new SyntheticNoteAdapter(notes, new Actor(Reader)), new SyntheticNoteAdapter(notes, new Actor(Reader))]));
    }

    [Fact]
    public async Task Retrieval_merges_every_adapter_by_rank_and_takes_the_limit()
    {
        var notes = new SyntheticNotes();
        notes.Put(new SyntheticNote("n-high", "Vysoká", "wifi heslo", 1, new HashSet<Guid> { Reader.Id }, 0.9));
        notes.Put(new SyntheticNote("n-low", "Nízká", "wifi síť", 1, new HashSet<Guid> { Reader.Id }, 0.2));
        notes.Put(new SyntheticNote("n-hidden", "Cizí", "wifi tajné", 1, new HashSet<Guid>(), 1.0));
        var pages = new StubAdapter([0.7, 0.4]);
        var registry = new SourceRegistry([pages, new SyntheticNoteAdapter(notes, new Actor(Reader))]);

        var merged = await registry.RetrieveAsync(new AnswerRetrievalRequest(Reader, "wifi", AnswerScope.Wiki, null, 3, 2), default);
        var hybrid = await registry.RetrieveAsync(new AnswerRetrievalRequest(Reader, "wifi", AnswerScope.Wiki, new SemanticQuery(Guid.NewGuid(), [1f]), 6, 2), default);

        Assert.Equal(["n-high", "stub-0", "stub-1"], merged.Sources.Select(source => source.Reference.Id));
        Assert.Equal(AnswerRetrieval.Lexical, merged.Retrieval);
        Assert.Equal(["n-high", "stub-0", "stub-1", "n-low"], hybrid.Sources.Select(source => source.Reference.Id));
        Assert.Equal(AnswerRetrieval.Hybrid, hybrid.Retrieval);
    }

    private sealed class Actor(UserAccount account) : ICurrentActor
    {
        public Task<UserAccount> RequireAsync(CancellationToken ct) => Task.FromResult(account);

        public ClaimedActor Claimed() => new(account.Id, account.SecurityStamp);
    }

    /// <summary>Another type with fixed ranks, standing in for pages.</summary>
    private sealed class StubAdapter(double[] ranks) : IKnowledgeSourceAdapter
    {
        public string Type => "stub";
        public int Version => 1;

        public Task<IReadOnlyList<AuthorizedSource>> RetrieveAsync(AnswerRetrievalRequest request, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AuthorizedSource>>(ranks.Take(request.Take)
                .Select((rank, i) => new AuthorizedSource(new SourceReference(Type, Version, "stub-" + i, "1", null), "Stub", null, null, null, "", rank, null))
                .ToArray());

        public Task<IReadOnlyDictionary<SourceReference, string?>> LoadTextAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> AuthorizeAsync(IReadOnlyList<SourceReference> refs, bool requireCurrentRevision, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> StatusAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<SourceReference, SourceDescription?>> DescribeAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct) => throw new NotSupportedException();
        public SourceColumns Columns(AuthorizedSource source) => throw new NotSupportedException();
        public SourceReference? Reference(SourceColumns columns) => null;
    }
}
