using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.ContentExport;

internal sealed class ContentExportFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private ContentExportFixture(AtlasTestHost host, AttachmentTests.Storage storage, ContentExportFiles exportFiles)
    {
        m_host = host;
        Storage = storage;
        ExportFiles = exportFiles;
    }

    public AtlasTestHost Host => m_host;
    public AttachmentTests.Storage Storage { get; }
    public ContentExportFiles ExportFiles { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount OtherWriter { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public Guid SpaceId { get; private set; }

    /// <param name="sharedAs">The test class whose tests share one host (nameof). They run one at a time, so the export folder holds
    /// only that class's archives ("no file is left behind") and a test that locks a whole table blocks no other class.</param>
    public static async Task<ContentExportFixture> CreateAsync(string sharedAs)
    {
        var host = await AtlasTestHost.CreateAsync("atlas_export_", services =>
        {
            // The folders live as long as the host, that is the test process.
            var storage = new AttachmentTests.Storage();
            services.AddSingleton(storage);
            services.AddSingleton(storage.Files);
            services.AddSingleton(new ContentExportFiles(new ConfigurationBuilder().Build(), new AttachmentTests.EnvironmentStub(storage.Root)));
        }, sharedAs: "content-export:" + sharedAs);
        var fixture = new ContentExportFixture(host, host.Services.GetRequiredService<AttachmentTests.Storage>(), host.Services.GetRequiredService<ContentExportFiles>());
        fixture.Admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        fixture.Writer = await host.SeedUserAsync("writer");
        fixture.OtherWriter = await host.SeedUserAsync("other");
        fixture.Reader = await host.SeedUserAsync("reader");
        fixture.SpaceId = await host.SeedSpaceAsync("History",
            (fixture.Writer, SpaceRole.Writer), (fixture.OtherWriter, SpaceRole.Writer), (fixture.Reader, SpaceRole.Reader));
        return fixture;
    }

    public static string Rich(string text)
    {
        return "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";
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

    public async Task<AttachmentDto> UploadAsync(UserAccount actor, Guid pageId, string name, Stream content, bool image = false)
    {
        await As(actor, new AuthorizeAttachmentUpload(pageId));
        var id = Guid.NewGuid();
        var stored = await Storage.Files.WriteAsync(id, content, image, CancellationToken.None);
        return await As<AttachmentDto>(actor, new StoreAttachment(id, pageId, name, stored.Size, stored.Hash, stored.MimeType, image));
    }

    public Task Execute(string sql, Guid id)
    {
        return m_host.ExecuteAsync(sql, id);
    }

    public ValueTask DisposeAsync() => m_host.DisposeAsync();
}
