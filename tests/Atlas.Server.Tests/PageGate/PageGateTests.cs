using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;
// The folder's namespace hides the type of the same name.
using Gate = Atlas.Server.Platform.Identity.PageGate;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// PageGate itself, before any handler goes through it: what it finds and in which order, what the resolvers answer, that a
/// page the session already holds comes back as it is under its lock, and through RecordingLocks what the SQL text does not
/// show. That its operations send the statements of today's handlers is in PageGateLockTraceTests, next to those handlers.
/// </summary>
public sealed class PageGateTests
{
    private const string NotFound = "404 -: Stránka nebyla nalezena.";
    private const string OwnText = "Vlastní text odmítnutí.";

    /// <summary>
    /// Each page here meets every later condition too: the moved one is in the trash, both trashed ones are another author's
    /// unpublished drafts the Reader may not see, and the Reader may edit none. So the state is the first check that applies.
    /// The Reader asks for EditContent through all three operations; a read may ask for it (history, editor assist).
    /// The states that end before the decision are pinned by their statements as well, once here for every message that goes
    /// through the gate: PageGateTrashedPageTests pins the same per message only while messages still open a page themselves.
    /// The Smoke tag is deliberate: PageGate lives under Platform, and for a change there tests/changed.ps1 runs only the Smoke
    /// tests, as tests/smoke.ps1 always does. This decision table is what a change to the gate must not break.
    /// </summary>
    [Trait("Category", "Smoke")]
    [PostgreSqlTheory]
    [InlineData("Read")]
    [InlineData("Edit")]
    [InlineData("Restructure")]
    public async Task The_first_of_missing_moved_trashed_hidden_forbidden_decides_and_each_resolver_answers_it(string operation)
    {
        await using var f = await PageGateFixture.CreateAsync();
        var locks = RecordingLocks.Start();
        var elsewhere = await f.Host.SeedSpaceAsync("Elsewhere", (f.Reader, SpaceRole.Admin));
        var published = await f.PublishedPageAsync("Published");
        var draft = await DraftAsync(f, "Draft");
        var trashed = await DraftAsync(f, "Trashed");
        var moved = await DraftAsync(f, "Moved");
        foreach (var id in new[] { trashed, moved })
        {
            // CK_Page_Trash: a trashed row carries all three trash columns.
            await f.Host.ExecuteAsync("UPDATE \"Page\" SET \"IsTrashed\"=true,\"TrashedAt\"=now(),\"TrashedById\"=\"OwnerId\",\"TrashBatchId\"=gen_random_uuid() WHERE \"Id\"=:id", id);
        }
        var movedAnswer = operation == "Read" ? NotFound : "409 space_changed: Stránka byla přesunuta. Načtěte ji znovu.";
        var sent = new Dictionary<PageOpeningState, IReadOnlyList<string>>();

        Assert.Equal(new Seen(PageOpeningState.Missing, null, NotFound, NotFound, NotFound), await OpenAsync(Guid.NewGuid(), f.Reader));
        // The page moves to a space the Reader administers, after the gate looked up its space and before it locks that space.
        locks.BeforeNextSpaceLock(() => f.Host.SeedAsync(session => session.CreateSQLQuery("""UPDATE "Page" SET "SpaceId"=:space WHERE "Id"=:page""")
            .SetParameter("space", elsewhere).SetParameter("page", moved).ExecuteUpdateAsync()));
        Assert.Equal(new Seen(PageOpeningState.Moved, f.Reader.Id, movedAnswer, NotFound, movedAnswer), await OpenAsync(moved, f.Reader));
        Assert.Equal(new Seen(PageOpeningState.Trashed, f.Reader.Id, NotFound, NotFound, NotFound), await OpenAsync(trashed, f.Reader));
        Assert.Equal(new Seen(PageOpeningState.Hidden, f.Reader.Id, NotFound, NotFound, NotFound), await OpenAsync(draft, f.Reader));
        Assert.Equal(new Seen(PageOpeningState.Forbidden, f.Reader.Id, "403 -: K této operaci potřebujete oprávnění k úpravám stránky.", NotFound, "403 -: " + OwnText),
            await OpenAsync(published.Id, f.Reader));
        Assert.Equal(new Seen(PageOpeningState.Open, f.Writer.Id, "page", "page", "page"), await OpenAsync(published.Id, f.Writer));

        // What the gate sends before it stops. For an unknown id nothing is locked but the content key. A page that moved or
        // is in the trash costs the ladder up to its row lock and not one statement more: it is not decided for the account,
        // so no grant query follows, where a page that is decided costs three (PageGateLockTraceTests).
        string[] ladder = operation switch
        {
            "Read" => ["advisory shared", "read Page.SpaceId", "Space FOR SHARE", "UserAccount FOR SHARE", "set actor", "Page FOR SHARE"],
            "Edit" => ["advisory shared", "read Page.SpaceId", "Space FOR SHARE", "UserAccount FOR SHARE", "set actor", "Page FOR UPDATE"],
            _ => ["advisory shared", "read Page.SpaceId", "Space FOR UPDATE", "Space FOR SHARE", "UserAccount FOR SHARE", "set actor", "Page FOR UPDATE"]
        };
        Assert.Equal((2, "advisory shared | read Page.SpaceId"), Cost(PageOpeningState.Missing));
        Assert.Equal((ladder.Length, string.Join(" | ", ladder)), Cost(PageOpeningState.Moved));
        Assert.Equal((ladder.Length, string.Join(" | ", ladder)), Cost(PageOpeningState.Trashed));
        Assert.Equal(ladder.Length + 3, sent[PageOpeningState.Open].Count);

        Task<Seen> OpenAsync(Guid pageId, UserAccount actor)
        {
            return f.ThroughGateAsync(actor, async (gate, _) =>
            {
                using var recording = SqlRecording.Start();
                var seen = operation switch
                {
                    "Read" => Describe(await gate.ReadAsync(pageId, AccessOperation.EditContent, default)),
                    "Edit" => Describe(await gate.EditAsync(pageId, AccessOperation.EditContent, default)),
                    "Restructure" => Describe(await gate.RestructureAsync(pageId, AccessOperation.EditContent, default)),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
                };
                // The move of this test is committed by another session inside the gate's call; it is not a statement of the gate.
                sent[seen.State] = recording.Statements.Where(sql => !sql.StartsWith("UPDATE \"Page\" SET \"SpaceId\"", StringComparison.Ordinal)).ToArray();
                return seen;
            });
        }

        (int Statements, string Steps) Cost(PageOpeningState state)
        {
            return (sent[state].Count, string.Join(" | ", LockTrace.Of(sent[state])));
        }
    }

