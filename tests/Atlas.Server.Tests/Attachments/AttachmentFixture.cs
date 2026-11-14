using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Tests.Platform;

namespace Atlas.Server.Tests.Attachments;

internal sealed class AttachmentFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;
    private readonly AttachmentTests.Storage? m_storage;

    private AttachmentFixture(AtlasTestHost host, AttachmentTests.Storage? storage)
    {
        m_host = host;
        m_storage = storage;
    }

    public AtlasTestHost Host => m_host;
    /// <summary>The host's attachment folder: on the shared host every test's files are in it, so a test looks at its own by id.</summary>
    public AttachmentFiles Files => m_host.Services.GetRequiredService<AttachmentFiles>();
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount OtherWriter { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public Guid SpaceId { get; private set; }

    /// <summary>On the shared host.</summary>
    public static async Task<AttachmentFixture> CreateAsync()
    {
        return await SeedAsync(new AttachmentFixture(await AtlasTestHost.CreateAsync("atlas_attachment_"), null));
    }

    /// <summary>A host, a database and an attachment folder of the test's own: for a backup of the whole installation, which reads
    /// every table and every file.</summary>
    /// <param name="outbox">A host whose outbox delivers (AtlasTestHost.CreateAsync).</param>
    public static async Task<AttachmentFixture> CreateIsolatedAsync(bool outbox = false)
    {
        var storage = new AttachmentTests.Storage();
        var host = await AtlasTestHost.CreateIsolatedAsync("atlas_attachment_", services => services.AddSingleton(storage.Files), outbox: outbox);
        return await SeedAsync(new AttachmentFixture(host, storage));
    }

    private static async Task<AttachmentFixture> SeedAsync(AttachmentFixture fixture)
    {
        var host = fixture.m_host;
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

    /// <summary>Mirrors the endpoint: preflight message, disk write, metadata message.</summary>
    public async Task<AttachmentDto> UploadAsync(UserAccount actor, Guid pageId, string name, Stream content, bool image = false, Guid? id = null)
    {
        await As(actor, new AuthorizeAttachmentUpload(pageId));
        var fileId = id ?? Guid.NewGuid();
        var stored = await Files.WriteAsync(fileId, content, image, CancellationToken.None);
        return await As<AttachmentDto>(actor, new StoreAttachment(fileId, pageId, name, stored.Size, stored.Hash, stored.MimeType, image));
    }

    public Task Execute(string sql, Guid id)
    {
        return m_host.ExecuteAsync(sql, id);
    }

    public async ValueTask DisposeAsync()
    {
        await m_host.DisposeAsync();
        m_storage?.Dispose();
    }
}
