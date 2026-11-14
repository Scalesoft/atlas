using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Platform;
// The folder's namespace hides the type of the same name.
using Gate = Atlas.Server.Platform.Identity.PageGate;

namespace Atlas.Server.Tests.PageGate;

/// <summary>One space with a Writer and a Reader over one AtlasTestHost schema: the ground of the tests that pin how today's
/// copies of the page gate lock, how many statements they send and what they answer, and of the tests of PageGate itself.</summary>
internal sealed class PageGateFixture : IAsyncDisposable
{
    private readonly AtlasTestHost m_host;

    private PageGateFixture(AtlasTestHost host)
    {
        m_host = host;
    }

    public AtlasTestHost Host => m_host;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public Guid SpaceId { get; private set; }

    /// <summary>On the one host of the page gate tests: the locks write into the log a test starts (RecordingLocks.Start).
    /// <paramref name="isolated"/> gives a host of its own, for a test that changes a row of the whole installation (say which).</summary>
    public static async Task<PageGateFixture> CreateAsync(bool isolated = false)
    {
        static void Configure(IServiceCollection services)
        {
            RecordingLocks.Register(services);
            // The extraction lexicon is kept 5 s of the clock (PlaceLexiconCache, per space); a stopped clock keeps it however slow the run.
            services.Replace(ServiceDescriptor.Singleton(new PlaceLexiconCache(new FakeTimeProvider())));
            // The PDF export resolves the storage profiles (PageGateTrashedPageTests).
            services.AddDataProtection();
            services.AddStorageProfiles();
        }
        var host = isolated
            ? await AtlasTestHost.CreateIsolatedAsync("atlas_page_gate_", Configure)
            : await AtlasTestHost.CreateAsync("atlas_page_gate_", Configure, sharedAs: "page_gate");
        var fixture = new PageGateFixture(host);
        fixture.Writer = await host.SeedUserAsync("writer");
        fixture.Reader = await host.SeedUserAsync("reader");
        fixture.SpaceId = await host.SeedSpaceAsync("Gate", (fixture.Writer, SpaceRole.Writer), (fixture.Reader, SpaceRole.Reader));
        return fixture;
    }

    public static string Rich(string text)
    {
        return "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";
    }

    /// <summary>A page of the space the Writer created and published, so the Reader reads it too.</summary>
    public async Task<PageDto> PublishedPageAsync(string title)
    {
        var page = await As<PageDto>(Writer, new CreatePage(new CreatePageRequest(SpaceId, null, title, Rich(title), title)));
        return await As<PageDto>(Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
    }

    public async Task<Guid> RevisionAsync(Guid pageId, int version)
    {
        using var session = m_host.OpenSession();
        return await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.Version == version).Select(x => x.Id).SingleOrDefaultAsync<Guid>();
    }

    /// <summary>The metadata message of an upload whose bytes are already on disk, as the endpoint sends it after the write.</summary>
    public async Task<StoreAttachment> StoredAsync(Guid pageId)
    {
        var id = Guid.NewGuid();
        using var content = new MemoryStream("attachment"u8.ToArray());
        var stored = await m_host.Services.GetRequiredService<AttachmentFiles>().WriteAsync(id, content, false, CancellationToken.None);
        return new StoreAttachment(id, pageId, "note.txt", stored.Size, stored.Hash, stored.MimeType, false);
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

    /// <summary>The SQL statements of one message with the answer <typeparamref name="T"/>, in the order it sent them.</summary>
    public async Task<IReadOnlyList<string>> RecordAsync<T>(UserAccount actor, object message)
    {
        using var recording = SqlRecording.Start();
        await As<T>(actor, message);
        return recording.Statements;
    }

    /// <summary>The SQL statements of one message without an answer, in the order it sent them.</summary>
    public async Task<IReadOnlyList<string>> RecordAsync(UserAccount actor, object message)
    {
        using var recording = SqlRecording.Start();
        await As(actor, message);
        return recording.Statements;
    }

    /// <summary>
    /// Calls of PageGate in a scope and a transaction of their own, as a handler would make them. With <paramref name="like"/>
    /// the transaction is opened as the middleware opens that message's: a query commits without the WAL flush, which shows in
    /// the text of the actor statement.
    /// </summary>
    public Task<T> ThroughGateAsync<T>(UserAccount actor, Func<Gate, NHibernate.ISession, Task<T>> calls, object? like = null)
    {
        return InScopeAsync(actor, services => calls(services.GetRequiredService<Gate>(), services.GetRequiredService<NHibernate.ISession>()), like);
    }

    /// <summary>Calls in a scope and a transaction of their own with the scope's services at hand, for what a handler does with
    /// PageGate and a service built on it, such as PageReader. <paramref name="like"/> as in <see cref="ThroughGateAsync"/>.</summary>
    public async Task<T> InScopeAsync<T>(UserAccount actor, Func<IServiceProvider, Task<T>> calls, object? like = null)
    {
        m_host.Actor.Current = actor;
        using var scope = m_host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TransactionIntent>().Begin(like);
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        var result = await calls(scope.ServiceProvider);
        await transaction.CommitAsync();
        return result;
    }

    /// <summary>The SQL statements of one call of PageGate in a transaction of its own, in the order it sent them.</summary>
    public Task<IReadOnlyList<string>> RecordGateAsync(UserAccount actor, Func<Gate, Task> call, object? like = null)
    {
        return ThroughGateAsync(actor, async (gate, _) =>
        {
            using var recording = SqlRecording.Start();
            await call(gate);
            return recording.Statements;
        }, like);
    }

    public async ValueTask DisposeAsync()
    {
        await m_host.DisposeAsync();
    }
}
