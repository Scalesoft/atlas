using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using NHibernate;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.AiIndex;

/// <summary>
/// The real AddAi and AddAiIndex registrations over a disposable schema, with the fake provider (no model is ever called),
/// a fake clock and A3's real configuration cache and circuit breaker, which the test drives with probes. A wiki admin, a writer of two spaces; the worker and the maintenance are called directly.
/// </summary>
internal sealed class IndexFixture : IAsyncDisposable
{
    private IndexFixture(AtlasTestHost host, UserAccount admin, UserAccount writer, Guid spaceId, Guid otherSpaceId, FakeTimeProvider clock)
    {
        Host = host;
        Admin = admin;
        Writer = writer;
        SpaceId = spaceId;
        OtherSpaceId = otherSpaceId;
        Clock = clock;
        Pages = new TestPages(host);
    }

    public AtlasTestHost Host { get; }
    public UserAccount Admin { get; }
    public UserAccount Writer { get; }
    public Guid SpaceId { get; }
    public Guid OtherSpaceId { get; }
    public AiAvailability Availability => Host.Services.GetRequiredService<AiAvailability>();
    public AiConfigurationCache Cache => Host.Services.GetRequiredService<AiConfigurationCache>();
    public FakeTimeProvider Clock { get; }
    public TestPages Pages { get; }
    public FakeAiClientFactory Fake => Host.Services.GetRequiredService<FakeAiClientFactory>();
    public IndexRunner Runner => Host.Services.GetRequiredService<IndexRunner>();
    public IndexMaintenanceRunner Maintenance => Host.Services.GetRequiredService<IndexMaintenanceRunner>();

    /// <summary>Index-embedding calls of the fake, flattened to the texts that left Atlas.</summary>
    public IReadOnlyList<string> Embedded => Fake.Calls.Where(x => x.Task == AiTaskCatalog.IndexEmbed).SelectMany(x => x.Inputs).ToArray();

