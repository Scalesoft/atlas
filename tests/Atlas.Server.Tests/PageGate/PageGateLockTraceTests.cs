using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.AiSuggestions.Queries;
using Atlas.Server.Features.AiWriting.Commands;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Queries;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.Mcp.Queries;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Linking;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// The lock order of the copies of the page gate, as each representative message takes it today (LockTrace explains the steps).
/// A change of the order, of a strength or of the place of an unlocked read shows as a diff of the traces below. Lines after
/// the page lock of a command belong to its work and to the read-back of its answer (PageReader locks the page again; the account
/// stays locked and checked from the ladder to the end of the transaction, so it is not read again).
/// The three operations of PageGate are spelled out statement for statement, as SQL (LockTrace.Shape): nine, nine and ten
/// statements, the ladder and then the decision. Each is also held against a message, GetPage, SavePage and RenamePage, which
/// begins with exactly those statements. While the message opens its page itself, that shows that converting it changes
/// nothing; once it goes through the gate it compares the gate with itself and says only that the handler does its gate
/// first, which is why the ladders are literals.
/// Since 2026110930 what the rows of pages bring along are statements of their own (ContentRowEffects): a new revision one (the
/// contribution and the live notification, sent with the page's envelope since they are read by nothing in their flush), a page's
/// new version one (its envelope), a publication one (the notification), and an
/// insert two (its space FOR UPDATE with its place, then its envelope and history). Since 2026110935 a change of a page's working
/// or published revision or title adds one more: the refresh of its search document (SearchDocuments). Since 2026110941 an insert
/// adds two: the access mode's history (PageAccessModeHistory: one statement decides and closes the current revision, one writes the new one).
/// The traces cover only the messages below: the trace of any other copy is added in the commit before its slice converts,
/// as the rows of PageGateMovedPageTests are.
/// </summary>
public sealed class PageGateLockTraceTests
{
    /// <summary>The decision of a visible page by IContentAccess, after the page row lock: the wiki administrator check (once per
    /// transaction), the grants of the space and the grants of the page. Their conditions belong to the grant queries and are not pinned here.</summary>
    private const string Decision = """
        SELECT "HubVerifiedUntil" FROM "EffectiveWikiAdmin" WHERE "UserId"=:p0
        SELECT ... FROM "SpaceGrant" this_ WHERE ...
        SELECT ... FROM "ResourceGrant" this_ WHERE ...
        """;

    private readonly List<(string Message, IReadOnlyList<string> Steps)> m_traces = [];
    private readonly List<string> m_differences = [];

    /// <summary>
    /// The Smoke tag is deliberate: PageGate, the content access service, the locks and the locking load of the session live
    /// under Platform, and a change there runs only the Smoke tests in tests/changed.ps1 and tests/smoke.ps1. This is the one
    /// test in which a changed lock order shows.
    /// </summary>
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Representative_messages_take_their_locks_in_this_order_and_never_upgrade_a_space_or_page_row()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Trace");
        var firstRevision = await f.RevisionAsync(page.Id, 1);

