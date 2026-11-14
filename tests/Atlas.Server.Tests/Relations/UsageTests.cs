using System.Text.Json;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Domain;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Features.TreeReferences.Contracts;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Xunit;
using static Atlas.Server.Tests.Relations.RelationsFixture;

namespace Atlas.Server.Tests.Relations;

/// <summary>„Použito na“ (plan 3.1 M5): one service tells kde, jak and proč of every use, drafts included, to each reader only what it may
/// read and the rest as a hidden count; its count, the space's inventory and the relations of MCP read the same uses.</summary>
public sealed class UsageTests
{
    [PostgreSqlFact]
    public async Task A_contact_is_used_where_how_and_why_drafts_included()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Jana");
        var provoz = await PageAsync(f, "Provoz", Doc(Heading("Kontakty"), Paragraph("Za VPN odpovídá", Mention(jana))));
        var navrh = await PageAsync(f, "Návrh", Doc(Paragraph("Zatím nic")));
        await f.Pages.SaveAsync(navrh.Id, new SavePageRequest("Návrh", Doc(Paragraph("Nově přebírá", Mention(jana))), "Návrh", navrh.Version), f.Writer, default);
        var plan = await f.BoardAsync("Plán");
        var card = Guid.NewGuid();
        await f.Boards.ApplyAsync(f.Writer, plan, ObjectCard(card, jana));
        var node = await f.As<PageTreeItem>(f.Writer, new AddTreeReference(f.SpaceId, new AddTreeReferenceRequest(jana, null, provoz.Id)));

        var usage = await UsageAsync(f, f.Writer, jana);
        Assert.Equal(["Návrh mention draft", "Plán placement", "Provoz mention", "Provoz reference"], Places(usage));
        Assert.Equal((4, 0, (string?)null), (usage.Total, usage.Hidden, usage.NextCursor));
        Assert.All(usage.Items, row => Assert.Equal((f.SpaceId, "Obsah"), (row.SpaceId, row.SpaceName)));
        Assert.Equal("Za VPN odpovídá Jana", usage.Items.Single(row => row.PlaceId == provoz.Id && row.Kind == RelationKinds.Mention).Excerpt);
        Assert.Equal("Nově přebírá Jana", usage.Items.Single(row => row.Draft).Excerpt);
        Assert.Equal(card.ToString(), usage.Items.Single(row => row.Kind == RelationKinds.Placement).PartId);
        Assert.Equal(node.Id.ToString(), usage.Items.Single(row => row.Kind == RelationKinds.Reference).PartId);

        // A reader of the published pages: the draft's use is hidden, told only as a number.
        Assert.Equal(["Plán placement", "Provoz mention", "Provoz reference"], Places(await UsageAsync(f, f.Reader, jana)));
        Assert.Equal(new UsageCount(jana, 4, 3, 1), await f.As<UsageCount>(f.Reader, new CountObjectUsage(jana)));
        Assert.Equal(new UsageCount(jana, 3, 3, 0), await f.As<UsageCount>(f.Writer, new CountObjectUsage(jana, Drafts: false)));

        // atlas_get_relations reads the same: drafts to an editor of the page, why each use is there, the rest hidden.
        var asReader = await f.As<ObjectRelations>(f.Reader, new GetObjectRelations(jana, Drafts: true, Reasons: true));
        Assert.Equal(1, asReader.Hidden);
        Assert.DoesNotContain(asReader.Edges, edge => edge.Draft || edge.SourceId == navrh.Id);
        Assert.Equal("Za VPN odpovídá Jana", asReader.Edges.Single(edge => edge.SourceId == provoz.Id && edge.Kind == RelationKinds.Mention).Excerpt);
        Assert.Contains((await f.As<ObjectRelations>(f.Writer, new GetObjectRelations(jana, Drafts: true))).Edges, edge => edge.Draft && edge.SourceId == navrh.Id);
        Assert.DoesNotContain((await f.RelationsAsync(f.Writer, jana)).Edges, edge => edge.Draft);

