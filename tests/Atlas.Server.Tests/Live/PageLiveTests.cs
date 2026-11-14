using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Live;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Xunit;

namespace Atlas.Server.Tests.Live;

/// <summary>
/// The live page channel <c>page:{pageId}</c> (interaction spec §4): the database announces every saved, published, trashed or
/// restored page whatever path wrote it, one listener relays it as PageChanged, and a reader who may not edit hears only
/// <c>published</c>.
/// </summary>
public sealed class PageLiveTests
{
    [PostgreSqlFact]
    public async Task Every_saved_published_trashed_or_restored_page_is_announced_whatever_path_wrote_it()
    {
        await using var f = await BoardsFixture.SharedAsync();
        await using var listener = new NpgsqlConnection(f.Host.ConnectionString);
        await listener.OpenAsync();
        var heard = new List<string>();
        listener.Notification += (_, e) => heard.Add(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN " + PageChangeListener.Channel, listener))
        {
            await listen.ExecuteNonQueryAsync();
        }
        // The channel carries every page of the shared database: only this test's page counts.
        async Task<string[]> Heard(Guid pageId)
        {
            // Every notification of a committed command has arrived once the next round trip on this connection returns.
            await using (var ping = new NpgsqlCommand("SELECT 1", listener))
            {
                await ping.ExecuteScalarAsync();
            }
            var all = heard.Where(payload => payload.StartsWith(pageId + ":", StringComparison.Ordinal)).ToArray();
            heard.Clear();
            return all;
        }

        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Live", IndexFixture.Rich("first"), "x"), f.Writer, default);
        Assert.Equal([$"{page.Id}:working:Live"], await Heard(page.Id));
        var saved = await pages.SaveAsync(page.Id, new SavePageRequest("Live", IndexFixture.Rich("second"), null, page.Version), f.Writer, default);
        Assert.Equal([$"{page.Id}:working:Live"], await Heard(page.Id));
        // An unchanged save writes no revision and announces nothing.
        await pages.SaveAsync(page.Id, new SavePageRequest("Live", IndexFixture.Rich("second"), null, saved.Version), f.Writer, default);
        Assert.Empty(await Heard(page.Id));

        // The personal API's Markdown save (the path MCP shares).
        f.Host.Actor.Current = f.Writer;
        using (var scope = f.Host.Services.CreateScope())
        {
            saved = await new PageMarkdownSaveRunner(scope.ServiceProvider.GetRequiredService<IMessageBus>())
                .SaveAsync(page.Id, new ApiSavePageRequest("Third, from the API.", saved.Version), default);
        }
        Assert.Equal([$"{page.Id}:working:Live"], await Heard(page.Id));

        await pages.PublishAsync(page.Id, new PublishPageRequest(saved.Version), f.Writer, default);
        Assert.Equal([$"{page.Id}:published"], await Heard(page.Id));
        await f.As<OrganizationChange>(f.Writer, new TrashPage(page.Id, new TrashPageRequest(true, await TreeVersionAsync(f), saved.Version)));
        Assert.Equal([$"{page.Id}:published"], await Heard(page.Id));
        await f.As<OrganizationChange>(f.Admin, new UntrashPage(page.Id, new UntrashPageRequest(null, true, await TreeVersionAsync(f))));
        Assert.Equal([$"{page.Id}:published"], await Heard(page.Id));
    }

    [PostgreSqlFact]
    public async Task The_listener_relays_working_changes_to_editors_only_and_comes_back_after_its_connection_drops()
    {
        // Own host: the test ends the host's page-change listener session, which relays the pages of every test on a shared host.
        await using var f = await BoardsFixture.CreateAsync();
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Live", IndexFixture.Rich("first"), "x"), f.Writer, default);
        var channel = PageLiveChannel.Name(page.Id);
        var reader = f.SubscribeTo(f.Reader, channel, canWrite: false);
        var writer = f.SubscribeTo(f.Writer, channel, canWrite: true);
        var first = await ListeningAsync(f, null);

        // The creation's own announcement may reach the subscribers too; the relay keeps the database's order.
        var saved = await pages.SaveAsync(page.Id, new SavePageRequest("Live", IndexFixture.Rich("second"), null, page.Version), f.Writer, default);
        var working = await HeardAsync(f, new PageChangedMessage(page.Id, "working", "Live"), 0);
        Assert.Equal((writer, "PageChanged"), (Assert.Single(working.Connections), working.Method));
        await pages.PublishAsync(page.Id, new PublishPageRequest(saved.Version), f.Writer, default);
        var published = await HeardAsync(f, new PageChangedMessage(page.Id, "published"), 0);
        Assert.Equal(new[] { reader, writer }.Order(), published.Connections.Order());

        // The database ends the listener's session (a failover, a restart): it reconnects and relays again.
        await ListenerAsync(f, "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND application_name = @name");
        await ListeningAsync(f, first);
        var mark = f.Hub.Sent.Count;
        await pages.SaveAsync(page.Id, new SavePageRequest("Live", IndexFixture.Rich("third"), null, saved.Version), f.Writer, default);
        Assert.Equal(writer, Assert.Single((await HeardAsync(f, new PageChangedMessage(page.Id, "working", "Live"), mark)).Connections));
        Assert.DoesNotContain(f.Hub.Sent, sent => sent.Connections.Contains(reader) && sent.Payload is PageChangedMessage { Kind: "working" });
    }

    [PostgreSqlFact]
    public async Task A_page_subscriber_needs_read_access_and_may_hear_working_changes_only_when_it_may_edit()
    {
        await using var f = await BoardsFixture.SharedAsync();
        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Live", IndexFixture.Rich("x"), "x"), f.Writer, default);
        // Until it is published, the page is its writers' draft: a reader cannot even learn that it exists.
        Assert.Equal(LiveAccess.NotFound, await f.Host.InvokeAsync<LiveAccess>(new AuthorizePageSubscriber(page.Id, f.Reader.Id, f.Reader.SecurityStamp, null)));
        await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        Task<LiveAccess> Decide(UserAccount account, Guid pageId, string? stamp = null) =>
            f.Host.InvokeAsync<LiveAccess>(new AuthorizePageSubscriber(pageId, account.Id, stamp ?? account.SecurityStamp, null));
        Assert.Equal(new LiveAccess(LiveAccessOutcome.Allowed, true, 0), await Decide(f.Writer, page.Id));
        Assert.Equal(new LiveAccess(LiveAccessOutcome.Allowed, false, 0), await Decide(f.Reader, page.Id));
        Assert.Equal(LiveAccess.NotFound, await Decide(f.Outsider, page.Id));
        Assert.Equal(LiveAccess.NotFound, await Decide(f.Writer, Guid.NewGuid()));
        Assert.Equal(LiveAccess.Forbidden, await Decide(f.Reader, page.Id, "rotated"));
        Assert.Contains(f.Host.Services.GetServices<ILiveChannel>(), channel => channel is PageLiveChannel);
    }

    [PostgreSqlFact]
    public async Task A_slow_page_observer_holds_up_neither_the_relay_nor_more_than_one_run_per_page()
    {
        // Own host: the held observer stops the observer runs of every page of the host.
        var observer = new GatedObserver();
        await using var f = await BoardsFixture.CreateAsync(services => services.AddSingleton<IPageChangeObserver>(observer));
        var pages = new TestPages(f.Host);
        async Task<Guid> Page(string title) => (await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, title, IndexFixture.Rich(title), "x"), f.Writer, default)).Id;
        async Task Save(Guid page, string text) => await pages.SaveAsync(page, new SavePageRequest("Live", IndexFixture.Rich(text), null,
            (await pages.GetAsync(page, f.Writer, default)).Version), f.Writer, default);
        var other = await Page("Other");
        await observer.RunsAsync(other, 1);
        observer.Hold();
        var slow = await Page("Slow");
        await observer.RunsAsync(slow, 1);
        f.SubscribeTo(f.Writer, PageLiveChannel.Name(other), canWrite: true);

