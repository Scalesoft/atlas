using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// Faithful in-memory <see cref="IDocumentStore"/>: models the append-log +
/// snapshot-truncates-log contract so tests can assert persistence behaviour.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private sealed class Entry
    {
        public byte[]? Snapshot;
        public string Markdown = "";
        public long SnapshotSeq;
        public readonly List<LogUpdate> Log = [];
        public readonly Dictionary<Guid, long> RecentUpdateIds = [];
        public readonly Dictionary<Guid, StoredRecentUpdate> RecentUpdates = [];
        public string? SeedMarkdown;
        public System.Text.Json.JsonElement? SeedEditorJson;
        public bool ExternalChange;
        public bool ArchiveExternalOnNextFlush;
        public int AppendCalls;
        public int FlushCalls;
        public int BindCalls;
        public bool SchemaBound;
        public MarkdownDocumentSchema? Schema;
    }

    private readonly ConcurrentDictionary<string, Entry> _docs = new(StringComparer.Ordinal);
    private Entry E(string id) => _docs.GetOrAdd(id, static _ => new Entry());

    /// <summary>Pre-seed the readable projection a fresh (unseeded) doc will be seeded from.</summary>
    public void Seed(string documentId, string seedMarkdown) => E(documentId).SeedMarkdown = seedMarkdown;
    public void SeedRich(string documentId, System.Text.Json.JsonElement seed) => E(documentId).SeedEditorJson = seed.Clone();

    public void SetExternalChange(string documentId, bool value) => E(documentId).ExternalChange = value;

    /// <summary>Make the next <see cref="FlushAsync"/> report an external edit was archived (ADR-018 §5 conflict path).</summary>
    public void SetExternalEditPending(string documentId) => E(documentId).ArchiveExternalOnNextFlush = true;

    public byte[]? SnapshotOf(string documentId)
    {
        if (!_docs.TryGetValue(documentId, out var e)) return null;
        lock (e) return e.Snapshot;
    }

    public string MarkdownOf(string documentId)
    {
        if (!_docs.TryGetValue(documentId, out var e)) return "";
        lock (e) return e.Markdown;
    }

    public int LogCountOf(string documentId)
    {
        if (!_docs.TryGetValue(documentId, out var e)) return 0;
        lock (e) return e.Log.Count;
    }

    public int FlushCallsOf(string documentId)
    {
        if (!_docs.TryGetValue(documentId, out var e)) return 0;
        lock (e) return e.FlushCalls;
    }

    public int AppendCallsOf(string documentId)
    {
        if (!_docs.TryGetValue(documentId, out var e)) return 0;
        lock (e) return e.AppendCalls;
    }

    public int BindCallsOf(string documentId)
    {
        if (!_docs.TryGetValue(documentId, out var e)) return 0;
        lock (e) return e.BindCalls;
    }

    public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct)
    {
        if (!_docs.TryGetValue(documentId, out var e))
            return Task.FromResult<StoredDocument?>(null);
        lock (e)
        {
            var updates = e.Log.Where(u => u.Seq > e.SnapshotSeq).ToList();
            var lastSeq = e.Log.Count > 0 ? e.Log[^1].Seq : e.SnapshotSeq;
            return Task.FromResult<StoredDocument?>(
                new StoredDocument(
                    e.Snapshot,
                    updates,
                    lastSeq,
                    e.SeedMarkdown,
                    e.ExternalChange,
                    new Dictionary<Guid, long>(e.RecentUpdateIds))
                {
                    Schema = e.Schema,
                    SeedEditorJson = e.SeedEditorJson,
                    RecentUpdates = new Dictionary<Guid, StoredRecentUpdate>(e.RecentUpdates),
                });
        }
    }

    public Task<MarkdownDocumentSchema?> BindSchemaAsync(string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
    {
        var e = E(documentId);
        var requested = schema is null ? null : DocumentSchemaCompatibility.Normalize(schema);
        lock (e)
        {
            e.BindCalls++;
            if (e.SchemaBound || e.Snapshot is not null || e.Log.Count > 0)
                DocumentSchemaCompatibility.EnsureCompatible(e.Schema, requested);
            e.Schema = requested;
            e.SchemaBound = true;
            return Task.FromResult(e.Schema);
        }
    }

    public Task<AppendUpdateResult> AppendUpdateAsync(
        string documentId,
        Guid clientUpdateId,
        string userId,
        DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update,
        CancellationToken ct)
    {
        var e = E(documentId);
        lock (e)
        {
            e.AppendCalls++;
            if (e.RecentUpdateIds.TryGetValue(clientUpdateId, out var existing))
            {
                if (!e.RecentUpdates.TryGetValue(clientUpdateId, out var retained) ||
                    !retained.Update.AsSpan().SequenceEqual(update.Span))
                    throw new InvalidDataException("Client update id was reused with a different payload.");
                return Task.FromResult(new AppendUpdateResult(existing, true));
            }

            var sequence = e.Log.Count > 0 ? e.Log[^1].Seq + 1 : e.SnapshotSeq + 1;
            var payload = update.ToArray();
            e.Log.Add(new LogUpdate(sequence, clientUpdateId, userId, receivedAtUtc, payload));
            e.RecentUpdateIds[clientUpdateId] = sequence;
            e.RecentUpdates[clientUpdateId] = new(sequence, payload);
            return Task.FromResult(new AppendUpdateResult(sequence, false));
        }
    }

    public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
    {
        var e = E(documentId);
        bool archived;
        lock (e)
        {
            e.FlushCalls++;
            e.Snapshot = snapshot.YjsState;
            e.Markdown = snapshot.Projections.Markdown;
            e.SnapshotSeq = snapshot.CoveredSequence;
            e.ExternalChange = false; // a flush reconciles the readable file
            e.Log.RemoveAll(u => u.Seq <= snapshot.CoveredSequence); // truncate log to records above the snapshot
            archived = e.ArchiveExternalOnNextFlush;
            e.ArchiveExternalOnNextFlush = false;
        }

        return Task.FromResult(archived ? new FlushResult(true) : FlushResult.None);
    }

    public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct)
        => Task.FromResult(_docs.TryGetValue(documentId, out var e) && e.ExternalChange);
}

