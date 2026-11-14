using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Contacts.Contracts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Queries;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>Plan 3.1 M4b: the party as a contact, who changed a block when, dependencies across pages and the catalog's uses.</summary>
public sealed class ProjectorLinksTests
{
    [PostgreSqlFact]
    public async Task A_party_contact_is_found_by_its_identity_grouped_filtered_and_counted_as_a_use()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var first = await f.PageAsync("KBN", Json(RequirementHeading("portal.ssl", "SSL", "waiting_for_input")));
        var second = await f.PageAsync("Pojišťovna", Json(RequirementHeading("portal.dns", "DNS", "waiting_for_input", party: "Jan Novák")));
        // Contacts are of the whole wiki: the identity is the test's own, as no other test's contact may match it.
        var name = TestLogin.For("Jan Novák");
        var identity = new JsonObject { ["name"] = name, ["emails"] = new JsonArray(Email("jan.novak")) };

        var linked = await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(first, "portal.ssl", new RequirementChange(PartyContact: identity), await VersionAsync(f, first)));
        var contact = Guid.Parse(await PartyContactAsync(f, first, "portal.ssl"));
        // The same identity again is the same contact (the contacts' identity service), and the party reads its name.
        await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(second, "portal.dns", new RequirementChange(PartyContact: (JsonObject)identity.DeepClone()),
            await VersionAsync(f, second)));
        Assert.Equal(contact.ToString("D"), await PartyContactAsync(f, second, "portal.dns"));
        Assert.Empty(linked.Warnings);
        var blocks = await f.As<PageRequirements>(f.Admin, new GetPageRequirements(first));
        Assert.Equal((name, contact.ToString("D")), (blocks.Blocks[0].Party, blocks.Blocks[0].PartyContactId));

        await f.Pages.PublishAsync(first, new PublishPageRequest(await VersionAsync(f, first)), f.Admin, default);
        await f.Pages.PublishAsync(second, new PublishPageRequest(await VersionAsync(f, second)), f.Admin, default);
        var waiting = await f.As<WaitingList>(f.Reader, new GetProjectorWaiting(f.SpaceId));
        var party = Assert.Single(waiting.Parties);
        Assert.Equal((contact, 2), (party.ContactId!.Value, party.Items.Count));
        var query = await f.As<RequirementQueryPage>(f.Reader, new QueryRequirements(f.SpaceId, PartyContactId: contact));
        Assert.Equal(2, query.Total);

        var uses = await f.As<ObjectUsageList>(f.Reader, new GetObjectUsage(contact));
        Assert.Equal(new[] { first, second }.Order(), uses.Items.Where(x => x.Kind == "party").Select(x => x.PlaceId).Order());
        // A heading party is a use of its page (the use names no block any more).
        Assert.Null(uses.Items.Single(x => x.PlaceId == first).PartId);

        // Text alone is no contact: it unlinks it.
        await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(first, "portal.ssl", new RequirementChange(Party: "IT KBN"), await VersionAsync(f, first)));
        Assert.Equal("", await PartyContactAsync(f, first, "portal.ssl"));
        var version = await VersionAsync(f, first);
        var unknown = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementWriteResult>(f.Admin,
            new UpdateRequirement(first, "portal.ssl", new RequirementChange(PartyContactId: Guid.NewGuid().ToString()), version)));
        Assert.Equal(400, unknown.Status);
    }

    /// <summary>Owner 2. 11. 2026: „čekají na DODAVATEL“ also finds a party that is a contact of DODAVATEL, case and diacritics aside.</summary>
    [PostgreSqlFact]
    public async Task A_party_filter_also_finds_a_contact_by_its_organization()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(RequirementHeading("portal.ssl", "SSL", "waiting_for_input"),
            RequirementHeading("portal.dns", "DNS", "waiting_for_input", party: "IT KBN")));
        var identity = new JsonObject { ["name"] = TestLogin.For("Jan Novák"), ["organization"] = "DODAVATEL Čechy", ["emails"] = new JsonArray(Email("jan.novak")) };
        await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(page, "portal.ssl", new RequirementChange(PartyContact: identity), await VersionAsync(f, page)));
        await f.Pages.PublishAsync(page, new PublishPageRequest(await VersionAsync(f, page)), f.Admin, default);

        async Task<string[]> Ids(string party)
            => (await f.As<RequirementQueryPage>(f.Reader, new QueryRequirements(f.SpaceId, Party: party))).Rows.Select(x => x.Requirement.BlockId).ToArray();
        Assert.Equal(["portal.ssl"], await Ids("dodavatel"));
        Assert.Equal(["portal.ssl"], await Ids("Cechy"));
        Assert.Equal(["portal.ssl"], await Ids("novak"));
        Assert.Equal(["portal.dns"], await Ids("it kbn"));
        Assert.Empty(await Ids("ZENTIVA"));
    }

    /// <summary>A party merged into another contact is a use of that other one: its trash or the orphan cleanup must not miss it.</summary>
    [PostgreSqlFact]
    public async Task A_party_merged_into_another_contact_is_a_use_of_the_target()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(RequirementHeading("portal.ssl", "SSL", "waiting_for_input")));
        var identity = new JsonObject { ["name"] = TestLogin.For("J. Novák"), ["emails"] = new JsonArray(Email("j.novak")) };
        await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(page, "portal.ssl", new RequirementChange(PartyContact: identity), await VersionAsync(f, page)));
        await f.Pages.PublishAsync(page, new PublishPageRequest(await VersionAsync(f, page)), f.Admin, default);
        var merged = Guid.Parse(await PartyContactAsync(f, page, "portal.ssl"));
        var target = (await f.As<ObjectDto>(f.Admin, new CreateObject(ContactObjectType.Id, ObjectScopes.Wiki, null, new JsonObject { ["name"] = TestLogin.For("Jan Novák") }))).Id;

        await f.As<MergedContacts>(f.Admin, new MergeContacts(merged, target));

        // The block still names the merged contact until its page is saved again.
        Assert.Equal(merged.ToString("D"), await PartyContactAsync(f, page, "portal.ssl"));
        var uses = await f.As<ObjectUsageList>(f.Reader, new GetObjectUsage(target));
        Assert.Equal([(page, "party")], uses.Items.Select(x => (x.PlaceId, x.Kind)));
        Assert.Equal(1, (await f.As<UsageCount>(f.Admin, new CountObjectUsage(target))).Total);
    }

    /// <summary>Shape §6: a requirement heading's party is its attributes. The update (MCP's too) writes them, not a facts line; the contact
    /// is a use (the party) and a mention of the page, and a Markdown save (MCP, API) after a merge writes the target's id and name.</summary>
    [PostgreSqlFact]
    public async Task A_heading_party_is_its_attributes_a_use_and_a_mention_and_follows_a_merge_on_save()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Heading("Portál", "portal", 2), RequirementHeading("portal.ssl", "SSL", "waiting_for_input")));
        var (name, directoryName) = (TestLogin.For("J. Novák"), TestLogin.For("Jan Novák"));
        var identity = new JsonObject { ["name"] = name, ["emails"] = new JsonArray(Email("j.novak")) };

        await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(page, "portal.ssl", new RequirementChange(PartyContact: identity, Date: "2026-11-15"),
            await VersionAsync(f, page)));

        var merged = Guid.Parse(await PartyContactAsync(f, page, "portal.ssl"));
        var stored = (await f.Pages.GetAsync(page, f.Admin, default)).ContentJson;
        var content = JsonNode.Parse(stored)!["document"]!["content"]!.AsArray();
        Assert.Equal((name, merged.ToString("D")), ((string?)content[1]!["attrs"]!["party"], (string?)content[1]!["attrs"]!["partyContactId"]));
        Assert.Equal("do 15. 11. 2026", (string?)content[2]!["content"]![0]!["text"]);
        Assert.DoesNotContain("contactInline", stored, StringComparison.Ordinal);
        await f.Pages.PublishAsync(page, new PublishPageRequest(await VersionAsync(f, page)), f.Admin, default);
        // What the outbox derives after the commit, for this page only (the host's other tests write theirs).
        await f.Host.SeedAsync(async session =>
        {
            await new Atlas.Server.Features.PageLinks.PageLinkProjection(session).RefreshAsync(page, default);
            await new PageObjectMentions(session).RefreshAsync(page, default);
        });
        await f.FreshIndexAsync();
        Assert.Equal(1L, await f.ScalarAsync("""SELECT count(DISTINCT "SourcePageId") FROM "PageObjectMention" WHERE "ObjectId"=:id""", merged));
        Assert.Contains((page, "party"), (await f.As<ObjectUsageList>(f.Reader, new GetObjectUsage(merged))).Items.Select(x => (x.PlaceId, x.Kind)));

        var target = (await f.As<ObjectDto>(f.Admin, new CreateObject(ContactObjectType.Id, ObjectScopes.Wiki, null, new JsonObject { ["name"] = directoryName }))).Id;
        await f.As<MergedContacts>(f.Admin, new MergeContacts(merged, target));
        // The web's save names the merged contact's target, the party's name as it was (contacts spec §5 point 4).
        var current = await f.Pages.GetAsync(page, f.Admin, default);
        await f.Pages.SaveAsync(page, new SavePageRequest(current.Title, current.ContentJson, null, current.Version), f.Admin, default);
        var web = JsonNode.Parse((await f.Pages.GetAsync(page, f.Admin, default)).ContentJson)!["document"]!["content"]![1]!["attrs"]!;
        Assert.Equal((name, target.ToString("D")), ((string?)web["party"], (string?)web["partyContactId"]));
        // A Markdown save (MCP, API) also takes the directory's name.
        current = await f.Pages.GetAsync(page, f.Admin, default);
        await f.As<PageDto>(f.Admin, new SavePage(page, new SavePageRequest(current.Title, current.ContentJson, null, current.Version), FromMarkdown: true));

        var saved = JsonNode.Parse((await f.Pages.GetAsync(page, f.Admin, default)).ContentJson)!["document"]!["content"]![1]!["attrs"]!;
        Assert.Equal((directoryName, target.ToString("D")), ((string?)saved["party"], (string?)saved["partyContactId"]));
        Assert.Equal(target.ToString("D"), await PartyContactAsync(f, page, "portal.ssl"));
    }

    [PostgreSqlFact]
    public async Task Each_block_keeps_who_changed_it_when_in_the_draft_and_in_the_publication()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(RequirementHeading("portal.ssl", "SSL"), RequirementHeading("portal.dns", "DNS")));
        await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(page, "portal.dns", new RequirementChange(State: "in_progress"), await VersionAsync(f, page)));

        var editor = await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page));
        Assert.Equal((f.Writer.Id, f.Admin.Id), (editor.Blocks[0].Changed!.ChangedById, editor.Blocks[1].Changed!.ChangedById));
        // The reader sees the publication: both blocks as the Writer published them.
        var reader = await f.As<PageRequirements>(f.Reader, new GetPageRequirements(page));
        Assert.All(reader.Blocks, block => Assert.Equal(("publish", f.Writer.Id), (block.Changed!.Kind, block.Changed.ChangedById)));

        // Plan 3.6: the history is the text: the dated lines of the notes, whole, and the state changes of the versions with their author.
        var longLine = "Urgováno: " + string.Concat(Enumerable.Repeat("IT KBN čeká na podpis smlouvy. ", 20));
        await f.As<RequirementWriteResult>(f.Admin, new UpdateRequirement(page, "portal.dns",
            new RequirementChange(NotesMarkdown: $"- 1. 11. 2026 23:40 — {longLine}\n- 15. 10. 2026 — telefonát s IT"), await VersionAsync(f, page)));
        var history = await f.As<RequirementHistory>(f.Writer, new GetRequirementHistory(page, "portal.dns"));
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague")).ToString("yyyy-MM-dd");
        Assert.Equal([("state", today, "planned>in_progress"), ("note", "2026-11-01", longLine.Trim()), ("note", "2026-10-15", "telefonát s IT")],
            history.Entries.Select(x => (x.Kind, x.Date, x.Kind == "state" ? $"{x.From}>{x.To}" : x.Text!)));
        Assert.Equal((f.Admin.Id, "23:40"), (history.Entries[0].ChangedById!.Value, history.Entries[1].Time!));
        // An untouched block has no state change; a reader reads the publication, which has neither yet.
        Assert.Empty((await f.As<RequirementHistory>(f.Writer, new GetRequirementHistory(page, "portal.ssl"))).Entries);
        Assert.Empty((await f.As<RequirementHistory>(f.Reader, new GetRequirementHistory(page, "portal.dns"))).Entries);
        await f.Pages.PublishAsync(page, new PublishPageRequest(await VersionAsync(f, page)), f.Writer, default);
        Assert.Equal(3, (await f.As<RequirementHistory>(f.Reader, new GetRequirementHistory(page, "portal.dns"))).Entries.Count);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementHistory>(f.Outsider, new GetRequirementHistory(page, "portal.dns")))).Status);
    }

    [PostgreSqlFact]
    public async Task Dependencies_across_pages_wait_as_the_reader_reads_them_and_refuse_a_cycle()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("Infrastruktura", Json(Block(S1, "vpn", "VPN")));
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL", requires: $"vpn@{source:D}")));
        var reference = $"vpn@{source:D}";

        Assert.Equal([reference], (await f.As<PageRequirements>(f.Reader, new GetPageRequirements(page))).Blocks[0].WaitingOn);
        var prerequisite = Assert.Single(await f.As<IReadOnlyList<RequirementPrerequisite>>(f.Reader, new GetRequirementPrerequisites([reference])));
        Assert.Equal((true, "VPN", "planned", "Infrastruktura"), (prerequisite.Readable, prerequisite.Title, prerequisite.State, prerequisite.PageTitle));
        var dependents = await f.As<RequirementDependents>(f.Reader, new GetRequirementDependents(source, S1));
        Assert.Equal([(page, B1)], dependents.Rows.Select(x => (x.PageId, x.Requirement.BlockId)));

        // The way back from the source to the page closes a cycle: refused with its path, and the check says the same.
        var version = await VersionAsync(f, source);
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementWriteResult>(f.Admin,
            new UpdateRequirement(source, S1, new RequirementChange(Requires: [$"portal.ssl@{page:D}"]), version)));
        Assert.Equal(("cyclic_dependency", 400), (refused.Code, refused.Status));
        Assert.Equal(["vpn", $"portal.ssl@{page:D}", "vpn"], (IEnumerable<string>)refused.Extensions!["path"]!);
        var cycle = await f.As<RequirementCycle>(f.Admin, new CheckRequirementCycle(source, "vpn", [$"portal.ssl@{page:D}"]));
        Assert.Equal(["vpn", $"portal.ssl@{page:D}", "vpn"], cycle.Path);
        Assert.Null((await f.As<RequirementCycle>(f.Admin, new CheckRequirementCycle(source, "vpn", ["jine"]))).Path);

        // Done in the draft: an editor of both waits on nothing, the reader of the publication still does.
        await f.SaveWorkingAsync(source, Json(Block(S1, "vpn", "VPN", state: "done")));
        Assert.Empty((await f.As<PageRequirements>(f.Admin, new GetPageRequirements(page))).Blocks[0].WaitingOn);
        Assert.Equal([reference], (await f.As<PageRequirements>(f.Reader, new GetPageRequirements(page))).Blocks[0].WaitingOn);
        // A prerequisite the reader may not read is unknown: it still waits, and nothing of it is told.
        await f.OwnAccessAsync(source);
        Assert.Equal([reference], (await f.As<PageRequirements>(f.Reader, new GetPageRequirements(page))).Blocks[0].WaitingOn);
        prerequisite = Assert.Single(await f.As<IReadOnlyList<RequirementPrerequisite>>(f.Reader, new GetRequirementPrerequisites([reference])));
        Assert.Equal((false, null, null), (prerequisite.Readable, prerequisite.Title, prerequisite.State));
        Assert.Empty((await f.As<RequirementDependents>(f.Reader, new GetRequirementDependents(page, B1))).Rows);
    }

    /// <summary>Review P2-5: a reader of a live page (3.6-I Živě) reads its working version in every requirement read, as the page itself
    /// shows it: the list, the dependents of a new block and the cycle check agree; on a Ručně page all of them read only the publication.</summary>
    [PostgreSqlFact]
    public async Task A_reader_of_a_live_page_reads_its_saved_requirements_in_the_list_the_dependents_and_the_cycle_check()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("Infrastruktura", Json(Block(S1, "vpn", "VPN")));
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL", requires: $"vpn@{source:D}")));
        // Saved, not published: a new block dns, and vpn requires it, so dns requiring portal.ssl would close a cycle through KBN.
        await f.SaveWorkingAsync(source, Json(Block(S1, "vpn", "VPN", requires: "dns"), Block(B2, "dns", "DNS")));
        var check = new CheckRequirementCycle(source, "dns", [$"portal.ssl@{page:D}"]);

        await f.As<PagePublicationState>(f.Writer, new SetPagePublication(source, new("Live")));
        Assert.Equal([S1, B2], (await f.As<PageRequirements>(f.Reader, new GetPageRequirements(source))).Blocks.Select(x => x.BlockId));
        Assert.Equal("dns", (await f.As<RequirementDependents>(f.Reader, new GetRequirementDependents(source, B2))).Key);
        Assert.Equal(["dns", $"portal.ssl@{page:D}", "vpn", "dns"], (await f.As<RequirementCycle>(f.Reader, check)).Path);

        await f.As<PagePublicationState>(f.Writer, new SetPagePublication(source, new("Manual")));
        Assert.Equal([S1], (await f.As<PageRequirements>(f.Reader, new GetPageRequirements(source))).Blocks.Select(x => x.BlockId));
        Assert.Equal("requirement_not_found",
            (await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementDependents>(f.Reader, new GetRequirementDependents(source, B2)))).Code);
        Assert.Null((await f.As<RequirementCycle>(f.Reader, check)).Path);
    }

    /// <summary>Review P2-6: the state changes of a block in one pass over the page's revisions: each version's change by the last of its
    /// checkpoints that touched the block (the version's newest otherwise), newest first, at most the limit.</summary>
    [PostgreSqlFact]
    public async Task A_state_change_names_the_last_checkpoint_that_touched_the_block_newest_first_up_to_the_limit()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block(B1, "vpn", "VPN")));
        for (var n = 0; n < 4; n++)
        {
            await f.SaveWorkingAsync(page, Json(Block(B1, "vpn", "VPN")));
        }
        // Revisions 2 and 3 are one version (2 touched the block, 3 sums the change), 4 and 5 versions of their own.
        string State(string from, string to) => $$"""[{"blockId":"{{B1}}","from":"{{from}}","to":"{{to}}"}]""";
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""
                WITH r AS (SELECT "Id",row_number() OVER (ORDER BY "Version") AS n FROM "PageRevision" WHERE "PageId"=:page),
                g AS (SELECT "Id" FROM r WHERE n=2)
                UPDATE "PageRevision" p SET
                  "GroupId"=CASE WHEN r.n IN (2,3) THEN (SELECT "Id" FROM g) ELSE p."Id" END,
                  "AuthorId"=CASE WHEN r.n IN (3,4) THEN :admin ELSE :writer END,
                  "ChangeSummary"=CAST(CASE r.n WHEN 2 THEN :touched WHEN 3 THEN :first WHEN 4 THEN :second WHEN 5 THEN :third END AS jsonb)
                FROM r WHERE r."Id"=p."Id" AND r.n>=2
                """).SetParameter("page", page).SetParameter("admin", f.Admin.Id).SetParameter("writer", f.Writer.Id)
            .SetParameter("touched", $$"""{"touched":["{{B1}}"]}""")
            .SetParameter("first", $$"""{"touched":[],"states":{{State("planned", "in_progress")}}}""")
            .SetParameter("second", $$"""{"touched":[],"states":{{State("in_progress", "waiting_for_input")}}}""")
            .SetParameter("third", $$"""{"touched":["{{B1}}"],"states":{{State("waiting_for_input", "done")}}}""")
            .ExecuteUpdateAsync());

        var history = await f.As<RequirementHistory>(f.Writer, new GetRequirementHistory(page, B1));
        Assert.Equal([("done", f.Writer.Id), ("waiting_for_input", f.Admin.Id), ("in_progress", f.Writer.Id)],
            history.Entries.Select(x => (x.To!, x.ChangedById!.Value)));
        var limited = await f.As<RequirementHistory>(f.Writer, new GetRequirementHistory(page, B1, 2));
        Assert.Equal(["done", "waiting_for_input"], limited.Entries.Select(x => x.To!));
    }

    /// <summary>The dependency picker asks the server as the user types: every word in the block's title, key or page title, case and
    /// diacritics aside, at most 50 blocks, only those the reader may read.</summary>
    [PostgreSqlFact]
    public async Task The_requirements_of_the_space_are_searched_word_by_word_fifty_at_most()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("Infrastruktura", Json(Block(S1, "vpn", "Přístup do VPN"), Block(B2, "portal.dns", "DNS")));
        var many = await f.PageAsync("Mnoho", Json(Enumerable.Range(1, 60).Select(n => Block($"00000000-0000-4000-8000-{n:D12}", $"k{n}", $"Blok {n}")).ToArray()));
        var hidden = await f.PageAsync("Tajné", Json(Block(B1, "vpn", "VPN")));
        await f.OwnAccessAsync(hidden);

        var found = await f.As<IReadOnlyList<RequirementRow>>(f.Reader, new SearchSpaceRequirements(f.SpaceId, "pristup INFRA"));
        Assert.Equal([(source, "vpn")], found.Select(x => (x.PageId, x.Requirement.Key)));
        Assert.Equal([source], (await f.As<IReadOnlyList<RequirementRow>>(f.Reader, new SearchSpaceRequirements(f.SpaceId, "vpn"))).Select(x => x.PageId));
        var blocks = await f.As<IReadOnlyList<RequirementRow>>(f.Reader, new SearchSpaceRequirements(f.SpaceId, "blok"));
        Assert.Equal((SearchSpaceRequirements.Limit, many), (blocks.Count, blocks.Select(x => x.PageId).Distinct().Single()));
        // The picker's own page goes before the limit: its sixty blocks no longer crowd out another page's.
        var other = await f.PageAsync("Další", Json(Block(B2, "blok.dalsi", "Blok jinde")));
        var elsewhere = await f.As<IReadOnlyList<RequirementRow>>(f.Reader, new SearchSpaceRequirements(f.SpaceId, "blok", many));
        Assert.Equal([(other, "blok.dalsi")], elsewhere.Select(x => (x.PageId, x.Requirement.Key)));
    }

    /// <summary>G5: on MCP a page of a space switched out of AI is unknown to the dependencies across pages, as to any other read.</summary>
    [PostgreSqlFact]
    public async Task Dependencies_across_pages_tell_MCP_nothing_of_a_space_out_of_AI()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var source = await f.PageAsync("Infrastruktura", Json(Block(S1, "vpn", "VPN")));
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL", requires: $"vpn@{source:D}")));
        await f.Host.ExecuteAsync($"""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") VALUES (gen_random_uuid(),:id,false,'{f.Admin.Id}',now())
            """, f.SpaceId);
        var reference = $"vpn@{source:D}";

        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, Guid.NewGuid());
        try
        {
            var prerequisite = Assert.Single(await f.As<IReadOnlyList<RequirementPrerequisite>>(f.Reader, new GetRequirementPrerequisites([reference])));
            Assert.Equal((false, null), (prerequisite.Readable, prerequisite.Title));
            Assert.Null((await f.As<RequirementCycle>(f.Admin, new CheckRequirementCycle(source, "vpn", [$"portal.ssl@{page:D}"]))).Path);
        }
        finally
        {
            f.Host.Channel.Current = RequestChannel.Web;
        }
        // On the web the same reads see it.
        Assert.True(Assert.Single(await f.As<IReadOnlyList<RequirementPrerequisite>>(f.Reader, new GetRequirementPrerequisites([reference]))).Readable);
        Assert.NotNull((await f.As<RequirementCycle>(f.Admin, new CheckRequirementCycle(source, "vpn", [$"portal.ssl@{page:D}"]))).Path);
    }

    [PostgreSqlFact]
    public async Task A_catalog_requirement_is_used_by_the_deployments_holding_its_key()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var catalog = await f.PageAsync("Katalog", Json(Block(C1, "portal.ssl", "SSL"), Block(C2, "portal.dns", "DNS")));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        var deployment = await f.PageAsync("KBN", Json(Block(D1, "portal.ssl", "SSL")));
        await f.SaveWorkingAsync(deployment, Json(Block(D1, "portal.ssl", "SSL"), Block(D2, "portal.dns", "DNS")));

        // Uses read the index as the outbox keeps it (eventually): this host has none, so the test makes it current.
        await f.FreshIndexAsync();
        var ssl = await f.As<ObjectUsageList>(f.Writer, new GetObjectUsage(catalog, Part: "portal.ssl"));
        Assert.Equal([(deployment, "catalog", "portal.ssl", false)], ssl.Items.Select(x => (x.PlaceId, x.Kind, x.Anchor, x.Draft)));
        // Only the draft holds portal.dns: the Writer sees it as a draft's use, the Reader not at all.
        Assert.True(Assert.Single((await f.As<ObjectUsageList>(f.Writer, new GetObjectUsage(catalog, Part: "portal.dns"))).Items).Draft);
        var reader = await f.As<UsageCount>(f.Reader, new CountObjectUsage(catalog, Part: "portal.dns"));
        Assert.Equal((1, 0, 1), (reader.Total, reader.Visible, reader.Hidden));
    }

    private const string B1 = "00000000-0000-4000-8000-0000000000b1", B2 = "00000000-0000-4000-8000-0000000000b2", S1 = "00000000-0000-4000-8000-0000000000a1",
        D1 = "00000000-0000-4000-8000-0000000000d1", D2 = "00000000-0000-4000-8000-0000000000d2", C1 = "00000000-0000-4000-8000-0000000000c1",
        C2 = "00000000-0000-4000-8000-0000000000c2";

    private static string Email(string name) => $"{name}.{Guid.NewGuid():N}@example.cz";

    private static async Task<int> VersionAsync(ProjectorFixture f, Guid page) => (await f.Pages.GetAsync(page, f.Admin, default)).Version;

    private static async Task<string> PartyContactAsync(ProjectorFixture f, Guid page, string blockId)
    {
        using var session = f.Host.OpenSession();
        await Atlas.Server.Features.Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, [page], default);
        return await session.CreateSQLQuery("""SELECT "PartyContactId" FROM "ProjectorRequirementIndex" WHERE "PageId"=:page AND "BlockId"=:block AND "IsWorking" """)
            .SetParameter("page", page).SetParameter("block", blockId).UniqueResultAsync<string>();
    }
}
