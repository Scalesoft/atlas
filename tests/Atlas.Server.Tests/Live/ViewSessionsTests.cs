using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Atlas.Server.Tests.Live;

/// <summary>
/// Server-side view sessions on /hubs/live (objects spec §4.2, F1 plan §2.4): a view's membership comes from its data and follows
/// its changes; each participant holds an <c>obj:{id}</c> subscription per readable member, counted over its views and kept 30 s
/// after the last one; a change or presence of an object reaches every live session that contains it, in detail inside the
/// recipient's viewport and as a summary outside it; and nothing, not even an id, reaches a participant who may not read the
/// object, including the colleague's view (<c>via</c>) and after a revocation in the middle of a session.
/// </summary>
public sealed class ViewSessionsTests
{
    private static readonly JsonElement s_cursor = JsonDocument.Parse("""{"cursor":{"from":3,"to":5}}""").RootElement;

    [PostgreSqlFact]
    public async Task Membership_follows_board_batches_and_never_names_an_object_the_participant_may_not_read()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var shared = await t.PublishedPageAsync(f.Writer, f.SpaceId, "Shared");
        var draft = await t.DraftPageAsync(f.Writer, f.SpaceId, "Draft");
        var board = await f.CreateBoardAsync();
        var sharedCard = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.PageCard(sharedCard, shared));
        var writer = t.Connect(f.Writer);
        var reader = t.Connect(f.Reader);

        var joined = await t.Views.JoinAsync(writer, "board:" + board.ToString("N").ToUpperInvariant(), default);
        Assert.Equal([(board, "atlas.board", 1L), (shared, "atlas.page", await t.VersionAsync(shared))],
            joined.Members.Select(member => (member.ObjectId, member.TypeId, member.Version)));
        Assert.Equal([board, shared], (await t.Views.JoinAsync(reader, "board:" + board, default)).Members.Select(member => member.ObjectId));
        Assert.True(f.Subscriptions.CanWrite(writer, "obj:" + shared));
        Assert.False(f.Subscriptions.CanWrite(reader, "obj:" + board));

        // A card of a page only the writer may read: the writer is told, the reader hears nothing of it at all.
        var mark = t.Mark();
        var draftCard = Guid.NewGuid();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.PageCard(draftCard, draft));
        var added = Assert.Single(t.Since<ViewMembersMessage>(mark, writer, "ViewMembers"));
        Assert.Equal(("board:" + board, draft), (added.ViewKey, Assert.Single(added.Added).ObjectId));
        Assert.Empty(added.Removed);
        Assert.Empty(t.Since<ViewMembersMessage>(mark, reader, "ViewMembers"));
        // The board's own change reaches both, with its version.
        Assert.Equal(2L, Assert.Single(t.Since<ObjectChangedMessage>(mark, reader, "ObjectChanged")).Version);
        Assert.Null(f.Subscriptions.CanWrite(reader, "obj:" + draft));

        mark = t.Mark();
        await t.Views.ObjectChangedAsync(new ObjectChangedMessage(draft, 9, JsonNode.Parse("""{"kind":"working"}""")));
        Assert.Single(t.Since<ObjectChangedMessage>(mark, writer, "ObjectChanged"));
        Assert.Empty(t.Sent(mark, reader));

        // Removing a card removes the object from the view for whoever was told of it.
        mark = t.Mark();
        await f.ApplyAsync(f.Writer, board, new BoardChange("delete", ItemId: sharedCard), new BoardChange("delete", ItemId: draftCard));
        Assert.Equal([shared, draft], Assert.Single(t.Since<ViewMembersMessage>(mark, writer, "ViewMembers")).Removed);
        Assert.Equal([shared], Assert.Single(t.Since<ViewMembersMessage>(mark, reader, "ViewMembers")).Removed);
        mark = t.Mark();
        await t.Views.ObjectChangedAsync(new ObjectChangedMessage(shared, 9, null));
        Assert.Empty(t.Sent(mark, writer).Concat(t.Sent(mark, reader)));
    }

    [PostgreSqlFact]
    public async Task An_object_subscription_is_counted_over_the_participants_views_and_lingers_thirty_seconds_after_the_last()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var page = await t.PublishedPageAsync(f.Writer, f.SpaceId, "Linked");
        var board = await f.CreateBoardAsync();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.PageCard(Guid.NewGuid(), page));
        var writer = t.Connect(f.Writer);
        await t.Views.JoinAsync(writer, "board:" + board, default);
        await t.Views.JoinAsync(writer, "page:" + page, default);

        await t.Views.LeaveAsync(writer, "board:" + board);
        Assert.True(f.Subscriptions.CanWrite(writer, "obj:" + page));
        Assert.True(f.Subscriptions.CanWrite(writer, "obj:" + board));
        t.Clock.Advance(TimeSpan.FromSeconds(29));
        t.Views.ExpireLingering();
        Assert.True(f.Subscriptions.CanWrite(writer, "obj:" + board));
        // Back within the linger: the cached decision is used, no new authorization (the lost grant would refuse it).
        await f.Host.ExecuteAsync("""UPDATE "SpaceGrant" SET "ActiveTo"=now() WHERE "UserId"=:id AND "ActiveTo" IS NULL""", f.Writer.Id);
        await t.Views.JoinAsync(writer, "board:" + board, default);
        await t.Views.LeaveAsync(writer, "board:" + board);
        t.Clock.Advance(TimeSpan.FromSeconds(31));
        t.Views.ExpireLingering();
        Assert.Null(f.Subscriptions.CanWrite(writer, "obj:" + board));
        Assert.True(f.Subscriptions.CanWrite(writer, "obj:" + page));
        Assert.True(t.Views.Contains(page));
        Assert.False(t.Views.Contains(board));

        await t.Views.LeaveAsync(writer, "page:" + page);
        t.Clock.Advance(ViewSessions.Linger);
        t.Views.ExpireLingering();
        Assert.Null(f.Subscriptions.CanWrite(writer, "obj:" + page));
        Assert.False(t.Views.Contains(page));
    }

    /// <summary>„Moje barva“ (5.2): each person present has one colour per view; who prefers a taken colour gets the next free one in
    /// palette order, who came first keeps theirs, and a colour is free again once its holder leaves.</summary>
    [PostgreSqlFact]
    public async Task Two_people_who_prefer_one_colour_get_different_ones_and_a_leaver_frees_theirs()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var board = await f.CreateBoardAsync(f.Admin);
        var teal = CollaboratorPalette.SlotOf("teal");
        var prefer = Atlas.Server.Platform.Validation.Appearance.Default with { CollaboratorColor = "teal" };
        await f.As<Atlas.Server.Platform.Validation.Appearance>(f.Admin, new UpdateAppearance(prefer));
        await f.As<Atlas.Server.Platform.Validation.Appearance>(f.Writer, new UpdateAppearance(prefer));
        var admin = t.Connect(f.Admin);
        var writer = t.Connect(f.Writer);
        var reader = t.Connect(f.Reader);
        foreach (var connection in new[] { admin, writer, reader })
        {
            await t.Views.JoinAsync(connection, "board:" + board, default);
        }

        async Task<string> ColorOfAsync(string sender, string receiver)
        {
            var mark = t.Mark();
            await t.Views.SendPresenceAsync(sender, board, s_cursor);
            return Assert.Single(t.Since<ObjectPresenceMessage>(mark, receiver, "ObjectPresence")).Color;
        }
        var first = await ColorOfAsync(admin, reader);
        var second = await ColorOfAsync(writer, reader);
        Assert.Equal(CollaboratorPalette.Entries[teal].Light, first);
        Assert.Equal(CollaboratorPalette.Entries[(teal + 1) % CollaboratorPalette.Count].Light, second);

        await t.Views.LeaveAsync(admin, "board:" + board);
        Assert.Equal(second, await ColorOfAsync(writer, reader));            // the one who stayed keeps theirs
        await t.Views.JoinAsync(admin, "board:" + board, default);
        Assert.Equal(first, await ColorOfAsync(admin, reader));              // and the freed colour can be had again
    }

    /// <summary>A page's carets (its live document, CollaborationModule's ColorScope) and its object presence (its view session) hand out
    /// colours from one table: the person who holds a colour in the editor has it in the presence too, and no one else on the page gets it.</summary>
    [PostgreSqlFact]
    public async Task A_pages_live_document_and_view_session_hand_out_colours_from_one_table()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var space = await f.Host.SeedSpaceAsync("Colours", (f.Admin, SpaceRole.Writer), (f.Writer, SpaceRole.Writer));
        var page = await t.PublishedPageAsync(f.Admin, space, "Colours");
        await f.As<Atlas.Server.Platform.Validation.Appearance>(f.Writer, new UpdateAppearance(Atlas.Server.Platform.Validation.Appearance.Default with { CollaboratorColor = "teal" }));
        var teal = CollaboratorPalette.Entries[CollaboratorPalette.SlotOf("teal")].Light;
        // The admin's caret holds teal in the page's live document (a generation of it), as the Markdown hub assigns it.
        var scope = f.Host.Services.GetRequiredService<MarkdownSyncOptions>().ColorScope($"page:{page:N}:{Guid.NewGuid():N}");
        Assert.Equal(teal, f.Host.Services.GetRequiredService<CollaboratorColors>().Assign(scope, "markdown-connection", f.Admin.Id.ToString(), teal));
        var admin = t.Connect(f.Admin);
        var writer = t.Connect(f.Writer);
        await t.Views.JoinAsync(admin, "page:" + page, default);
        await t.Views.JoinAsync(writer, "page:" + page, default);

        var mark = t.Mark();
        await t.Views.SendPresenceAsync(writer, page, s_cursor);
        await t.Views.SendPresenceAsync(admin, page, s_cursor);
        Assert.NotEqual(teal, Assert.Single(t.Since<ObjectPresenceMessage>(mark, admin, "ObjectPresence")).Color);   // taken by the caret
        Assert.Equal(teal, Assert.Single(t.Since<ObjectPresenceMessage>(mark, writer, "ObjectPresence")).Color);     // the caret's own
    }

    /// <summary>A card on two boards: a recipient sees the sender in the colour the recipient's own board gave them (not the one of the
    /// board they work from, which someone else may have on this board), and a changed „Moje barva“ shows with the next presence.</summary>
    [PostgreSqlFact]
    public async Task Presence_takes_the_recipients_view_colour_and_a_changed_preference_restamps_live_sessions()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var space = await f.Host.SeedSpaceAsync("Cards", (f.Admin, SpaceRole.Writer), (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        var page = await t.PublishedPageAsync(f.Admin, space, "Card");
        var first = await f.CreateBoardAsync(f.Admin);
        var second = await f.CreateBoardAsync(f.Admin);
        await f.ApplyAsync(f.Admin, first, BoardsFixture.PageCard(Guid.NewGuid(), page));
        await f.ApplyAsync(f.Admin, second, BoardsFixture.PageCard(Guid.NewGuid(), page));
        var prefer = Atlas.Server.Platform.Validation.Appearance.Default with { CollaboratorColor = "teal" };
        await f.As<Atlas.Server.Platform.Validation.Appearance>(f.Admin, new UpdateAppearance(prefer));
        await f.As<Atlas.Server.Platform.Validation.Appearance>(f.Writer, new UpdateAppearance(prefer));
        var teal = CollaboratorPalette.SlotOf("teal");
        var admin = t.Connect(f.Admin);
        var writer = t.Connect(f.Writer);
        var reader = t.Connect(f.Reader);
        await t.Views.JoinAsync(writer, "board:" + second, default);        // teal on the second board
        await t.Views.JoinAsync(admin, "board:" + first, default);          // teal on the first, where the admin works from
        await t.Views.JoinAsync(admin, "board:" + second, default);         // the next free one on the second
        await t.Views.JoinAsync(reader, "board:" + second, default);

        var mark = t.Mark();
        await t.Views.SendPresenceAsync(admin, page, s_cursor);
        // Not teal, the admin's colour on the first board they work from: the writer is teal here.
        Assert.Equal(CollaboratorPalette.Entries[(teal + 1) % CollaboratorPalette.Count].Light, Assert.Single(t.Since<ObjectPresenceMessage>(mark, reader, "ObjectPresence")).Color);

        // A colour no one else on the second board has (the writer teal, the reader its automatic one).
        var free = CollaboratorPalette.Entries.Where((_, slot) => slot != teal && slot != CollaboratorPalette.Auto(f.Reader.Id.ToString())).Last();
        await f.As<Atlas.Server.Platform.Validation.Appearance>(f.Admin, new UpdateAppearance(prefer with { CollaboratorColor = free.Id }));
        mark = t.Mark();
        await t.Views.SendPresenceAsync(admin, page, s_cursor);
        Assert.Equal(free.Light, Assert.Single(t.Since<ObjectPresenceMessage>(mark, reader, "ObjectPresence")).Color);
    }

    /// <summary>A page edited in its own view shows up on the board that holds its card, and the other way round: one object, one
    /// presence, wherever it is shown. The colleague's view travels only to who may read it; the viewport decides the detail.</summary>
    [PostgreSqlFact]
    public async Task Changes_and_presence_of_an_object_reach_every_session_that_contains_it_with_detail_by_viewport()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var third = await f.Host.SeedSpaceAsync("Third", (f.Outsider, SpaceRole.Writer));
        var page = await t.PublishedPageAsync(f.Outsider, third, "Operations");
        var board = await f.CreateBoardAsync(f.Admin);
        await f.ApplyAsync(f.Admin, board, BoardsFixture.PageCard(Guid.NewGuid(), page));
        var admin = t.Connect(f.Admin);
        var outsider = t.Connect(f.Outsider);
        await t.Views.JoinAsync(admin, "board:" + board, default);
        await t.Views.JoinAsync(outsider, "page:" + page, default);

        var mark = t.Mark();
        await t.Views.ObjectChangedAsync(new ObjectChangedMessage(page, 7, JsonNode.Parse("""{"kind":"published"}""")));
        Assert.Equal("published", Assert.Single(t.Since<ObjectChangedMessage>(mark, admin, "ObjectChanged")).Change!["kind"]!.GetValue<string>());
        Assert.NotNull(Assert.Single(t.Since<ObjectChangedMessage>(mark, outsider, "ObjectChanged")).Change);

        mark = t.Mark();
        await t.Views.SendPresenceAsync(outsider, page, s_cursor);
        var heard = Assert.Single(t.Since<ObjectPresenceMessage>(mark, admin, "ObjectPresence"));
        Assert.Equal((page, f.Outsider.Id, f.Outsider.DisplayName, "page:" + page), (heard.ObjectId, heard.UserId, heard.DisplayName, heard.Via));
        Assert.Equal(5, heard.Payload!.Value.GetProperty("cursor").GetProperty("to").GetInt32());
        Assert.StartsWith("#", heard.Color);
        Assert.Empty(t.Sent(mark, outsider));
        // The admin works through a board the outsider may not read: it is "elsewhere".
        mark = t.Mark();
        await t.Views.SendPresenceAsync(admin, page, s_cursor);
        Assert.Null(Assert.Single(t.Since<ObjectPresenceMessage>(mark, outsider, "ObjectPresence")).Via);
        // Presence on the board itself never reaches who is not in a session containing it.
        mark = t.Mark();
        await t.Views.SendPresenceAsync(admin, board, s_cursor);
        Assert.Empty(t.Sent(mark, outsider));

        // Outside the admin's viewport the page is a summary: that it changed and who is there, not what or where.
        t.Views.SetViewport(admin, "board:" + board, [board]);
        mark = t.Mark();
        await t.Views.ObjectChangedAsync(new ObjectChangedMessage(page, 8, JsonNode.Parse("""{"kind":"published"}""")));
        await t.Views.SendPresenceAsync(outsider, page, s_cursor);
        var summary = Assert.Single(t.Since<ObjectChangedMessage>(mark, admin, "ObjectChanged"));
        Assert.Equal((8L, (JsonNode?)null), (summary.Version, summary.Change));
        var away = Assert.Single(t.Since<ObjectPresenceMessage>(mark, admin, "ObjectPresence"));
        Assert.Equal(((JsonElement?)null, "page:" + page), (away.Payload, away.Via));
        Assert.NotNull(Assert.Single(t.Since<ObjectChangedMessage>(mark, outsider, "ObjectChanged")).Change);
    }

    [PostgreSqlFact]
    public async Task A_revocation_in_the_middle_of_a_session_tells_the_participant_once_and_sends_it_nothing_more()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var page = await t.PublishedPageAsync(f.Writer, f.SpaceId, "Shared");
        var board = await f.CreateBoardAsync();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.PageCard(Guid.NewGuid(), page));
        var reader = t.Connect(f.Reader);
        await t.Views.JoinAsync(reader, "board:" + board, default);

        await f.Host.ExecuteAsync("""UPDATE "SpaceGrant" SET "ActiveTo"=now() WHERE "UserId"=:id AND "ActiveTo" IS NULL""", f.Reader.Id);
        var mark = t.Mark();
        await f.Host.Services.GetRequiredService<LiveRelay>().RefreshAsync(null, default);
        Assert.Equal(new[] { board, page }.Order(), t.Since<ObjectRevokedMessage>(mark, reader, "ObjectRevoked").Select(revoked => revoked.ObjectId).Order());

        mark = t.Mark();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.Note(Guid.NewGuid(), "after"));
        await t.Views.ObjectChangedAsync(new ObjectChangedMessage(page, 9, null));
        var writer = t.Connect(f.Writer);
        await t.Views.JoinAsync(writer, "board:" + board, default);
        await t.Views.SendPresenceAsync(writer, page, s_cursor);
        Assert.Empty(t.Sent(mark, reader));
    }

    [PostgreSqlFact]
    public async Task After_a_reconnect_joining_again_returns_the_members_at_their_current_versions()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var page = await t.PublishedPageAsync(f.Writer, f.SpaceId, "Versioned");
        var board = await f.CreateBoardAsync();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.PageCard(Guid.NewGuid(), page));
        var first = t.Connect(f.Writer);
        await t.Views.JoinAsync(first, "board:" + board, default);
        await t.Views.DisconnectAsync(first);
        f.Subscriptions.Disconnect(first);

        await f.ApplyAsync(f.Writer, board, BoardsFixture.Note(Guid.NewGuid(), "while away"));
        await t.SaveAsync(page, f.Writer, "Versioned", "changed while away");
        var again = t.Connect(f.Writer);
        var joined = await t.Views.JoinAsync(again, "board:" + board, default);
        Assert.Equal([(board, 2L), (page, await t.VersionAsync(page))], joined.Members.Select(member => (member.ObjectId, member.Version)));
    }

    [PostgreSqlFact]
    public async Task Joining_and_presence_are_refused_with_the_hub_codes()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var board = await f.CreateBoardAsync();
        var outsider = t.Connect(f.Outsider);
        var reader = t.Connect(f.Reader);
        foreach (var key in new[] { "doc:" + board, "board:not-a-guid", "board", "" })
        {
            await AssertHubErrorAsync("unsupported", () => t.Views.JoinAsync(reader, key, default));
        }
        await AssertHubErrorAsync("not_found", () => t.Views.JoinAsync(outsider, "board:" + board, default));
        await AssertHubErrorAsync("not_found", () => t.Views.JoinAsync(reader, "board:" + Guid.NewGuid(), default));
        await AssertHubErrorAsync("not_found", () => t.Views.JoinAsync(reader, "page:" + board, default));
        Assert.False(t.Views.Contains(board));

        await t.Views.JoinAsync(reader, "board:" + board, default);
        await AssertHubErrorAsync("forbidden", () => t.Views.SendPresenceAsync(reader, board, s_cursor));
        await AssertHubErrorAsync("forbidden", () => t.Views.SendPresenceAsync(outsider, board, s_cursor));
        var writer = t.Connect(f.Writer);
        await t.Views.JoinAsync(writer, "board:" + board, default);
        await AssertHubErrorAsync("invalid_presence", () => t.Views.SendPresenceAsync(writer, board, JsonDocument.Parse("[1]").RootElement));
        await AssertHubErrorAsync("invalid_presence", () => t.Views.SendPresenceAsync(writer, board,
            JsonDocument.Parse(JsonSerializer.Serialize(new { text = new string('x', 5000) })).RootElement));
        Assert.Throws<HubException>(() => t.Views.SetViewport(outsider, "board:" + board, [board]));
    }

    /// <summary>The page listener's announcement reaches the board session too: a page saved in its editor shows up at its card,
    /// with the page's version, and only to who may edit it (a working change), as on the page channel.</summary>
    [PostgreSqlFact]
    public async Task A_saved_page_reaches_the_board_session_that_shows_its_card()
    {
        await using var t = await ViewTest.CreateAsync();
        var f = t.Fixture;
        var page = await t.PublishedPageAsync(f.Writer, f.SpaceId, "Card");
        var board = await f.CreateBoardAsync();
        await f.ApplyAsync(f.Writer, board, BoardsFixture.PageCard(Guid.NewGuid(), page));
        var writer = t.Connect(f.Writer);
        var reader = t.Connect(f.Reader);
        await t.Views.JoinAsync(writer, "board:" + board, default);
        await t.Views.JoinAsync(reader, "board:" + board, default);

        var mark = t.Mark();
        await t.SaveAsync(page, f.Writer, "Card", "edited in the page");
        var version = await t.VersionAsync(page);
        var changed = await t.WaitAsync<ObjectChangedMessage>(mark, writer, "ObjectChanged", message => message.ObjectId == page);
        Assert.Equal((version, "working"), (changed.Version, changed.Change!["kind"]!.GetValue<string>()));
        Assert.DoesNotContain(t.Since<ObjectChangedMessage>(mark, reader, "ObjectChanged"), message => message.ObjectId == page);
    }

    private static async Task AssertHubErrorAsync(string code, Func<Task> invoke)
    {
        Assert.Equal(code, (await Assert.ThrowsAsync<HubException>(invoke)).Message);
    }

    /// <summary>A boards fixture on a fake clock, with helpers for connections, pages and what the hub sent.</summary>
    private sealed class ViewTest : IAsyncDisposable
    {
        private ViewTest(BoardsFixture fixture, FakeTimeProvider clock)
        {
            Fixture = fixture;
            Clock = clock;
        }

        public BoardsFixture Fixture { get; }
        public FakeTimeProvider Clock { get; }
        public ViewSessions Views => Fixture.Host.Services.GetRequiredService<ViewSessions>();

        public static async Task<ViewTest> CreateAsync()
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
            // Sweeps and lingering run when a test says so: no periodic refresh decides behind its back.
            return new ViewTest(await BoardsFixture.CreateAsync(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                services.Remove(services.Single(service => service.ImplementationType == typeof(LiveRefreshService)));
            }), clock);
        }

        /// <summary>A connection as LiveHub registers one at its handshake.</summary>
        public string Connect(UserAccount account)
        {
            var connection = "connection-" + Guid.NewGuid().ToString("N");
            Fixture.Subscriptions.Connect(new LiveSubscriber(connection, account.Id, account.DisplayName, account.SecurityStamp, null));
            return connection;
        }

        public async Task<Guid> DraftPageAsync(UserAccount author, Guid spaceId, string title)
        {
            return (await new TestPages(Fixture.Host).CreateAsync(new CreatePageRequest(spaceId, null, title, IndexFixture.Rich(title), "x"), author, default)).Id;
        }

        public async Task<Guid> PublishedPageAsync(UserAccount author, Guid spaceId, string title)
        {
            var pages = new TestPages(Fixture.Host);
            var page = await pages.CreateAsync(new CreatePageRequest(spaceId, null, title, IndexFixture.Rich(title), "x"), author, default);
            await pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), author, default);
            return page.Id;
        }

        public async Task SaveAsync(Guid page, UserAccount author, string title, string text)
        {
            var pages = new TestPages(Fixture.Host);
            var current = await pages.GetAsync(page, author, default);
            await pages.SaveAsync(page, new SavePageRequest(title, IndexFixture.Rich(text), null, current.Version), author, default);
        }

        public async Task<long> VersionAsync(Guid objectId)
        {
            using var session = Fixture.Host.OpenSession();
            return (await session.GetAsync<Atlas.Server.Platform.Resources.ContentResource>(objectId))!.Version;
        }

        public int Mark() => Fixture.Hub.Sent.Count;

        public IReadOnlyList<Sent> Sent(int mark, string connection) => Fixture.Hub.Sent.Skip(mark).Where(sent => sent.Connections.Contains(connection)).ToArray();

        public T[] Since<T>(int mark, string connection, string method) => Sent(mark, connection).Where(sent => sent.Method == method).Select(sent => (T)sent.Payload!).ToArray();

        public async Task<T> WaitAsync<T>(int mark, string connection, string method, Func<T, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (Since<T>(mark, connection, method).FirstOrDefault(match) is { } found)
                {
                    return found;
                }
                await Task.Delay(20, timeout.Token);
            }
        }

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }
}