/// <summary>
/// Fault-injecting adapter over the faithful in-memory store. Tests can fail appends or fail
/// flushes only after the initial durable seed, so each failure targets the intended seam.
/// </summary>
public sealed class FaultInjectingDocumentStore : IDocumentStore
{
    private int _writeAttempts;
    private int _flushAttempts;

    public InMemoryDocumentStore Inner { get; } = new();
    public bool FailAppends { get; init; }
    /// <summary>Appends answer that the host replaced the document (a writer on the old generation).</summary>
    public bool SupersedeAppends { get; set; }
    public int SuccessfulFlushesBeforeFailure { get; init; } = int.MaxValue;
    /// <summary>Flushes after this many answer that the host replaced the document (a new generation).</summary>
    public int FlushesBeforeSuperseded { get; init; } = int.MaxValue;

    public int WriteAttempts => Volatile.Read(ref _writeAttempts);

    public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct)
        => Inner.LoadAsync(documentId, ct);

    public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
    {
        Interlocked.Increment(ref _writeAttempts);
        var attempt = Interlocked.Increment(ref _flushAttempts);
        if (attempt > SuccessfulFlushesBeforeFailure)
            throw new IOException("simulated snapshot write failure");
        if (attempt > FlushesBeforeSuperseded)
            return Task.FromResult(FlushResult.Superseded);

        return Inner.FlushAsync(documentId, snapshot, ct);
    }

    public Task<AppendUpdateResult> AppendUpdateAsync(
        string documentId,
        Guid clientUpdateId,
        string userId,
        DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _writeAttempts);
        if (FailAppends)
            throw new IOException("simulated append failure");
        if (SupersedeAppends)
            throw new DocumentSupersededException("simulated new generation");

        return Inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);
    }

    public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct)
        => Inner.HasExternalChangeAsync(documentId, ct);
}

public sealed class TransientFlushFailureStore : IDocumentStore
{
    private int _flushAttempts;
    private int _failNext;
    public InMemoryDocumentStore Inner { get; } = new();
    public int FlushAttempts => Volatile.Read(ref _flushAttempts);
    public void FailNextFlush() => Interlocked.Exchange(ref _failNext, 1);

    public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct)
        => Inner.LoadAsync(documentId, ct);

    public Task<MarkdownDocumentSchema?> BindSchemaAsync(
        string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
        => Inner.BindSchemaAsync(documentId, schema, ct);

    public Task<AppendUpdateResult> AppendUpdateAsync(
        string documentId, Guid clientUpdateId, string userId, DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update, CancellationToken ct)
        => Inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);

    public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
    {
        Interlocked.Increment(ref _flushAttempts);
        if (Interlocked.Exchange(ref _failNext, 0) == 1)
            throw new IOException("simulated transient snapshot write failure");
        return Inner.FlushAsync(documentId, snapshot, ct);
    }

    public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct)
        => Inner.HasExternalChangeAsync(documentId, ct);
}

