using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.ContentImport;
using Atlas.Server.Features.ContentImport.Commands;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.ContentImport;

internal sealed class ContentImportFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;
    private readonly string? m_directory;

    private ContentImportFixture(AtlasTestHost host, string? directory)
    {
        m_host = host;
        m_directory = directory;
    }

    public AtlasTestHost Host => m_host;
    public string AttachmentDirectory => m_host.Services.GetRequiredService<AttachmentFiles>().Root;
    public TestCollaboration Collaboration { get; private set; } = null!;
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Other { get; private set; } = null!;
    /// <summary>The import source of this test (WikiImportOptions.SourceKey): a source's pages, checkpoints and destination are its own.</summary>
    public string Source { get; } = "wiki-" + Guid.NewGuid().ToString("N")[..12];

    /// <param name="own">A host of the test's own instead of the one ContentImportHandlerTests share; their tests run one at a time,
    /// so a test there may compare whole tables before and after its import.</param>
    public static async Task<ContentImportFixture> CreateAsync(bool own = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "atlas_import_" + Guid.NewGuid().ToString("N"));
        var host = await AtlasTestHost.CreateAsync("atlas_import_", services =>
        {
            Directory.CreateDirectory(directory);
            var configuration = new ConfigurationBuilder().Build();
            var environment = new AttachmentTests.EnvironmentStub(directory);
            services.AddSingleton(new ContentImportFiles(configuration, environment));
            services.AddSingleton(new AttachmentFiles(configuration, environment));
        }, sharedAs: own ? null : "content-import");
        // The folder of the shared host lives as long as the host, that is the test process.
        var fixture = new ContentImportFixture(host, own ? directory : null);
        fixture.Collaboration = new TestCollaboration(host);
        fixture.Admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        fixture.Other = await host.SeedUserAsync("reader");
        return fixture;
    }

    public Task<T> As<T>(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync<T>(message);
    }

    public Task As(UserAccount actor, object message)
    {
        m_host.Actor.Current = actor;
        return m_host.InvokeAsync(message);
    }

    /// <summary>Mirrors the endpoint: administrator check, expiry cleanup, then the preview transaction.</summary>
    public async Task<WikiImportReport> PreviewAsync(UserAccount actor, Stream zip, WikiImportOptions options)
    {
        await As<Guid>(actor, new RequireImportAdministrator());
        await As(actor, new CleanupExpiredImportSources());
        return await As<WikiImportReport>(actor, new PreviewImport(zip, options));
    }

    public async Task<WikiImportReport> Import(string markdown, bool update = false)
    {
        using var zip = ContentImportTests.Zip(("article.md", markdown));
        var preview = await PreviewAsync(Admin, zip, new WikiImportOptions(Source, "Import", UpdateExisting: update));
        return await As<WikiImportReport>(Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)));
    }

    public async Task<WikiImportReport> ImportWithFile(string content, bool update = false)
    {
        using var zip = ContentImportTests.Zip(("article.md", "[Manual](manual.txt)"), ("manual.txt", content));
        var preview = await PreviewAsync(Admin, zip, new WikiImportOptions(Source, "Import", UpdateExisting: update));
        return await As<WikiImportReport>(Admin, new CommitImport(preview.Id, new ConfirmWikiImport(preview.ReviewToken, true, true)));
    }

    public Task Execute(string sql, Guid id)
    {
        return m_host.ExecuteAsync(sql, id);
    }

    public async ValueTask DisposeAsync()
    {
        await m_host.DisposeAsync();
        if (m_directory is not null)
            Directory.Delete(m_directory, true);
    }
}
