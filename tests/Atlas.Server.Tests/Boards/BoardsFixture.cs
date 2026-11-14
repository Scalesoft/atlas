using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Atlas.Server.Tests.Boards;

/// <summary>
/// A host with the Boards module on and a recording hub: a WikiAdmin, a Writer and a Reader of the board space, an account
/// without any role there, and a second space only the WikiAdmin reaches. <see cref="SharedAsync"/> shares one host among the tests
/// that keep to their own data; <see cref="Hub"/> then holds what this fixture's connections received.
/// </summary>
internal sealed class BoardsFixture : IAsyncDisposable
{
    private BoardsFixture(AtlasTestHost host, FixtureHub hub)
    {
        Host = host;
        Hub = hub;
    }

    public AtlasTestHost Host { get; }
    public FixtureHub Hub { get; }
    public UserAccount Admin { get; private set; } = null!;
    public UserAccount Writer { get; private set; } = null!;
    public UserAccount Reader { get; private set; } = null!;
    public UserAccount Outsider { get; private set; } = null!;
    public Guid SpaceId { get; private set; }
    public Guid OtherSpaceId { get; private set; }
    public LiveSubscriptions Subscriptions => Host.Services.GetRequiredService<LiveSubscriptions>();

    /// <summary>The fixture on the host every test of the process shares (no services of its own, no outbox).</summary>
    public static Task<BoardsFixture> SharedAsync() => CreateAsync(sharedAs: "boards");