public sealed class BlockingInitialFlushStore : IDocumentStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public InMemoryDocumentStore Inner { get; } = new();
    public Task FlushEntered => _entered.Task;
    public void ReleaseFlush() => _release.TrySetResult();

    public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct)
        => Inner.LoadAsync(documentId, ct);

    public Task<MarkdownDocumentSchema?> BindSchemaAsync(
        string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
        => Inner.BindSchemaAsync(documentId, schema, ct);

    public Task<AppendUpdateResult> AppendUpdateAsync(
        string documentId, Guid clientUpdateId, string userId, DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update, CancellationToken ct)
        => Inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);

    public async Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(ct);
        return await Inner.FlushAsync(documentId, snapshot, ct);
    }

    public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct)
        => Inner.HasExternalChangeAsync(documentId, ct);
}

/// <summary>
/// Appends as the Postgres store does: the transaction commits, then NHibernate's <c>CommitAsync</c> checks the token, so a cancelled
/// caller sees an exception for an update that is durable. <see cref="FailAfterNextAppend"/> models any other failure after the commit.
/// </summary>
public sealed class CommitThenCheckStore : IDocumentStore
{
    private readonly TaskCompletionSource _appendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public InMemoryDocumentStore Inner { get; } = new();
    public TaskCompletionSource? HoldAppend { get; set; }
    public Task AppendStarted => _appendStarted.Task;
    public CancellationToken AppendToken { get; private set; }
    public bool FailAfterNextAppend { get; set; }

    public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct) => Inner.LoadAsync(documentId, ct);

    public Task<MarkdownDocumentSchema?> BindSchemaAsync(string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
        => Inner.BindSchemaAsync(documentId, schema, ct);

    public async Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId,
        DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update, CancellationToken ct)
    {
        AppendToken = ct;
        _appendStarted.TrySetResult();
        if (HoldAppend is { } hold) await hold.Task;
        var result = await Inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (FailAfterNextAppend)
        {
            FailAfterNextAppend = false;
            throw new IOException("simulated failure after the commit");
        }
        return result;
    }

    public Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct) => Inner.FlushAsync(documentId, snapshot, ct);

    public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct) => Inner.HasExternalChangeAsync(documentId, ct);
}

public sealed class PersistedThenBlockingInitialFlushStore : IDocumentStore
{
    private readonly TaskCompletionSource _persisted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public InMemoryDocumentStore Inner { get; } = new();
    public Task Persisted => _persisted.Task;
    public void ReleaseReturn() => _release.TrySetResult();

    public Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct)
        => Inner.LoadAsync(documentId, ct);

    public Task<MarkdownDocumentSchema?> BindSchemaAsync(
        string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
        => Inner.BindSchemaAsync(documentId, schema, ct);

    public Task<AppendUpdateResult> AppendUpdateAsync(
        string documentId, Guid clientUpdateId, string userId, DateTimeOffset receivedAtUtc,
        ReadOnlyMemory<byte> update, CancellationToken ct)
        => Inner.AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, ct);

    public async Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
    {
        var result = await Inner.FlushAsync(documentId, snapshot, CancellationToken.None);
        _persisted.TrySetResult();
        // Deliberately ignore connection cancellation after persistence. A real store can cross its
        // final durability boundary immediately before the disconnect becomes observable.
        await _release.Task;
        return result;
    }

    public Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct)
        => Inner.HasExternalChangeAsync(documentId, ct);
}

/// <summary>
/// Test authenticator: identity from the <c>?user=</c> handshake query (empty = reject).
/// Access convention: docId "denied:*" → Denied; user "ro-*" → ReadOnly; else Write.
/// </summary>
public sealed class FakeAuthenticator : ICollabAuthenticator
{
    public Task<CollabUser?> AuthenticateAsync(HttpContext handshakeContext, CancellationToken ct)
    {
        var id = handshakeContext.Request.Query["user"].ToString();
        return Task.FromResult<CollabUser?>(string.IsNullOrEmpty(id) ? null : new CollabUser(id, id));
    }