        // While the observer is stuck on one page, that page keeps changing and another page's change is relayed anyway.
        for (var n = 0; n < 3; n++)
        {
            await Save(slow, "again " + n);
        }
        var mark = f.Hub.Sent.Count;
        await Save(other, "changed");
        await HeardAsync(f, new PageChangedMessage(other, "working", "Live"), mark);

        // Released, it runs once more for the three changes that waited, then for the other page.
        observer.Release();
        await observer.RunsAsync(other, 2);
        Assert.Equal(2, observer.Runs(slow));
    }

    /// <summary>Counts every run per page; once held, a run waits until released.</summary>
    private sealed class GatedObserver : IPageChangeObserver
    {
        private readonly TaskCompletionSource m_gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> m_runs = new();
        private volatile bool m_held;

        public async Task PageChangedAsync(Guid pageId, string kind, CancellationToken ct)
        {
            m_runs.AddOrUpdate(pageId, 1, (_, runs) => runs + 1);
            if (m_held)
            {
                await m_gate.Task.WaitAsync(ct);
            }
        }

        public void Hold() => m_held = true;

        public void Release() => m_gate.TrySetResult();

        public int Runs(Guid pageId) => m_runs.GetValueOrDefault(pageId);

        public async Task RunsAsync(Guid pageId, int runs)
        {
            for (var attempt = 0; attempt < 500 && Runs(pageId) < runs; attempt++)
            {
                await Task.Delay(20);
            }
            Assert.Equal(runs, Runs(pageId));
        }
    }

    private static async Task<long> TreeVersionAsync(BoardsFixture f)
    {
        using var session = f.Host.OpenSession();
        return await TreeVersion.ReadAsync(session, f.SpaceId, default);
    }

    /// <summary>Until the listener's session is in the database, a new one when <paramref name="before"/> names the old one.</summary>
    private static async Task<int> ListeningAsync(BoardsFixture f, int? before)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (await ListenerAsync(f, "SELECT max(pid) FROM pg_stat_activity WHERE datname = current_database() AND application_name = @name AND state = 'idle' AND query LIKE 'LISTEN%'") is int listening
                && listening != before)
            {
                return listening;
            }
            await Task.Delay(50, timeout.Token);
        }
    }

    /// <summary>A query about the listener's session, on a connection of its own.</summary>
    private static async Task<object?> ListenerAsync(BoardsFixture f, string sql)
    {
        await using var connection = new NpgsqlConnection(f.Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", PageChangeListener.ApplicationName);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>The first <paramref name="message"/> the relay sent from index <paramref name="from"/> on.</summary>
    private static async Task<Sent> HeardAsync(BoardsFixture f, PageChangedMessage message, int from)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (f.Hub.Sent.Skip(from).FirstOrDefault(sent => Equals(sent.Payload, message)) is { } heard)
            {
                return heard;
            }
            await Task.Delay(20);
        }
        Assert.Fail($"{message} was not relayed; sent from {from}: {string.Join(", ", f.Hub.Sent.Skip(from).Select(sent => sent.Payload))}");
        return null!;
    }
}