        // A reader's read takes the publication its page inherits (3.6-I Živě) from the space lock, without a statement.
        var getPage = Expect("GetPage", await f.RecordAsync<PageDto>(f.Reader, new GetPage(page.Id)), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR SHARE
            """);
        // A query commits without the WAL flush, which the actor statement asks for.
        ExpectGate("ReadAsync", """
            SELECT pg_advisory_xact_lock_shared(:p0)
            SELECT this_."SpaceId" as y0_ FROM "Page" this_ WHERE this_."Id" = :p0
            SELECT "PublicationMode" FROM "Space" WHERE "Id"=:p0 FOR SHARE
            SELECT ... FROM "UserAccount" row WHERE row."Id"=:p0 FOR SHARE
            SELECT set_config('synchronous_commit','off',true) WHERE NOT EXISTS (SELECT 1 FROM "RevokedBrowserSession" WHERE "Id" = :p0)
            SELECT ... FROM "Page" row WHERE row."Id"=:p0 FOR SHARE
            """, getPage, await f.RecordGateAsync(f.Reader, gate => gate.ReadAsync(page.Id, AccessOperation.ReadContent, default), new GetPage(page.Id)));
        var savePage = Expect("SavePage", await f.RecordAsync<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Trace", PageGateFixture.Rich("Second"), "Second", 1))), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR UPDATE
            update Page
            PageCollaboration FOR UPDATE
            Page FOR SHARE
            read Page
            """, 22); // 26 - 3: the read-back keeps the locked account; - 1: the revision's contribution goes with the envelope
        ExpectGate("EditAsync", """
            SELECT pg_advisory_xact_lock_shared(:p0)
            SELECT this_."SpaceId" as y0_ FROM "Page" this_ WHERE this_."Id" = :p0
            SELECT "PublicationMode" FROM "Space" WHERE "Id"=:p0 FOR SHARE
            SELECT ... FROM "UserAccount" row WHERE row."Id"=:p0 FOR SHARE
            SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM "RevokedBrowserSession" WHERE "Id" = :p0)
            SELECT ... FROM "Page" page0_ WHERE page0_."Id"=:p0 for update
            """, savePage, await f.RecordGateAsync(f.Writer, gate => gate.EditAsync(page.Id, AccessOperation.EditContent, default)));
        Expect("PublishPage", await f.RecordAsync<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(2))), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR UPDATE
            read Page
            update Page
            Page FOR SHARE
            read Page
            """, 21); // 24 - 3: the read-back keeps the locked account
        Expect("RestorePage", await f.RecordAsync<PageDto>(f.Writer, new RestorePage(page.Id, new RestorePageRequest(firstRevision, 2))), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR UPDATE
            update Page
            PageCollaboration FOR UPDATE
            Page FOR SHARE
            read Page
            """, 24); // 28 - 3: the read-back keeps the locked account; - 1: the revision's contribution goes with the envelope
        var renamePage = Expect("RenamePage", await f.RecordAsync(f.Writer, new RenamePage(page.Id, new RenamePageRequest("Renamed", 3))), """
            advisory shared
            read Page.SpaceId
            Space FOR UPDATE
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR UPDATE
            update Page
            """, 19); // +1: a rename waits while a live write's append is pending (reads PageCollaboration under the page lock); - 1: the
            // revision's contribution goes with the envelope
        ExpectGate("RestructureAsync", """
            SELECT pg_advisory_xact_lock_shared(:p0)
            SELECT this_."SpaceId" as y0_ FROM "Page" this_ WHERE this_."Id" = :p0
            SELECT "Id" FROM "Space" WHERE "Id"=:p0 FOR UPDATE
            SELECT "PublicationMode" FROM "Space" WHERE "Id"=:p0 FOR SHARE
            SELECT ... FROM "UserAccount" row WHERE row."Id"=:p0 FOR SHARE
            SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM "RevokedBrowserSession" WHERE "Id" = :p0)
            SELECT ... FROM "Page" page0_ WHERE page0_."Id"=:p0 for update
            """, renamePage, await f.RecordGateAsync(f.Writer, gate => gate.RestructureAsync(page.Id, AccessOperation.EditContent, default)));
        Expect("CreatePage under a parent", await f.RecordAsync<PageDto>(f.Writer,
            new CreatePage(new CreatePageRequest(f.SpaceId, page.Id, "Child", PageGateFixture.Rich("Child"), "Child"))), """
            advisory shared
            Space FOR UPDATE
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR SHARE
            Space FOR UPDATE
            insert Page
            read Space
            Page FOR SHARE
            read Page
            """, 25); // 26 + the read of the parent decoration, which the new page inherits (plan 3.5 E), which takes no lock; - 3: the
            // read-back keeps the locked account; + 1 (5.2): the space's verification interval, whether the new page is verified
        Expect("AuthorizeAttachmentUpload", await f.RecordAsync(f.Writer, new AuthorizeAttachmentUpload(page.Id)), """
            advisory shared
            read Page
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            read Page
            """);
        var upload = await f.StoredAsync(page.Id);
        Expect("StoreAttachment", await f.RecordAsync<AttachmentDto>(f.Writer, upload), """
            advisory shared
            read Page
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR UPDATE
            read Page
            """);
        Expect("OpenAttachment", await f.RecordAsync<AttachmentContentDto>(f.Writer, new OpenAttachment(upload.Id, false)), """
            advisory shared
            read Page
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR SHARE
            read Page
            """);
        Expect("RecordPageVisit", await f.RecordAsync<PersonalPageState>(f.Reader, new RecordPageVisit(page.Id)), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            advisory exclusive (text key)
            UserAccount FOR SHARE
            set actor
            Page FOR SHARE
            """, 15); // the visit list is trimmed by OperationalRetention, not on every visit (ARCH3-1)
        var live = await f.As<WikiCollaborationSession>(f.Writer, new OpenCollaboration(page.Id));
        Expect("AppendUpdate", await f.RecordAsync<AppendUpdateResult>(f.Writer,
            new AppendUpdate(page.Id, live.Generation, Guid.NewGuid(), f.Writer.Id, DateTimeOffset.UtcNow, [1, 2, 3])), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            Page FOR UPDATE
            """);
        // 5.2: the tree also reads the verification states of the space's pages (read Page), without a lock.
        Expect("GetPageTree", await f.RecordAsync<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId)), """
            advisory shared
            read Space
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            read Page
            """);

        Assert.True(m_differences.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, m_differences));
        // The detector sees the shape of c54d1c0f (a create that held the space only FOR SHARE), and none of the traces has it.
        Assert.Equal(["insert Page after Space FOR SHARE"], LockTrace.Upgrades(["advisory shared", "Space FOR SHARE", "insert Page"]));
        Assert.Empty(m_traces.SelectMany(trace => LockTrace.Upgrades(trace.Steps).Select(upgrade => $"{trace.Message}: {upgrade}")));
    }

    /// <summary>
    /// The page reads that open their page themselves, outside PageReader, with the number of statements each sends: a copy
    /// that moves to PageGate keeps both. The lock steps do not show a grant query more or less; the count does.
    /// </summary>
    [PostgreSqlFact]
    public async Task Page_reads_outside_PageReader_take_their_locks_in_this_order_and_send_this_many_statements()
    {
        // Own host: turns the AI switch on (the current AiSetting row, one for the whole installation; a second save without its version is a 409).
        await using var f = await PageGateFixture.CreateAsync(isolated: true);
        var admin = await f.Host.SeedUserAsync("admin", isWikiAdmin: true);
        var page = await f.PublishedPageAsync("Trace");
        // A template with a heading: the outline of the writing assistant reads it and keeps it as a dependency.
        var template = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Template",
            McpSite.Rich(McpSite.Heading(2, "osnova", "Osnova"), McpSite.Paragraph("Text")), "## Osnova\n\nText")));
        await f.As<PageDto>(f.Writer, new PublishPage(template.Id, new PublishPageRequest(template.Version)));
        await f.As<PageDto>(admin, new SetPageTemplate(template.Id, true));
        await f.As<AiSwitchView>(admin, new SaveAiSwitch(new SaveAiSwitchRequest(true, null)));
        await f.Host.GrantAiAsync(f.Writer);
        const string readLadder = """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR SHARE
            """;

        Expect("GetPageConnections", await f.RecordAsync<PageConnections>(f.Reader, new GetPageConnections(page.Id, 10)), readLadder, 10);
        Expect("FindLinkCandidates", await f.RecordAsync<LinkCandidateLookup>(f.Writer, new FindLinkCandidates(page.Id, [])), readLadder, 12);
        WritingContext outline;
        using (var recording = SqlRecording.Start())
        {
            outline = await f.As<WritingContext>(f.Writer, new WritingRead(page.Id, "editor.outline", "Topic"));
            // The templates of the space are listed, then each is locked: the session holds it by then, so the lock re-reads it.
            Expect("WritingRead for an outline", recording.Statements, readLadder + """

                read Page
                Page FOR SHARE
                read Page
                """, 19);
        }
        Assert.Equal(template.Id, Assert.Single(outline.Templates).PageId);
        // The second read of a writing request: the page again and the template it depends on, which is not held this time.
        Expect("WritingRead again with its template", await f.RecordAsync<WritingContext>(f.Writer, new WritingRead(page.Id, "editor.outline", "", Expected: outline)),
            readLadder + """

                Page FOR SHARE
                """, 17);
        // The history reads. Before PageGate they locked the page row and loaded it in a second statement (read Page, 14 each).
        var firstRevision = await f.RevisionAsync(page.Id, 1);
        Expect("GetPageHistory", await f.RecordAsync<PageHistoryList>(f.Writer, new GetPageHistory(page.Id, null, 20)), readLadder, 12);
        Expect("GetPageRevision", await f.RecordAsync<PageHistoryRevision>(f.Writer, new GetPageRevision(page.Id, firstRevision)), readLadder, 12);
        // The personal state of a page, read. Its write, RecordPageVisit, is with the representative messages.
        Expect("GetPersonalPageState", await f.RecordAsync<PersonalPageState>(f.Reader, new GetPersonalPageState(page.Id)), readLadder, 11);

        Assert.True(m_differences.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, m_differences));
        Assert.Empty(m_traces.SelectMany(trace => LockTrace.Upgrades(trace.Steps).Select(upgrade => $"{trace.Message}: {upgrade}")));
    }

    /// <summary>
    /// The reads through PageReader that the other tests do not count: PageReader serves every message that answers with a
    /// page and the reads built on it, so its move to PageGate moves them all at once and may change none. Counted here: the
    /// read of an editor, of the working revision and of the publication (asked for over MCP), the reads of the access tab
    /// and of the MCP writes, and the read-back of a save that changed nothing. GetPage by a reader and a saving SavePage are
    /// in PageGateStatementCountTests; the read-backs of the other commands follow in the next test.
    /// </summary>
    [PostgreSqlFact]
    public async Task Messages_that_read_through_PageReader_take_their_locks_in_this_order_and_send_this_many_statements()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var admin = await f.Host.SeedUserAsync("admin", isWikiAdmin: true);
        var agent = new RequestChannel(ContentChannel.Mcp, await TestApiTokens.SeedAsync(f.Host, f.Writer, "Agent"));
        var readingAgent = new RequestChannel(ContentChannel.Mcp, await TestApiTokens.SeedAsync(f.Host, f.Reader, "Reading agent"));
        var page = await f.PublishedPageAsync("Trace");
        const string readLadder = """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR SHARE
            """;
        // After PageReader the message takes the actor for its own work: it holds it locked already, so that sends nothing. Then it
        // reads the space: for the page's path over MCP, for its name in the access tab.
        const string readAndSpace = readLadder + """

            read Space
            """;
        // 5.2: the MCP page also carries its verification, one statement over the page, its space and the verification row.
        const string readAndVerification = readAndSpace + """

            read Page
            """;

        // The working revision is the published one: its version is the publication's, without a statement.
        Expect("GetPage by an editor", await f.RecordAsync<PageDto>(f.Writer, new GetPage(page.Id)), readLadder, 12);
        // G5: every MCP read and write asks once whether its space takes part in AI (SpaceRoles.InAiAsync, one statement per request).
        Expect("GetMcpPage, the working revision", await OverMcpAsync<McpPageRead>(f, f.Writer, agent, new GetMcpPage(page.Id, true)), readAndVerification, 17);
        Expect("GetMcpPage, the publication by an editor", await OverMcpAsync<McpPageRead>(f, f.Writer, agent, new GetMcpPage(page.Id, false)), readAndVerification, 17);
        Expect("GetMcpPage, the publication by a reader", await OverMcpAsync<McpPageRead>(f, f.Reader, readingAgent, new GetMcpPage(page.Id, false)), readAndVerification, 17);
        // A reader is decided and then refused the working revision, before any revision is read; the space lock tells whether the page is live (3.6-I).
        Expect("GetMcpPage, the working revision refused to a reader", await OverMcpAsync<McpPageRead>(f, f.Reader, readingAgent, new GetMcpPage(page.Id, true), 404), readLadder, 10);
        Expect("GetMcpEditBase", await OverMcpAsync<McpEditBase>(f, f.Writer, agent, new GetMcpEditBase(page.Id, 1, MarkdownLinkRequest.None)), readLadder, 13);
        // The access tab and the people directory read the page first, as the content endpoint does.
        Expect("GetPageAccess by a reader", await f.RecordAsync<PageAccessState>(f.Reader, new GetPageAccess(page.Id)), readAndSpace, 14);
        // 13: the first page of the history also reads the Projector catalog marks (review A2-4).
        Expect("GetPageAccessHistory", await f.RecordAsync<PageAccessHistory>(admin, new GetPageAccessHistory(page.Id)), readLadder, 13);
        Expect("SearchPagePeople", await f.RecordAsync<IReadOnlyList<PagePerson>>(f.Writer, new SearchPagePeople(page.Id, "wri")), readLadder, 13);

        var saved = new SavePage(page.Id, new SavePageRequest("Trace", PageGateFixture.Rich("Second"), "Second", 1));
        await f.As<PageDto>(f.Writer, saved);
        // The working revision is ahead of the publication now: the version of the publication is one statement more.
        Expect("GetPage by an editor, with unpublished changes", await f.RecordAsync<PageDto>(f.Writer, new GetPage(page.Id)), readLadder, 13);
        Expect("GetMcpPage, the working revision with unpublished changes", await OverMcpAsync<McpPageRead>(f, f.Writer, agent, new GetMcpPage(page.Id, true)), readAndVerification, 18);
        // The assistant's preview of a publication reads the working revision.
        Expect("PreviewMcpPublish", await OverMcpAsync<McpPublishSummary>(f, f.Writer, agent, new PreviewMcpPublish(page.Id, 2, null)), readAndSpace, 19);
        Expect("SavePage without a change", await f.RecordAsync<PageDto>(f.Writer, saved with { Request = saved.Request with { ExpectedVersion = 2 } }), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR UPDATE
            PageCollaboration FOR UPDATE
            Page FOR SHARE
            read Page
            """, 17);

        Assert.True(m_differences.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, m_differences));
        Assert.Empty(m_traces.SelectMany(trace => LockTrace.Upgrades(trace.Steps).Select(upgrade => $"{trace.Message}: {upgrade}")));
    }

    /// <summary>
    /// The commands that answer with a page, beyond those of the first test: each ends in PageReader, which locks the page again
    /// and reads the page row under its lock (the account it keeps from the ladder). Publish, source and template commands read back as an editor
    /// without a second decision; restore and create read back like any reader of the page. The shared handlers run inside the
    /// MCP commands too, some of them with the page already held by the session. One of those, a draft created from a
    /// template with appended text, is the one message found to take a page row FOR UPDATE that it holds FOR SHARE: the end
    /// of the test says which row and why nothing can wait on it.
    /// </summary>
    [PostgreSqlFact]
    public async Task Commands_that_answer_with_a_page_read_it_back_in_this_order_and_send_this_many_statements()
    {
        // The fixture's clock of the extraction lexicon is stopped, so the first Markdown write of the space reads it and the later ones do not.
        await using var f = await PageGateFixture.CreateAsync();
        var locks = RecordingLocks.Start();
        var admin = await f.Host.SeedUserAsync("admin", isWikiAdmin: true);
        var agent = new RequestChannel(ContentChannel.Mcp, await TestApiTokens.SeedAsync(f.Host, f.Writer, "Agent"));
        var page = await f.PublishedPageAsync("Trace");
        const string editLadder = """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            Page FOR UPDATE
            """;
        const string createLadder = """
            advisory shared
            Space FOR UPDATE
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            """;
        const string readBack = """

            Page FOR SHARE
            read Page
            """;

        // A save and a source replacement validate their input before anything else: that refusal sends no statement. For an
        // unknown id they stop after the lookup, with no account locked. And the source is refused to a writer at the gate,
        // after the decision.
        Expect("SavePage with a blank title", await RefusedAsync<PageDto>(f, f.Writer, new SavePage(page.Id, new SavePageRequest(" ", PageGateFixture.Rich("x"), "x", 1)), 400), "", 0);
        Expect("SavePage with an invalid document", await RefusedAsync<PageDto>(f, f.Writer, new SavePage(page.Id, new SavePageRequest("Trace", "not json", "x", 1)), 400), "", 0);
        Expect("ReplacePageSource with an invalid document", await RefusedAsync<PageDto>(f, admin, new ReplacePageSource(page.Id, new("not json", "x", 1)), 400), "", 0);
        Expect("SavePage of an unknown page",
            await RefusedAsync<PageDto>(f, f.Writer, new SavePage(Guid.NewGuid(), new SavePageRequest("Trace", PageGateFixture.Rich("x"), "x", 1)), 404), "advisory shared\nread Page.SpaceId", 2);
        Expect("ReplacePageSource of an unknown page",
            await RefusedAsync<PageDto>(f, admin, new ReplacePageSource(Guid.NewGuid(), new(PageGateFixture.Rich("x"), "x", 1)), 404), "advisory shared\nread Page.SpaceId", 2);
        Expect("ReplacePageSource refused to a writer",
            await RefusedAsync<PageDto>(f, f.Writer, new ReplacePageSource(page.Id, new(PageGateFixture.Rich("Other"), "Other", 1)), 403), editLadder, 9);

        // Each read-back below sends three statements fewer since the account it locked again stays locked from the ladder, and a new
        // revision one fewer since its contribution goes with the page's envelope.
        // Publish locks the page and refreshes it (read Page); a version that is already published is only read back.
        Expect("PublishPage of the published version", await f.RecordAsync<PageDto>(f.Writer, new PublishPage(page.Id, new PublishPageRequest(1))),
            editLadder + "\nread Page" + readBack, 15);
        // A draft approval is refused to anyone but a wiki administrator before the page row is locked.
        var draft = await f.As<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Reviewed", PageGateFixture.Rich("Reviewed"), "Reviewed")));
        var review = new PublishPage(draft.Id, new PublishPageRequest(1), new SpaceDraftReview(f.SpaceId, await f.RevisionAsync(draft.Id, 1)));
        Expect("PublishPage with a review, refused to a writer", await RefusedAsync<PageDto>(f, f.Writer, review, 403), """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            """, 6);
        Expect("PublishPage with a review", await f.RecordAsync<PageDto>(admin, review), editLadder + "\nread Page\nupdate Page" + readBack, 19);

        Expect("RestorePage of the current revision", await f.RecordAsync<PageDto>(f.Writer, new RestorePage(page.Id, new RestorePageRequest(await f.RevisionAsync(page.Id, 1), 1))),
            editLadder + "\nPageCollaboration FOR UPDATE" + readBack, 18);
        var source = new ReplacePageSource(page.Id, new(PageGateFixture.Rich("Source"), "Source", 1));
        Expect("ReplacePageSource", await f.RecordAsync<PageDto>(admin, source), editLadder + "\nupdate Page\nPageCollaboration FOR UPDATE" + readBack, 21);
        Expect("ReplacePageSource without a change", await f.RecordAsync<PageDto>(admin, source with { Request = source.Request with { ExpectedVersion = 2 } }),
            editLadder + "\nPageCollaboration FOR UPDATE" + readBack, 15);

        // The template flag: the space role is decided before the page is loaded, which reads the Space row.
        var template = await f.PublishedPageAsync("Template");
        const string templateLadder = """
            advisory shared
            read Page.SpaceId
            Space FOR SHARE
            UserAccount FOR SHARE
            set actor
            read Space
            Page FOR UPDATE
            """;
        Expect("SetPageTemplate", await f.RecordAsync<PageDto>(admin, new SetPageTemplate(template.Id, true)), templateLadder + "\nread Page\nupdate Page" + readBack, 15);
        Expect("SetPageTemplate without a change", await f.RecordAsync<PageDto>(admin, new SetPageTemplate(template.Id, true)), templateLadder + readBack, 12);

        // A create reads its page back, and a create from a template reads the template through PageReader first.
        Expect("CreatePage at the root", await f.RecordAsync<PageDto>(f.Writer, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Root", PageGateFixture.Rich("Root"), "Root"))),
            createLadder + "\nread Space\nSpace FOR UPDATE\ninsert Page" + readBack, 22);
        Expect("CreatePage from a template", await f.RecordAsync<PageDto>(f.Writer,
                new CreatePage(new CreatePageRequest(f.SpaceId, null, "From a template", PageGateFixture.Rich("x"), "x", false, template.Id))),
            createLadder + "\nread Page" + readBack + "\nread Space\nSpace FOR UPDATE\ninsert Page" + readBack, 29); // 34 + the read of the template decoration, inherited by the new page (plan 3.5 E); - 6: two read-backs keep the locked account

        // The MCP commands decide G5 under locks of their own and then run the shared handlers in the same transaction.
        const string mcpCreateLadder = createLadder + """

            Space FOR UPDATE
            """;
        // A Markdown write links by default (link: "auto", c868106f): the first one in the space reads its extraction lexicon (contacts,
        // accounts, page titles, uses: four statements, shown as "read Page"); the lexicon is cached, so the later writes read none.
        Expect("CreateMcpDraft", await OverMcpAsync<McpWriteResult>(f, f.Writer, agent,
                new CreateMcpDraft(new(f.SpaceId, null, "Draft", McpSite.Rich(McpSite.Paragraph("Text")), "Text"))),
            mcpCreateLadder + "\nread Space\nread Page\nSpace FOR UPDATE\ninsert Page" + readBack, 29);
        // Text appended to a template is merged before the write, into the template as the read before it saw it: the create
        // copies the template, compares it with what was read, and saves the merged copy as its one revision. No save after it.
        var fromTemplate = new CreatePageRequest(f.SpaceId, null, "Draft 2", "", "", false, template.Id);
        var templateBase = await OverMcpAsync(f, agent, () => f.As<PageMarkdownCreateBase>(f.Writer, new PrepareMcpDraft(fromTemplate, MarkdownLinkRequest.None)));
        var (create, _) = PageMarkdownCreate.Convert(fromTemplate, "Appended text", templateBase.Template, MarkdownLinkTargets.None.Context(() => "appended"));
        locks.Clear();
        McpWriteResult appended = null!;
        Expect("CreateMcpDraft from a template with appended text", await OverMcpAsync(f, agent, async () =>
            {
                using var recording = SqlRecording.Start();
                appended = await f.As<McpWriteResult>(f.Writer, new CreateMcpDraft(create.Request, create.TemplateContentJson));
                return recording.Statements;
            }),
            mcpCreateLadder + "\nread Page" + readBack + "\nread Space\nSpace FOR UPDATE\ninsert Page" + readBack, 32); // 39 + the read of the template decoration, inherited by the new page (plan 3.5 E); - 9: the MCP ladder and two read-backs keep the locked account
        Assert.Equal([$"page {template.Id} FOR SHARE", $"page {appended.PageId} FOR SHARE"], locks.Calls.Where(call => call.StartsWith("page ", StringComparison.Ordinal)));
        // The MCP save decides G5 under the space lock (content shared, space FOR SHARE, the visibility read), then the shared save
        // runs its ladder and finds both held; the publication still looks at the page before the shared handler's locks.
        Expect("SaveMcpDraft", await OverMcpAsync<McpWriteResult>(f, f.Writer, agent,
                new SaveMcpDraft(page.Id, new SavePageRequest("Trace", PageGateFixture.Rich("Agent"), "Agent", 2))),
            "advisory shared\nread Page.SpaceId\nSpace FOR SHARE\nread Page\nread Page.SpaceId\nUserAccount FOR SHARE\nset actor\nPage FOR UPDATE" +
            "\nupdate Page\nPageCollaboration FOR UPDATE" + readBack, 25);

        Assert.True(m_differences.Count == 0, string.Join(Environment.NewLine + Environment.NewLine, m_differences));
        // No message of this test upgrades a row. (A create from a template with appended text used to save the merged text after
        // the copy in the same transaction, FOR UPDATE on the new page its read-back held FOR SHARE; it now converts before the write.)
        Assert.Empty(m_traces.SelectMany(trace => LockTrace.Upgrades(trace.Steps).Select(upgrade => $"{trace.Message}: {upgrade}")));
    }

    /// <summary>
    /// atlas_get_labels decides G5 (the one rule AccessSql.SpaceInAi) after the shared reads took the space FOR SHARE, as
    /// atlas_get_page does, so a switch of the space out of MCP, which writes the space, cannot interleave with the read.
    /// </summary>
    [PostgreSqlFact]
    public async Task GetMcpLabels_decides_G5_under_the_space_lock()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var agent = new RequestChannel(ContentChannel.Mcp, await TestApiTokens.SeedAsync(f.Host, f.Reader, "Agent"));
        var page = await f.PublishedPageAsync("Labels");

        var statements = await OverMcpAsync<McpLabels>(f, f.Reader, agent, new GetMcpLabels(page.Id));

        var spaceLock = statements.ToList().FindIndex(sql => LockTrace.Of([sql]) is ["Space FOR SHARE"]);
        var g5 = statements.ToList().FindIndex(sql => sql.Contains("\"SpaceAiSetting\"", StringComparison.Ordinal));
        Assert.True(spaceLock >= 0 && g5 > spaceLock, string.Join(Environment.NewLine, statements));
    }

    /// <summary>The statements of a message on the channel mcp with the agent's token; with a status, of a message refused with it.</summary>
    private static Task<IReadOnlyList<string>> OverMcpAsync<T>(PageGateFixture f, UserAccount actor, RequestChannel channel, object message, int? refused = null)
    {
        return OverMcpAsync(f, channel, () => refused is { } status ? RefusedAsync<T>(f, actor, message, status) : f.RecordAsync<T>(actor, message));
    }

    private static async Task<T> OverMcpAsync<T>(PageGateFixture f, RequestChannel channel, Func<Task<T>> send)
    {
        f.Host.Channel.Current = channel;
        try
        {
            return await send();
        }
        finally
        {
            f.Host.Channel.Current = RequestChannel.Web;
        }
    }

    /// <summary>The statements of a message that is refused with the status.</summary>
    private static async Task<IReadOnlyList<string>> RefusedAsync<T>(PageGateFixture f, UserAccount actor, object message, int status)
    {
        using var recording = SqlRecording.Start();
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(() => f.As<T>(actor, message))).Status);
        return recording.Statements;
    }

    private IReadOnlyList<string> Expect(string message, IReadOnlyList<string> statements, string expected, int? count = null)
    {
        var steps = LockTrace.Of(statements);
        m_traces.Add((message, steps));
        var actual = string.Join("\n", steps);
        var wanted = string.Join("\n", expected.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (actual != wanted)
        {
            m_differences.Add($"{message} takes{Environment.NewLine}{actual}{Environment.NewLine}expected{Environment.NewLine}{wanted}");
        }
        if (count is { } expectedCount && statements.Count != expectedCount)
        {
            m_differences.Add($"{message} sends these {statements.Count} statements, expected {expectedCount}{Environment.NewLine}"
                + string.Join(Environment.NewLine, statements.Select((sql, index) => $"  {index + 1}. {string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}")));
        }
        return statements;
    }

    /// <summary>
    /// The statements of a gate operation, each in the shape of LockTrace.Shape: the ladder as the test spells it out, then the
    /// decision. A gate that stopped deciding the page, or decided it before the page row lock, sends other statements. And
    /// the message does its gate first, so the gate's statements are the message's first statements, text for text.
    /// </summary>
    private void ExpectGate(string operation, string ladder, IReadOnlyList<string> message, IReadOnlyList<string> gate)
    {
        m_traces.Add(("PageGate." + operation, LockTrace.Of(gate)));
        var expected = (ladder + "\n" + Decision).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sent = gate.Select(LockTrace.Shape).ToArray();
        if (sent.Length != expected.Length || !sent.Zip(expected, Matches).All(same => same))
        {
            m_differences.Add($"PageGate.{operation} sends these {sent.Length} statements{Environment.NewLine}{string.Join(Environment.NewLine, sent)}{Environment.NewLine}"
                + $"expected these {expected.Length}{Environment.NewLine}{string.Join(Environment.NewLine, expected)}");
        }
        if (!message.Take(gate.Count).SequenceEqual(gate))
        {
            m_differences.Add($"PageGate.{operation} sends{Environment.NewLine}{string.Join(Environment.NewLine, gate)}{Environment.NewLine}"
                + $"the message begins with{Environment.NewLine}{string.Join(Environment.NewLine, message.Take(gate.Count))}");
        }
    }

    /// <summary>Text for text, or up to the place where the expected line ends in " ...".</summary>
    private static bool Matches(string sent, string expected)
    {
        return expected.EndsWith(" ...", StringComparison.Ordinal) ? sent.StartsWith(expected[..^3], StringComparison.Ordinal) : sent == expected;
    }
}
