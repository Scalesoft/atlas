using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Atlas.Server.Features.Jobs;
using Atlas.Server.Features.Objects.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Live;
using Npgsql;
using Wolverine;

namespace Atlas.Server.Features.Pages.Live;

/// <summary>
/// Relays the page changes the database announces (every saved working revision, publication change, trash and restore, whatever
/// path wrote it: ContentRowEffects notifies with the row, delivered when its transaction commits; a trigger did until 2026110930) to the subscribers of <c>page:{pageId}</c> as PageChanged; a
/// <c>working</c> change only to who may edit the page, or to every reader of a live page (3.6-I Živě); and as ObjectChanged to the view sessions containing the page (with its
/// version, read only when a session does). One connection of its own only LISTENs, its callback only queues, and one
/// loop publishes. The connection is opened again after it drops (a restart, a failover).
/// </summary>
// shortcut: a change committed while the listener is reconnecting is not relayed (a card refreshes with the next change or when it
// is shown again); keep a change log to replay if a missed refresh starts to matter.
public sealed class PageChangeListener : BackgroundService
{
    /// <summary>The notification channel of the page rows (ContentRowEffects).</summary>
    public const string Channel = "atlas_page_changed";

    /// <summary>The listener's session in pg_stat_activity.</summary>
    public const string ApplicationName = "atlas-page-changes";

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    // Bounded: a burst (an import of thousands of pages) drops its oldest announcements rather than the host's memory.
    private readonly Channel<string> m_queue = System.Threading.Channels.Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    // The observers' work, in a loop of its own so a slow one never holds up the relay (review A4-2): one entry per page and kind
    // waits at a time, and a change while it waits joins it. Full, a new change is dropped: an observer catches up on its next one.
    private readonly Channel<(Guid PageId, string Kind)> m_observed = System.Threading.Channels.Channel.CreateBounded<(Guid, string)>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentDictionary<(Guid PageId, string Kind), byte> m_waiting = new();
    private readonly IConfiguration m_configuration;
    private readonly LiveRelay m_relay;
    private readonly ViewSessions m_views;
    private readonly IServiceScopeFactory m_scopes;
    private readonly ILogger<PageChangeListener> m_log;
    private readonly SystemServiceHealth m_health;

    public PageChangeListener(IConfiguration configuration, LiveRelay relay, ViewSessions views, IServiceScopeFactory scopes, ILogger<PageChangeListener> log,
        SystemServiceHealth health)
    {
        m_health = health;
        m_configuration = configuration;
        m_relay = relay;
        m_views = views;
        m_scopes = scopes;
        m_log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (m_configuration.GetConnectionString("atlas-db") is not { Length: > 0 } connectionString)
        {
            return;
        }
        _ = SuperviseAsync(RelayAsync, stoppingToken);
        _ = SuperviseAsync(ObserveAllAsync, stoppingToken);
        var listening = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = ApplicationName, Pooling = false, KeepAlive = 30 }.ConnectionString;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(listening);
                connection.Notification += (_, notification) => m_queue.Writer.TryWrite(notification.Payload);
                await connection.OpenAsync(stoppingToken);
                await using (var listen = new NpgsqlCommand("LISTEN " + Channel, connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }
                m_health.Beat(SystemServices.PageChanges);
                while (true)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                m_log.LogWarning(ex, "Listening for page changes failed; reconnecting in {Retry}", Retry);
                m_health.Failed(SystemServices.PageChanges, ex);
                await Task.Delay(Retry, stoppingToken);
            }
        }
    }

    /// <summary>A loop that faults is logged and started again after <see cref="Retry"/>, as the listening connection is.</summary>
    private async Task SuperviseAsync(Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await loop(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                m_log.LogError(ex, "A page change loop failed; restarting in {Retry}", Retry);
                m_health.Failed(SystemServices.PageChanges, ex);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await Task.Delay(Retry, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> LiveAsync(Guid pageId, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<bool>(new Queries.IsPageLive(pageId), ct);
    }

    private async Task RelayAsync(CancellationToken ct)
    {
        await foreach (var payload in m_queue.Reader.ReadAllAsync(ct))
        {
            // "{pageId}:{kind}", a working revision's with ":{title}" (which may hold colons itself).
            var parts = payload.Split(':', 3);
            if (parts.Length < 2 || !Guid.TryParse(parts[0], out var pageId) || parts[1] is not ("working" or "published"))
            {
                continue;
            }
            var kind = parts[1];
            try
            {
                var change = new PageChangedMessage(pageId, kind, parts.Length > 2 ? parts[2] : null);
                // A save is for the page's editors, and for every reader of a live page (3.6-I Živě), who reads it too.
                var writersOnly = kind == "working" && !await LiveAsync(pageId, ct);
                await m_relay.PublishAsync(PageLiveChannel.Name(pageId), "PageChanged", change, writersOnly: writersOnly);
                if (m_views.Contains(pageId))
                {
                    await using var scope = m_scopes.CreateAsyncScope();
                    var version = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<ObjectVersion>(new GetObjectVersion(pageId), ct);
                    if (version.Version is { } current)
                    {
                        await m_views.ObjectChangedAsync(new ObjectChangedMessage(pageId, current, new JsonObject { ["kind"] = kind }), writersOnly: writersOnly);
                    }
                    if (kind == "published")
                    {
                        // A trash revokes the page's card at once (not at the next periodic sweep), a restore brings it back.
                        await m_relay.RefreshAsync(ViewSessions.Channel(pageId), ct);
                        await m_views.ReadmitAsync(pageId);
                    }
                }
            }
            catch (Exception ex)
            {
                m_log.LogWarning(ex, "PageChanged could not be sent for {PageId}", pageId);
            }
            if (m_waiting.TryAdd((pageId, kind), 0) && !m_observed.Writer.TryWrite((pageId, kind)))
            {
                m_waiting.TryRemove((pageId, kind), out _);
            }
        }
    }

    private async Task ObserveAllAsync(CancellationToken ct)
    {
        await foreach (var (pageId, kind) in m_observed.Reader.ReadAllAsync(ct))
        {
            // Before the run: a change committed during it is observed once more.
            m_waiting.TryRemove((pageId, kind), out _);
            await ObserveAsync(pageId, kind, ct);
        }
    }

    /// <summary>The modules that follow a page's changes (a deployment board), each in a scope of its own, after the relay.</summary>
    private async Task ObserveAsync(Guid pageId, string kind, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        foreach (var observer in scope.ServiceProvider.GetServices<IPageChangeObserver>())
        {
            try
            {
                await observer.PageChangedAsync(pageId, kind, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                m_log.LogWarning(ex, "{Observer} failed after a change of {PageId}", observer.GetType().Name, pageId);
            }
        }
    }
}

/// <summary>Told of every committed page change the listener relays (<c>working</c> or <c>published</c>), after the commit and in a scope
/// of its own, in a loop of its own beside the relay (changes of a page that wait for it are observed once); a module registers one to
/// follow pages (Projector's deployment board). A change missed while the listener reconnects is
/// not replayed, so an observer keeps what it derives correct on its own next occasion too.</summary>
public interface IPageChangeObserver
{
    Task PageChangedAsync(Guid pageId, string kind, CancellationToken ct);
}