    [PostgreSqlFact]
    public async Task A_page_the_session_already_holds_comes_back_as_it_is_under_its_lock()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Held");
        const string change = "UPDATE \"Page\" SET \"Version\"=\"Version\"+1 WHERE \"Id\"=:id";

        await f.ThroughGateAsync(f.Writer, async (gate, session) =>
        {
            var held = await session.GetAsync<Page>(page.Id);
            var loaded = held.Version;
            // Another transaction changes the row behind the instance this session holds.
            await f.Host.ExecuteAsync(change, page.Id);
            using var recording = SqlRecording.Start();
            Assert.Same(held, (await gate.ReadAsync(page.Id, AccessOperation.ReadContent, default)).Require().Page);
            Assert.Equal(loaded + 1, held.Version);
            // The held instance names its space, so no lookup. FOR SHARE costs a held page two statements, the lock and the
            // re-read: NHibernate has no shared lock mode, and a load cannot overwrite an instance the session holds.
            Assert.Equal(["advisory shared", "Space FOR SHARE", "UserAccount FOR SHARE", "set actor", "Page FOR SHARE", "read Page"],
                LockTrace.Of(recording.Statements));
            return true;
        });

        await f.ThroughGateAsync(f.Writer, async (gate, session) =>
        {
            var held = await session.GetAsync<Page>(page.Id);
            var loaded = held.Version;
            await f.Host.ExecuteAsync(change, page.Id);
            using (var recording = SqlRecording.Start())
            {
                Assert.Same(held, (await gate.EditAsync(page.Id, AccessOperation.EditContent, default)).Require().Page);
                Assert.Equal(loaded + 1, held.Version);
                // FOR UPDATE re-reads the held instance in the statement that locks it.
                Assert.Equal(["advisory shared", "read Page.SpaceId", "Space FOR SHARE", "UserAccount FOR SHARE", "set actor", "Page FOR UPDATE"],
                    LockTrace.Of(recording.Statements));
            }
            using (var recording = SqlRecording.Start())
            {
                // Held FOR UPDATE by this transaction now: an edit gate sends nothing more for the page row, nor for the account,
                // which stays locked and checked until the transaction ends.
                Assert.Same(held, (await gate.EditAsync(page.Id, AccessOperation.EditContent, default)).Require().Page);
                Assert.Equal(["read Page.SpaceId"], LockTrace.Of(recording.Statements));
            }
            using (var recording = SqlRecording.Start())
            {
                // The read-back of a command: the steps that end the trace of SavePage in PageGateLockTraceTests.
                Assert.Same(held, (await gate.ReadAsync(page.Id, AccessOperation.ReadContent, default)).Require().Page);
                Assert.Equal(["Page FOR SHARE", "read Page"], LockTrace.Of(recording.Statements));
            }
            return true;
        });
    }

    /// <summary>
    /// An edit gate decides the operation it is asked for and no other. The state theory asks for EditContent throughout, which
    /// a gate that always decided EditContent would pass; a Writer edits this page and does not manage it, so ManageAccess must
    /// be Forbidden for the Writer, with the text of that operation, and Open for the space Admin. And an edit gate that decided
    /// ReadContent or Share would hand an EditablePage, which says "may edit", to an account that may not.
    /// </summary>
    [PostgreSqlTheory]
    [InlineData("Edit")]
    [InlineData("Restructure")]
    public async Task An_edit_gate_decides_the_operation_it_is_asked_for_and_none_that_does_not_allow_editing(string operation)
    {
        await using var f = await PageGateFixture.CreateAsync();
        var admin = await f.Host.SeedUserAsync("spaceadmin");
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = admin.Id, Role = (int)SpaceRole.Admin }));
        var page = await f.PublishedPageAsync("Managed");

        Assert.Equal(new Seen(PageOpeningState.Open, f.Writer.Id, "page", "page", "page"), await OpenAsync(f.Writer, AccessOperation.EditContent));
        Assert.Equal(new Seen(PageOpeningState.Forbidden, f.Writer.Id, "403 -: K této operaci potřebujete oprávnění ke správě stránky.", NotFound, "403 -: " + OwnText),
            await OpenAsync(f.Writer, AccessOperation.ManageAccess));
        Assert.Equal(new Seen(PageOpeningState.Open, admin.Id, "page", "page", "page"), await OpenAsync(admin, AccessOperation.ManageAccess));
        foreach (var notAnEdit in new[] { AccessOperation.ReadContent, AccessOperation.Share })
        {
            // The space Admin holds both operations on the page, so only the gate's own rule refuses them.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OpenAsync(admin, notAnEdit));
        }

        Task<Seen> OpenAsync(UserAccount actor, AccessOperation asked)
        {
            return f.ThroughGateAsync(actor, async (gate, _) => operation switch
            {
                "Edit" => Describe(await gate.EditAsync(page.Id, asked, default)),
                "Restructure" => Describe(await gate.RestructureAsync(page.Id, asked, default)),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            });
        }
    }

    /// <summary>
    /// What LockTrace cannot see, read from the calls to IAtlasLocks: the advisory lock is the shared content lock and no other,
    /// the space is the page's own and the account the request's, a restructure takes that one space FOR UPDATE before its
    /// FOR SHARE, and a further page under a LockedSpace takes its row lock alone. The gate never locks two spaces or two
    /// accounts, and two pages only in the order its caller asks for them, so it has no id order of its own to keep.
    /// </summary>
    [PostgreSqlFact]
    public async Task The_gate_locks_the_content_key_the_pages_own_space_and_the_requests_account_and_a_further_page_alone()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var locks = RecordingLocks.Start();
        var page = await f.PublishedPageAsync("First");
        var second = await f.PublishedPageAsync("Second");
        var elsewhere = await f.Host.SeedSpaceAsync("Elsewhere", (f.Writer, SpaceRole.Writer));
        var foreign = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(elsewhere, null, "Foreign", PageGateFixture.Rich("Foreign"), "Foreign")));
        string[] entered = ["content shared", $"space {f.SpaceId} FOR SHARE", $"account {f.Writer.Id} FOR SHARE", $"actor {f.Writer.Id}"];
        string[] enteredForChange = ["content shared", $"space {f.SpaceId} FOR UPDATE", .. entered];

        Assert.Equal(["content shared", .. entered, $"page {page.Id} FOR SHARE"],
            await CallsAsync(gate => gate.ReadAsync(page.Id, AccessOperation.ReadContent, default)));
        // The page FOR UPDATE is a locking load of the session, not a call to IAtlasLocks; LockTrace shows it.
        Assert.Equal(["content shared", .. entered], await CallsAsync(gate => gate.EditAsync(page.Id, AccessOperation.EditContent, default)));
        Assert.Equal(enteredForChange, await CallsAsync(gate => gate.RestructureAsync(page.Id, AccessOperation.ManageAccess, default)));
        Assert.Equal(enteredForChange, await CallsAsync(gate => gate.EnterSpaceForChangeAsync(f.SpaceId, default)));

        await f.ThroughGateAsync(f.Reader, async (gate, _) =>
        {
            locks.Clear();
            var space = await gate.EnterSpaceAsync(f.SpaceId, default);
            Assert.Equal(["content shared", $"space {f.SpaceId} FOR SHARE", $"account {f.Reader.Id} FOR SHARE", $"actor {f.Reader.Id}"], locks.Calls);
            Assert.Equal((f.SpaceId, f.Reader.Id), (space.SpaceId, space.Actor.Id));
            locks.Clear();
            using var recording = SqlRecording.Start();
            var first = (await space.ReadPageAsync(page.Id, AccessOperation.ReadContent, default)).Require();
            var further = (await space.ReadPageAsync(second.Id, AccessOperation.ReadContent, default)).Require();
            var other = await space.ReadPageAsync(foreign.Id, AccessOperation.ReadContent, default);
            Assert.Equal([$"page {page.Id} FOR SHARE", $"page {second.Id} FOR SHARE", $"page {foreign.Id} FOR SHARE"], locks.Calls);
            Assert.Equal(["Page FOR SHARE", "Page FOR SHARE", "Page FOR SHARE"], LockTrace.Of(recording.Statements));
            Assert.Equal((page.Id, second.Id, f.Reader.Id), (first.Page.Id, further.Page.Id, further.Actor.Id));
            // The Reader reads the publication and may not edit; the decision travels with the page.
            Assert.False(first.CanEdit);
            // A page of another space is not one of the entered space, whatever the account may do there.
            Assert.Equal(PageOpeningState.Moved, other.State);
            return true;
        });

        // The way back from an opened page to its space: a further page under the same locks takes its row lock alone, as if
        // the caller had entered the space itself. An edited page leads back to its space too.
        await f.ThroughGateAsync(f.Reader, async (gate, _) =>
        {
            var main = (await gate.ReadAsync(page.Id, AccessOperation.ReadContent, default)).Require();
            locks.Clear();
            using var recording = SqlRecording.Start();
            var further = (await main.Space.ReadPageAsync(second.Id, AccessOperation.ReadContent, default)).Require();
            Assert.Equal([$"page {second.Id} FOR SHARE"], locks.Calls);
            Assert.Equal(["Page FOR SHARE"], LockTrace.Of(recording.Statements));
            Assert.Same(main.Space, further.Space);
            Assert.Equal((f.SpaceId, f.Reader.Id), (main.Space.SpaceId, main.Space.Actor.Id));
            // The page says which operation was decided for it.
            Assert.Equal(AccessOperation.ReadContent, main.Operation);
            return true;
        });
        Assert.Equal((f.SpaceId, AccessOperation.EditContent), await f.ThroughGateAsync(f.Writer, async (gate, _) =>
        {
            var edited = (await gate.EditAsync(page.Id, AccessOperation.EditContent, default)).Require();
            return (edited.Space.SpaceId, edited.Operation);
        }));

        // The Writer reads the working revision: the same read says so.
        Assert.True(await f.ThroughGateAsync(f.Writer, async (gate, _) => (await gate.ReadAsync(page.Id, AccessOperation.ReadContent, default)).Require().CanEdit));

        async Task<IReadOnlyList<string>> CallsAsync(Func<Gate, Task> call)
        {
            return await f.ThroughGateAsync(f.Writer, async (gate, _) =>
            {
                locks.Clear();
                await call(gate);
                return locks.Calls;
            });
        }
    }

    /// <summary>
    /// A space says whether the gate took its row FOR UPDATE, which only EnterSpaceForChangeAsync and RestructureAsync do. What
    /// inserts a page takes the space as its proof and requires that of it (RequireHeldForChange): the BEFORE INSERT trigger
    /// of Page takes the space FOR UPDATE, and an insert under FOR SHARE alone upgrades it (40P01, c54d1c0f). Every opened
    /// page hands out its space, so the space of a page that was only read or edited must not pass.
    /// </summary>
    [PostgreSqlFact]
    public async Task Only_a_space_the_gate_took_for_update_passes_as_entered_for_change()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var locks = RecordingLocks.Start();
        var page = await f.PublishedPageAsync("Page");

        Assert.False(await ForChangeAsync(gate => gate.EnterSpaceAsync(f.SpaceId, default)));
        Assert.False(await ForChangeAsync(async gate => (await gate.ReadAsync(page.Id, AccessOperation.ReadContent, default)).Require().Space));
        Assert.False(await ForChangeAsync(async gate => (await gate.EditAsync(page.Id, AccessOperation.EditContent, default)).Require().Space));
        Assert.True(await ForChangeAsync(gate => gate.EnterSpaceForChangeAsync(f.SpaceId, default)));
        Assert.True(await ForChangeAsync(async gate => (await gate.RestructureAsync(page.Id, AccessOperation.EditContent, default)).Require().Space));
        // A further page under a space entered for change leads back to that space, not to a weaker one.
        Assert.True(await ForChangeAsync(async gate =>
            (await (await gate.EnterSpaceForChangeAsync(f.SpaceId, default)).ReadPageAsync(page.Id, AccessOperation.ReadContent, default)).Require().Space));

        // It is the row lock that counts, not the name of the operation: a space that did not exist at the FOR UPDATE and was
        // committed before the FOR SHARE is held FOR SHARE alone.
        var late = Guid.NewGuid();
        locks.BeforeNextSpaceLock(() =>
        {
            locks.BeforeNextSpaceLock(() => f.Host.SeedAsync(session => session.SaveAsync(new Space { Id = late, Name = "Late" })));
            return Task.CompletedTask;
        });
        Assert.False(await ForChangeAsync(gate => gate.EnterSpaceForChangeAsync(late, default)));

        // Entered for change is no more than the locks of its transaction: after it the space proves nothing.
        var kept = await f.ThroughGateAsync(f.Writer, (gate, _) => gate.EnterSpaceForChangeAsync(f.SpaceId, default));
        Assert.True(kept.ForChange);
        Assert.Throws<InvalidOperationException>(kept.RequireHeldForChange);

        Task<bool> ForChangeAsync(Func<Gate, Task<LockedSpace>> enter)
        {
            return f.ThroughGateAsync(f.Writer, async (gate, _) =>
            {
                var space = await enter(gate);
                space.RequireHeld();
                // The check of what inserts a page agrees with what the space says, and refuses as a programming error.
                var refusal = Record.Exception(space.RequireHeldForChange);
                Assert.Equal(space.ForChange, refusal is null);
                Assert.True(refusal is null or InvalidOperationException, refusal?.ToString());
                return space.ForChange;
            });
        }
    }

    /// <summary>
    /// What inserts a page asks the gate of its own scope whether the space it was given was entered for change
    /// (RequireOwnForChange), and not the space: the space answers for the gate that entered it, about that gate's session. A
    /// space another scope entered for change is held FOR UPDATE over there while that scope's transaction runs, and here it
    /// proves nothing: the insert would go out on this session, whose transaction never locked the space.
    /// </summary>
    [PostgreSqlFact]
    public async Task What_inserts_a_page_is_refused_a_space_another_scope_entered_for_change()
    {
        await using var f = await PageGateFixture.CreateAsync();
        f.Host.Actor.Current = f.Writer;
        using var entering = f.Host.Services.CreateScope();
        using var taken = entering.ServiceProvider.GetRequiredService<NHibernate.ISession>().BeginTransaction();
        var gate = entering.ServiceProvider.GetRequiredService<Gate>();
        var space = await gate.EnterSpaceForChangeAsync(f.SpaceId, default);
        // Entered once more, without the row FOR UPDATE: this one says no to its own gate as well.
        var shared = await gate.EnterSpaceAsync(f.SpaceId, default);

        // Another scope in a transaction of its own.
        using var inserting = f.Host.Services.CreateScope();
        using var own = inserting.ServiceProvider.GetRequiredService<NHibernate.ISession>().BeginTransaction();
        var other = inserting.ServiceProvider.GetRequiredService<Gate>();

        // The space says yes for itself, since the gate that entered it still holds it for change. Only that gate takes it.
        space.RequireHeldForChange();
        gate.RequireOwnForChange(space);
        Assert.Throws<InvalidOperationException>(() => other.RequireOwnForChange(space));
        Assert.Throws<InvalidOperationException>(() => gate.RequireOwnForChange(shared));
    }

    /// <summary>
    /// A capability stands for locks, and locks end with their transaction. A space or a page kept and used in a later
    /// transaction of the same scope is a programming error: it fails as one, before any statement is sent, and never as an
    /// answer to the client.
    /// The check asks two things, that the transaction is still active and that it is the session's current one, and this
    /// test passes with either alone: in this NHibernate version an ended transaction is never active and never the
    /// session's current one, so each implies the other. Both stay, as in ContentAccessService, so the check does not rest
    /// on that.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_capability_used_after_its_transaction_fails_as_a_programming_error_before_any_statement()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Read");
        var other = await f.PublishedPageAsync("Edited");
        f.Host.Actor.Current = f.Writer;
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        var gate = scope.ServiceProvider.GetRequiredService<Gate>();
        LockedSpace space;
        LockedPage read;
        EditablePage edited;
        using (var taken = session.BeginTransaction())
        {
            space = await gate.EnterSpaceAsync(f.SpaceId, default);
            read = (await space.ReadPageAsync(page.Id, AccessOperation.ReadContent, default)).Require();
            edited = (await gate.EditAsync(other.Id, AccessOperation.EditContent, default)).Require();
            // Good while the transaction that took the locks runs.
            space.RequireHeld();
            read.RequireHeld();
            edited.RequireHeld();
            await taken.CommitAsync();
        }
        Assert.Throws<InvalidOperationException>(space.RequireHeld);

        using var later = session.BeginTransaction();
        using var recording = SqlRecording.Start();
        Assert.Throws<InvalidOperationException>(space.RequireHeld);
        Assert.Throws<InvalidOperationException>(read.RequireHeld);
        Assert.Throws<InvalidOperationException>(edited.RequireHeld);
        await Assert.ThrowsAsync<InvalidOperationException>(() => space.ReadPageAsync(page.Id, AccessOperation.ReadContent, default));
        Assert.Empty(recording.Statements);
    }

    private static async Task<Guid> DraftAsync(PageGateFixture f, string title)
    {
        return (await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, title, PageGateFixture.Rich(title), title)))).Id;
    }

    private static Seen Describe<TPage>(PageOpening<TPage> opening) where TPage : class
    {
        return new Seen(opening.State, opening.Actor?.Id, Answer(() => opening.Require()), Answer(() => opening.RequireHidden()),
            Answer(() => opening.Require(OwnText)));
    }

    private static string Answer(Func<object> resolve)
    {
        var refusal = Record.Exception(() => { resolve(); });
        if (refusal is null)
        {
            return "page";
        }
        var refused = Assert.IsType<ApiException>(refusal);
        return $"{refused.Status} {refused.Code ?? "-"}: {refused.Message}";
    }

    /// <summary>A gate's finding and the answers of Require(), RequireHidden() and Require(own text): "page" or status, code and text.</summary>
    private sealed record Seen(PageOpeningState State, Guid? ActorId, string Require, string RequireHidden, string RequireOwnText);
}