    /// <param name="configure">More services, e.g. a test object type.</param>
    /// <param name="outbox">A host whose outbox delivers.</param>
    /// <param name="sharedAs">Names a configuration the tests share (services that hold no state of one test); without it the host
    /// is the test's own.</param>
    public static async Task<BoardsFixture> CreateAsync(Action<IServiceCollection>? configure = null, bool outbox = false, string? sharedAs = null)
    {
        var host = await AtlasTestHost.CreateAsync("atlas_boards_",
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IHubContext<LiveHub>, RecordingHubContext>());
                configure?.Invoke(services);
            }, modules: TestModules.AllEnabled, outbox: outbox, sharedAs: sharedAs);
        var recording = (RecordingHubContext)host.Services.GetRequiredService<IHubContext<LiveHub>>();
        var shared = sharedAs is not null && Environment.GetEnvironmentVariable("ATLAS_TEST_PRIVATE_HOSTS") is null;
        var fixture = new BoardsFixture(host, new FixtureHub(recording, shared))
        {
            Admin = await host.SeedUserAsync("admin", isWikiAdmin: true),
            Writer = await host.SeedUserAsync("writer"),
            Reader = await host.SeedUserAsync("reader"),
            Outsider = await host.SeedUserAsync("outsider")
        };
        fixture.SpaceId = await host.SeedSpaceAsync("Boards", (fixture.Writer, SpaceRole.Writer), (fixture.Reader, SpaceRole.Reader));
        fixture.OtherSpaceId = await host.SeedSpaceAsync("Elsewhere");
        return fixture;
    }

    /// <summary>Async, so the actor set here stays in this call's flow (concurrent calls each keep their own). Returns once the live
    /// messages of what it committed are sent: a batch answers before them (BoardBroadcasts).</summary>
    public async Task<T> As<T>(UserAccount actor, object message)
    {
        Host.Actor.Current = actor;
        try
        {
            return await Host.InvokeAsync<T>(message);
        }
        finally
        {
            await Broadcasts.IdleAsync();
        }
    }

    public async Task As(UserAccount actor, object message)
    {
        Host.Actor.Current = actor;
        try
        {
            await Host.InvokeAsync(message);
        }
        finally
        {
            await Broadcasts.IdleAsync();
        }
    }

    public BoardBroadcasts Broadcasts => Host.Services.GetRequiredService<BoardBroadcasts>();

    public async Task<Guid> CreateBoardAsync(UserAccount? actor = null, Guid? spaceId = null, string title = "Plan")
    {
        return (await As<BoardDto>(actor ?? Writer, new CreateBoard(spaceId ?? SpaceId, title))).Id;
    }

    public Task<BoardBatchResult> ApplyAsync(UserAccount actor, Guid boardId, params BoardChange[] changes)
    {
        return As<BoardBatchResult>(actor, new ApplyBoardBatch(boardId, Guid.NewGuid(), changes));
    }

    /// <summary>A connection subscribed to the board, as LiveHub registers one.</summary>
    public string Subscribe(UserAccount account, Guid boardId, bool canWrite = false)
    {
        return SubscribeTo(account, Atlas.Server.Features.Boards.BoardLiveChannel.Name(boardId), canWrite);
    }

    /// <summary>A connection subscribed to any channel, as LiveHub registers one.</summary>
    public string SubscribeTo(UserAccount account, string channel, bool canWrite)
    {
        var connection = Connect(account);
        Subscriptions.Subscribe(connection, channel, canWrite);
        return connection;
    }

    /// <summary>A connection as LiveHub registers one at its handshake; what it is sent shows in <see cref="Hub"/>.</summary>
    public string Connect(UserAccount account)
    {
        var connection = "connection-" + Guid.NewGuid().ToString("N");
        Hub.Own(connection);
        Subscriptions.Connect(new LiveSubscriber(connection, account.Id, account.DisplayName, account.SecurityStamp, null));
        return connection;
    }

    public static BoardChange Note(Guid id, string? text = null, double x = 0)
    {
        return new BoardChange("create", Item: new BoardItemInput(id, "note", x, 0, 160, 120, 0, Text: text, Color: "yellow"));
    }

    /// <summary>The full props of a connector as the prototype's ones were migrated.</summary>
    public static JsonObject Elbow()
    {
        return JsonNode.Parse("""{"route":"elbow","arrowStart":"none","arrowEnd":"arrow","dash":"solid"}""")!.AsObject();
    }

    /// <summary>The full props of a page card shown as a card.</summary>
    public static JsonObject Card()
    {
        return new JsonObject { ["display"] = "card" };
    }

    public static BoardChange PageCard(Guid id, Guid page)
    {
        return new BoardChange("create", Item: new BoardItemInput(id, "page", 0, 0, 240, 160, 0, TargetResourceId: page, Props: Card()));
    }

    public static async Task<ApiException> Refused(int status, Func<Task> action, string? code = null)
    {
        var refused = await Xunit.Assert.ThrowsAsync<ApiException>(action);
        Xunit.Assert.Equal((status, code), (refused.Status, refused.Code));
        return refused;
    }

    public ValueTask DisposeAsync()
    {
        Hub.Release();
        return Host.DisposeAsync();
    }

    /// <summary>The recording hub as one fixture sees it. On a shared host: only what was sent to this fixture's connections (each
    /// connection id is unique), and <see cref="OnSend"/> runs only for those sends.</summary>
    internal sealed class FixtureHub
    {
        private static readonly ConcurrentDictionary<string, FixtureHub> s_owners = new(StringComparer.Ordinal);
        private readonly RecordingHubContext m_hub;
        private readonly bool m_shared;
        private readonly HashSet<string> m_connections = new(StringComparer.Ordinal);
        private Func<Sent, Task>? m_onSend;

        public FixtureHub(RecordingHubContext hub, bool shared)
        {
            m_hub = hub;
            m_shared = shared;
            if (shared)
            {
                hub.OnSend = DispatchAsync;
            }
        }

        public IReadOnlyList<Sent> Sent => m_shared ? [.. m_hub.Sent.Select(Mine).OfType<Sent>()] : m_hub.Sent;

        public Func<Sent, Task>? OnSend
        {
            get => m_shared ? m_onSend : m_hub.OnSend;
            set
            {
                if (m_shared)
                {
                    m_onSend = value;
                }
                else
                {
                    m_hub.OnSend = value;
                }
            }
        }

        internal void Own(string connection)
        {
            lock (m_connections)
            {
                m_connections.Add(connection);
            }
            s_owners[connection] = this;
        }

        internal void Release()
        {
            lock (m_connections)
            {
                foreach (var connection in m_connections)
                {
                    s_owners.TryRemove(connection, out _);
                }
            }
        }

        private Sent? Mine(Sent sent)
        {
            lock (m_connections)
            {
                var mine = sent.Connections.Where(m_connections.Contains).ToArray();
                return mine.Length == 0 ? null : sent with { Connections = mine };
            }
        }

        private static async Task DispatchAsync(Sent sent)
        {
            foreach (var owner in sent.Connections.Select(connection => s_owners.GetValueOrDefault(connection)).OfType<FixtureHub>().Distinct())
            {
                if (owner.m_onSend is { } hook && owner.Mine(sent) is { } mine)
                {
                    await hook(mine);
                }
            }
        }
    }
}