    public Task<DocAccess> AuthorizeAsync(CollabUser user, string documentId, CancellationToken ct)
    {
        if (documentId.StartsWith("denied:", StringComparison.Ordinal)) return Task.FromResult(DocAccess.Denied);
        if (user.Id.StartsWith("ro-", StringComparison.Ordinal)) return Task.FromResult(DocAccess.ReadOnly);
        return Task.FromResult(DocAccess.Write);
    }
}

public sealed class FaultingBroadcastHubContext : IHubContext<MarkdownSyncHub>
{
    private readonly FaultingHubClients _clients = new();
    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new NoopGroupManager();
    public int BroadcastCalls => _clients.BroadcastCalls;
    public IReadOnlyList<byte[]> BroadcastUpdates => _clients.BroadcastUpdates;

    private sealed class FaultingHubClients : IHubClients
    {
        private readonly FaultingClientProxy _proxy = new();
        public int BroadcastCalls => _proxy.Calls;
        public IReadOnlyList<byte[]> BroadcastUpdates => _proxy.Updates.ToArray();
        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class FaultingClientProxy : IClientProxy
    {
        public int Calls;
        public ConcurrentQueue<byte[]> Updates { get; } = new();
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            // The fault targets the durable update broadcast only; advisory signals (SnapshotStored, ...) never fault here.
            if (!string.Equals(method, MarkdownSyncHub.ReceiveUpdate, StringComparison.Ordinal)) return Task.CompletedTask;
            if (args.Length > 1 && args[1] is byte[] update) Updates.Enqueue(update.ToArray());
            return Interlocked.Increment(ref Calls) == 1
                ? Task.FromException(new IOException("simulated broadcast failure"))
                : Task.CompletedTask;
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

public sealed class BlockingSnapshotRequestHubContext : IHubContext<MarkdownSyncHub>
{
    private readonly BlockingClients _clients = new();
    public IHubClients Clients => _clients;
    public IGroupManager Groups { get; } = new NoopGroups();
    public Task RequestStarted => _clients.RequestStarted;

    private sealed class BlockingClients : IHubClients
    {
        private readonly BlockingProxy _proxy = new();
        public Task RequestStarted => _proxy.RequestStarted;
        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class BlockingProxy : IClientProxy
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RequestStarted => _started.Task;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(method, MarkdownSyncHub.RequestSnapshot, StringComparison.Ordinal))
                return Task.CompletedTask;
            _started.TrySetResult();
            return _never.Task; // models a transport/backplane send that ignores cancellation
        }
    }

    private sealed class NoopGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

/// <summary>
/// Hosts the hub on a real Kestrel listener at a dynamic port so tests drive it over a genuine
/// HubConnection (WebSocket). Independent of Projector's Program (the lib is self-contained).
/// </summary>
public sealed class HubTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }
    public InMemoryDocumentStore Store { get; }
    public DocumentRegistry Registry => _app.Services.GetRequiredService<DocumentRegistry>();
    public DocumentAccessCoordinator Access => _app.Services.GetRequiredService<DocumentAccessCoordinator>();
    public LiveDocumentWriter Writer => _app.Services.GetRequiredService<LiveDocumentWriter>();

    private HubTestHost(WebApplication app, string baseUrl, InMemoryDocumentStore store)
    {
        _app = app;
        BaseUrl = baseUrl;
        Store = store;
    }

    public static async Task<HubTestHost> StartAsync(
        Action<MarkdownSyncOptions>? configure = null,
        IDocumentStore? store = null,
        ICollabAuthenticator? authenticator = null,
        IHubContext<MarkdownSyncHub>? hubContext = null,
        Action<IServiceCollection>? services = null)
    {
        var inMemory = store as InMemoryDocumentStore ?? new InMemoryDocumentStore();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 8L * 1024 * 1024);
        if (hubContext is not null) builder.Services.AddSingleton(hubContext);
        builder.Services.AddAtlasMarkdownSync(configure);
        builder.Services.AddSingleton(store ?? inMemory);
        builder.Services.AddSingleton(authenticator ?? new FakeAuthenticator());
        services?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapAtlasMarkdownSyncHub("/hubs/tiptap");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HubTestHost(app, address, inMemory);
    }

    /// <summary>Build (not yet started) a HubConnection identified as <paramref name="user"/>.</summary>
    public HubConnection Connection(string user)
        => new HubConnectionBuilder()
            .WithUrl($"{BaseUrl}/hubs/tiptap?user={Uri.EscapeDataString(user)}")
            .Build();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