    public static async Task<IndexFixture> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        // Behind the database clock on purpose: the database stamps tasks and activations with its own now(), and nothing may
        // depend on the fake clock having passed it (with a head start, a test running longer than it found no due work). The
        // 24-hour retentions the tests step over keep an hour's margin either way.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-5));
        // Private (no sharedAs): the tests save the one embeddings profile and the AI switch (AiProfileAssignment, AiSetting), build and
        // activate the one index generation (IndexGeneration) and run the worker over the whole IndexTask queue on the host's fake clock.
        var host = await AtlasTestHost.CreateAsync("atlas_ai_index_", services =>
        {
            services.AddModuleSettings(new ConfigurationBuilder().Build());
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            configure?.Invoke(services);
        });
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var writer = await host.SeedUserAsync("writer");
        await host.GrantAiAsync(admin);
        await host.GrantAiAsync(writer);
        var space = await host.SeedSpaceAsync("Engineering", (writer, SpaceRole.Writer));
        var other = await host.SeedSpaceAsync("Archive", (writer, SpaceRole.Writer));
        return new IndexFixture(host, admin, writer, space, other, clock);
    }

    public static SaveAiProfileRequest Embeddings(string model = "text-embedding-bge-m3", string? expectedVersion = null, string? apiKey = null,
        string baseUrl = "http://lmstudio.example:1234/v1") => new(true, AiProvider.OpenAiCompatible, AiLocation.Company, baseUrl, model, null, 1024,
        "query: ", "passage: ", 30, 4, null, null, true, false, false, null, null, apiKey, false, expectedVersion);

    /// <summary>Embeddings profile saved, the master switch on, and the first probe passed (A3: a profile is unverified, so
    /// unavailable, until then).</summary>
    public async Task<AiProfileView> ConfigureAsync()
    {
        var profile = await As<AiProfileView>(Admin, new SaveAiProfile(AiProfileKind.Embeddings, Embeddings()));
        await As<AiSwitchView>(Admin, new SaveAiSwitch(new SaveAiSwitchRequest(true, null)));
        await ProbeAsync();
        return profile;
    }

    /// <summary>One round of A3's probes at the fake clock's time (the hosted probe service is not running in tests).</summary>
    public Task ProbeAsync() => Host.Services.GetRequiredService<AiProbeRunner>().RunOnceAsync(Clock.GetUtcNow(), default);

    /// <summary>One worker round that meets a timeout: the fake embedding call holds until the clock passes the profile's
    /// 30 s timeout. The fake must be in <see cref="FakeAiMode.Timeout"/>.</summary>
    public async Task<int> TimedOutRoundAsync()
    {
        var hold = Fake.Hold(AiProfileKind.Embeddings);
        var round = Runner.RunOnceAsync(Clock.GetUtcNow(), default);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        Clock.Advance(TimeSpan.FromSeconds(31));
        var finished = await round.WaitAsync(TimeSpan.FromSeconds(30));
        hold.Release();
        return finished;
    }

    public Task<AiProfileView> ProfileAsync() => As<AiProfileView>(Admin, new Atlas.Server.Features.Ai.Settings.Queries.GetAiProfile(AiProfileKind.Embeddings));

    /// <summary>
    /// The document of <paramref name="text"/> whose stored Markdown (the server writes it from this JSON) reads as the text: a
    /// "## Title" line is a heading (with the id <paramref name="headings"/> gives it), any other line a paragraph. Headings the
    /// text does not name come first.
    /// </summary>
    public static string Rich(string text, params (string Title, string Id)[] headings)
    {
        // "**x**" is bold text; the odd parts of the split are the bold ones.
        static string Inline(string line) => string.Join(",", line.Split("**").Select((part, i) => (part, i)).Where(x => x.part.Length > 0)
            .Select(x => "{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(x.part) + (x.i % 2 == 1 ? ",\"marks\":[{\"type\":\"bold\"}]" : "") + "}"));
        static string Heading(string title, string? id) => "{\"type\":\"heading\",\"attrs\":{\"level\":2" + (id is null ? "" : ",\"headingId\":" + JsonSerializer.Serialize(id))
            + "},\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(title) + "}]}";
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var titles = lines.Where(line => line.StartsWith("## ", StringComparison.Ordinal)).Select(line => line[3..]).ToHashSet();
        var nodes = headings.Where(h => !titles.Contains(h.Title)).Select(h => Heading(h.Title, h.Id))
            .Concat(lines.Select(line => line.StartsWith("## ", StringComparison.Ordinal)
                ? Heading(line[3..], headings.Where(h => h.Title == line[3..]).Select(h => h.Id).FirstOrDefault())
                : "{\"type\":\"paragraph\",\"content\":[" + Inline(line) + "]}"))
            .DefaultIfEmpty("{\"type\":\"paragraph\"}");
        return "{\"format\":\"atlas-rich\",\"version\":1,\"plugins\":[{\"id\":\"heading-anchors\",\"version\":1}],\"document\":{\"type\":\"doc\",\"content\":[" + string.Join(",", nodes) + "]}}";
    }

    /// <summary>The index tasks the outbox's delivery of a page's change enqueues (IndexTaskRevision); this host has no queue for it, so
    /// the test delivers each change itself, as it commits.</summary>
    public async Task DeliverAsync(Guid pageId, bool rebuild = false)
    {
        using var session = Host.OpenSession();
        using var transaction = session.BeginTransaction();
        await IndexTasks.EnqueuePageAsync(session, pageId, rebuild, default);
        await transaction.CommitAsync();
    }

    public async Task<PageDto> PublishNewAsync(string title, string markdown, Guid? spaceId = null, params (string Title, string Id)[] headings)
    {
        var page = await Pages.CreateAsync(new CreatePageRequest(spaceId ?? SpaceId, null, title, Rich(markdown, headings), markdown), Writer, default);
        var published = await Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), Writer, default);
        await DeliverAsync(page.Id);
        return published;
    }

    public async Task<PageDto> SaveDraftAsync(Guid id, string title, string markdown, params (string Title, string Id)[] headings)
    {
        var current = await Pages.GetAsync(id, Writer, default);
        return await Pages.SaveAsync(id, new SavePageRequest(title, Rich(markdown, headings), markdown, current.Version), Writer, default);
    }

    public async Task<PageDto> EditAndPublishAsync(Guid id, string title, string markdown, params (string Title, string Id)[] headings)
    {
        var saved = await SaveDraftAsync(id, title, markdown, headings);
        var published = await Pages.PublishAsync(id, new PublishPageRequest(saved.Version), Writer, default);
        await DeliverAsync(id);
        return published;
    }

    public Task<GenerationView> StartAsync(string mode, SaveAiProfileRequest? profile = null)
        => As<GenerationView>(Admin, new StartIndexGeneration(new StartIndexGenerationRequest(mode, profile)));

    public Task<IndexStatus> StatusAsync() => As<IndexStatus>(Admin, new Atlas.Server.Features.AiIndex.Queries.GetIndexStatus());

    public async Task<GenerationVerification> VerifyAsync(Guid generationId)
    {
        Host.Actor.Current = Admin;
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IndexVerificationRunner>().VerifyAsync(generationId, default);
    }

    public Task<IndexStatus> ActivateAsync(GenerationView generation)
        => As<IndexStatus>(Admin, new ActivateIndexGeneration(generation.Id, new ActivateIndexGenerationRequest(generation.Version)));

    public Task<int> RunAsync() => Runner.RunUntilIdleAsync(Clock.GetUtcNow(), default);

    /// <summary>When the index job's data schedule finds the next task (NextIndexTaskDue) at the fake clock's time.</summary>
    public Task<DateTime?> NextDueAsync() => Host.InvokeAsync<DateTime?>(new NextIndexTaskDue(Clock.GetUtcNow().UtcDateTime));

    /// <summary>Configured, built from the given pages, verified and active.</summary>
    public async Task<GenerationView> ActiveIndexAsync()
    {
        await ConfigureAsync();
        var generation = await StartAsync("build");
        await RunAsync();
        Assert.True((await VerifyAsync(generation.Id)).CanActivate);
        return (await ActivateAsync((await StatusAsync()).Building!)).Active!;
    }

    public async Task<T> As<T>(UserAccount actor, object message)
    {
        Host.Actor.Current = actor;
        var result = await Host.InvokeAsync<T>(message);
        if (message is Atlas.Server.Features.PageTemplates.SetPageTemplate template)
        {
            await DeliverAsync(template.PageId);
        }
        return result;
    }

    public async Task<IReadOnlyList<TaskRow>> TasksAsync(Guid? pageId = null)
    {
        using var session = Host.OpenSession();
        var rows = await session.CreateSQLQuery("""
            SELECT "GenerationId","PageId","Attempts","NextAttemptAt","LeasedUntil","LastErrorCode","EnqueuedAt" FROM "IndexTask"
            WHERE CAST(:page AS uuid) IS NULL OR "PageId"=:page ORDER BY "EnqueuedAt"
            """)
            .AddScalar("GenerationId", NHibernateUtil.Guid).AddScalar("PageId", NHibernateUtil.Guid).AddScalar("Attempts", NHibernateUtil.Int32)
            .AddScalar("NextAttemptAt", NHibernateUtil.UtcDateTime).AddScalar("LeasedUntil", NHibernateUtil.UtcDateTime)
            .AddScalar("LastErrorCode", NHibernateUtil.String).AddScalar("EnqueuedAt", NHibernateUtil.UtcDateTime)
            .SetParameter("page", pageId, NHibernateUtil.Guid).ListAsync<object[]>();
        return rows.Select(row => new TaskRow((Guid)row[0], (Guid)row[1], (int)row[2], (DateTime)row[3], (DateTime?)row[4], (string?)row[5], (DateTime)row[6])).ToArray();
    }

    /// <summary>The rows of a page in a generation: its revision, template state and chunk texts (null when it has no row).</summary>
    public async Task<IndexedRow?> IndexedAsync(Guid generationId, Guid pageId)
    {
        using var session = Host.OpenSession();
        var page = await session.CreateSQLQuery("""
            SELECT "RevisionId","IsTemplate","Centroid" IS NOT NULL AS centroid FROM "KnowledgePage" WHERE "GenerationId"=:g AND "PageId"=:p
            """)
            .AddScalar("RevisionId", NHibernateUtil.Guid).AddScalar("IsTemplate", NHibernateUtil.Boolean).AddScalar("centroid", NHibernateUtil.Boolean)
            .SetParameter("g", generationId).SetParameter("p", pageId).ListAsync<object[]>();
        if (page.Count == 0)
        {
            return null;
        }
        var chunks = await session.CreateSQLQuery("""
            SELECT "Text","HeadingId" FROM "KnowledgeChunk" WHERE "GenerationId"=:g AND "PageId"=:p ORDER BY "Ordinal"
            """)
            .AddScalar("Text", NHibernateUtil.String).AddScalar("HeadingId", NHibernateUtil.String)
            .SetParameter("g", generationId).SetParameter("p", pageId).ListAsync<object[]>();
        return new IndexedRow((Guid)page[0][0], (bool)page[0][1], (bool)page[0][2], chunks.Select(x => (string)x[0]).ToArray(),
            chunks.Select(x => (string?)x[1]).ToArray());
    }

    public async Task<Guid> PublishedRevisionAsync(Guid pageId)
    {
        using var session = Host.OpenSession();
        return (await session.GetAsync<Page>(pageId)).PublishedRevisionId!.Value;
    }

    public async Task<long> CountAsync(string sql)
    {
        using var session = Host.OpenSession();
        return Convert.ToInt64(await session.CreateSQLQuery(sql).UniqueResultAsync());
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    internal sealed record TaskRow(Guid GenerationId, Guid PageId, int Attempts, DateTime NextAttemptAt, DateTime? LeasedUntil, string? LastErrorCode,
        DateTime EnqueuedAt);

    internal sealed record IndexedRow(Guid RevisionId, bool IsTemplate, bool HasCentroid, IReadOnlyList<string> Chunks, IReadOnlyList<string?> HeadingIds);
}