        // A place in the trash is no use; an object the reader may not read has no answer.
        await f.TrashPageAsync(provoz, f.Writer);
        Assert.Equal(["Návrh mention draft", "Plán placement"], Places(await UsageAsync(f, f.Writer, jana)));
        await BoardsFixture.Refused(404, () => UsageAsync(f, f.Outsider, jana));
        await BoardsFixture.Refused(404, () => f.As<UsageCount>(f.Outsider, new CountObjectUsage(jana)));
    }

    [PostgreSqlFact]
    public async Task Every_reader_sees_only_the_places_it_may_read_and_the_rest_as_hidden()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Jana");
        var hiddenOnly = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Skrytá");
        await f.ContactAsync(f.Writer, ObjectScopes.Space, "Volná");
        var pub = await PageAsync(f, "Pub", Doc(Paragraph("Kontakt", Mention(jana))));
        // A page of its own access without grants: only the space's Admin and the WikiAdmin read it.
        var secret = await PageAsync(f, "Secret", Doc(Paragraph("Tajné", Mention(jana), Mention(hiddenOnly, "Skrytá"), PageRef(pub.Id))));
        await f.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", secret.Id);
        var navrh = await PageAsync(f, "Návrh", Doc(Paragraph("Zatím nic")));
        await f.Pages.SaveAsync(navrh.Id, new SavePageRequest("Návrh", Doc(Paragraph("Koncept", Mention(jana))), "Návrh", navrh.Version), f.Writer, default);
        var plan = await f.BoardAsync("Plán");
        await f.Boards.ApplyAsync(f.Writer, plan, ObjectCard(Guid.NewGuid(), jana));
        var own = await f.ContactAsync(f.Writer, ObjectScopes.Private, "Own");
        await f.Boards.ApplyAsync(f.Writer, plan, ObjectCard(Guid.NewGuid(), own));

        // (places of Jana, hidden ones, uses of Pub hidden)
        var expected = new[]
        {
            (f.Admin, "Návrh|Plán|Pub|Secret", 0, 0),
            (f.Manager, "Návrh|Plán|Pub|Secret", 0, 0),
            (f.Writer, "Návrh|Plán|Pub", 1, 1),
            (f.Reader, "Plán|Pub", 2, 1)
        };
        foreach (var (actor, places, hidden, pubHidden) in expected)
        {
            var usage = await UsageAsync(f, actor, jana);
            Assert.Equal((actor.Username, places, hidden, 4), (actor.Username, string.Join("|", usage.Items.Select(row => row.PlaceTitle)), usage.Hidden, usage.Total));
            Assert.Equal(new UsageCount(jana, 4, 4 - hidden, hidden), await f.As<UsageCount>(actor, new CountObjectUsage(jana)));
            var ofPub = await UsageAsync(f, actor, pub.Id);
            Assert.Equal((actor.Username, pubHidden), (actor.Username, ofPub.Hidden));
            Assert.Equal(1 - pubHidden, ofPub.Items.Count);
        }
        await BoardsFixture.Refused(404, () => UsageAsync(f, f.Outsider, jana));
        await BoardsFixture.Refused(404, () => UsageAsync(f, f.Reader, secret.Id));
        // A private object is its owner's, no administrator's.
        Assert.Equal(["Plán placement"], Places(await UsageAsync(f, f.Writer, own)));
        await BoardsFixture.Refused(404, () => UsageAsync(f, f.Admin, own));

        // The inventory and Obsah agree with the cleanup: a contact used only where the reader may not look is used, not unused.
        var inventory = await f.As<SpaceUsage>(f.Reader, new ListSpaceUsage(f.SpaceId));
        Assert.Equal((2, 2, false), inventory.Items.Where(row => row.Id == jana).Select(row => (row.Uses, row.Hidden, row.Unused)).Single());
        Assert.Equal((0, 1, false), inventory.Items.Where(row => row.Id == hiddenOnly).Select(row => (row.Uses, row.Hidden, row.Unused)).Single());
        Assert.DoesNotContain(inventory.Items, row => row.Id == secret.Id || row.Id == own);
        Assert.Equal(1, inventory.Unused);
        Assert.Equal(["Volná"], (await f.As<SpaceUsage>(f.Reader, new ListSpaceUsage(f.SpaceId, Unused: true))).Items.Select(row => row.Title));
        Assert.Equal(["Volná"], (await f.ContentsAsync(f.Reader, relation: "unused")).Items.Select(row => row.Title));
        await BoardsFixture.Refused(404, () => f.As<SpaceUsage>(f.Outsider, new ListSpaceUsage(f.SpaceId)));
    }

    [PostgreSqlFact]
    public async Task Uses_come_fifty_at_a_time()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Jana");
        // 55 published pages that mention her, written straight into the tables and derived by UsageAsync. On the shared host the ids are
        // this space's and the rows the page handlers would add are derived for its rows only (DerivedRows.ForSpaces).
        await ContentsBudgetTests.SqlAsync(f, $$"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT md5('{{f.SpaceId}}page' || n)::uuid, '{{f.SpaceId}}', '{{f.Writer.Id}}', 1 FROM generate_series(1, 55) n;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT md5('{{f.SpaceId}}revision' || n)::uuid, md5('{{f.SpaceId}}page' || n)::uuid, 1, 'Stránka ' || lpad(n::text, 2, '0'), '{{Doc(Paragraph("Text", Mention(jana)))}}', 'Text',
                '{{f.Writer.Id}}', now(), 'web'
              FROM generate_series(1, 55) n;
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."SpaceId"='{{f.SpaceId}}';
            {{Atlas.Server.Tests.Platform.DerivedRows.ForSpaces(f.SpaceId)}}
            """);

        var first = await UsageAsync(f, f.Reader, jana);
        Assert.Equal((50, 55, "50"), (first.Items.Count, first.Total, first.NextCursor));
        var rest = await UsageAsync(f, f.Reader, jana, cursor: first.NextCursor);
        Assert.Equal(["Stránka 51", "Stránka 52", "Stránka 53", "Stránka 54", "Stránka 55"], rest.Items.Select(row => row.PlaceTitle));
        Assert.Null(rest.NextCursor);
        await BoardsFixture.Refused(400, () => UsageAsync(f, f.Reader, jana, cursor: "-1"), "invalid_cursor");
    }

    [Fact]
    public void An_excerpt_leaves_out_the_stored_title_of_a_page_reference()
    {
        var contact = Guid.NewGuid();
        var json = Doc(Paragraph("Viz", PageRef(Guid.NewGuid()).Replace("Odkaz", "Tajný projekt"), Mention(contact)));
        Assert.Equal("Viz Jana", UsageExcerpt.Find(json, contact).Excerpt);
    }

    [Fact]
    public void An_excerpt_keeps_an_unresolved_name_the_reader_sees_in_the_sentence()
    {
        var contact = Guid.NewGuid();
        var unresolved = """{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Petr","unresolved":true}]}}}""";
        Assert.Equal("Volala jsem Petr kvůli Jana", UsageExcerpt.Find(Doc(Paragraph("Volala jsem", unresolved, """{"type":"text","text":"kvůli"}""", Mention(contact))), contact).Excerpt);
    }

    [Fact]
    public void A_contact_card_is_told_as_a_card_with_only_that_persons_name_and_role()
    {
        var jana = Guid.NewGuid();
        var card = """{"type":"contactCard","attrs":{"data":{"version":1,"contacts":[{"name":"Petr"},{"name":"Jana","role":"PM projektu","objectId":""" + $"\"{jana}\"" + """},{"name":"Eva"}]}}}""";
        var reason = UsageExcerpt.Find(Doc(card), jana);
        Assert.Equal(("Jana · PM projektu", true), (reason.Excerpt, reason.Card));
        Assert.Equal("Tým: Jana · PM projektu", UsageExcerpt.Find(Doc(Heading("Tým"), card), jana).Excerpt);
        Assert.False(UsageExcerpt.Find(Doc(Paragraph("Viz", Mention(jana))), jana).Card);
    }

    internal static string Mention(Guid contact, string name = "Jana") =>
        $$$$"""{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"{{{{name}}}}","objectId":"{{{{contact}}}}"}]}}}""";

    internal static string Paragraph(string text, params string[] inline) =>
        """{"type":"paragraph","content":[{"type":"text","text":""" + JsonSerializer.Serialize(text) + "}" + string.Concat(inline.Select(node => "," + node)) + "]}";

    private static string Heading(string text) => """{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":""" + JsonSerializer.Serialize(text) + "}]}";

    internal static string Doc(params string[] blocks) =>
        """{"format":"atlas-rich","version":1,"plugins":[{"id":"pages","version":1},{"id":"contacts","version":2}],"document":{"type":"doc","content":["""
        + string.Join(",", blocks) + "]}}";

    private static async Task<PageDto> PageAsync(RelationsFixture f, string title, string content)
    {
        var page = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, title, content, title), f.Writer, default);
        return await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
    }

    /// <summary>The mentions are derived after a save by the outbox's delivery, which this host has no queue for: derived first.</summary>
    private static async Task<ObjectUsageList> UsageAsync(RelationsFixture f, UserAccount actor, Guid id, string? cursor = null)
    {
        await f.DeriveAsync();
        return await f.As<ObjectUsageList>(actor, new GetObjectUsage(id, Cursor: cursor));
    }

    private static string[] Places(ObjectUsageList usage) =>
        usage.Items.Select(row => $"{row.PlaceTitle} {row.Kind}{(row.Draft ? " draft" : "")}").ToArray();
}
