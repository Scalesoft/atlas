using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Contacts;

/// <summary>The contact as one entity over MCP (contacts spec 2026-10-30 §3, §5; plan §4): a page write links its contacts to the
/// directory and says so, the upsert finds before it creates, the merge waits for a person, boards take contacts by reference.</summary>
public sealed class ContactMcpTests
{
    // The directory is the whole wiki's and refuses a second contact with an address: each test's people have addresses and a phone of their own.
    private readonly string m_jana = $"jana-{Guid.NewGuid():N}@example.com";
    private readonly string m_petr = $"petr-{Guid.NewGuid():N}@x.cz";
    private readonly string m_digits = Random.Shared.Next(1_000_000).ToString("D6");

    private string Phone => $"777 {m_digits[..3]} {m_digits[3..]}";
    private string PhoneIntl => "+420 " + Phone;
    private string PhoneE164 => "+420777" + m_digits;

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_page_write_with_a_known_address_references_the_existing_contact_and_creates_only_new_people()
    {
        await using var f = await SiteAsync();
        var (space, agent) = await AgentAsync(f);
        var jana = await ContactAsync(f, $$"""{"name":"Jana Nováková","emails":["{{m_jana}}"],"phones":["{{PhoneIntl}}"],"organization":"Modelsoft"}""");
        var page = await PageAsync(f.Admin, space, "Tým", Rich(Heading(2, "tym", "Tým"), Paragraph("Lidé.")), "Lidé.", publish: false);

        var markdown = $"## Tým\n\n```a-contact\n- name: Jana\n  role: PM projektu\n  emails: [{m_jana.ToUpperInvariant()}]\n  phones: [\"602 000 000\"]\n```\n\n" +
            $"Volejte [Petr](a-contact:?name=Petr&email={m_petr}) nebo [Petr](a-contact:?name=Petr&email={m_petr}).\n";
        var written = await CallAsync(agent, "atlas_replace_page", new { pageId = page.Id, expectedVersion = page.Version, markdown });
        Assert.True(written.IsError != true, Text(written));
        var contacts = Structured(written)["contacts"]!;
        Assert.Equal([jana], contacts["used"]!.AsArray().Select(x => x!.GetValue<string>()));
        var petr = Assert.Single(contacts["created"]!.AsArray())!.GetValue<string>();
        var warnings = contacts["warnings"]!.AsArray();
        Assert.Equal(["contact_exists", "contact_conflict", "contact_conflict"], warnings.Select(x => x!["type"]!.GetValue<string>()));
        Assert.Equal(["phones", "name"], warnings.Skip(1).Select(x => x!["field"]!.GetValue<string>()));
        Assert.Contains("Upozornění: Kontakt Jana Nováková už je v adresáři", Text(written), StringComparison.Ordinal);

        // The page holds references, the snapshot refreshed from the directory (whose phone holds) with the page's own role.
        var stored = JsonNode.Parse((string)(await f.Site.ScalarAsync("""SELECT "ContentJson" FROM "PageRevision" WHERE "PageId"=@page AND "ActiveTo" IS NULL""",
            ("page", Guid.Parse(page.Id))))!)!["document"]!["content"]!.AsArray();
        var card = stored[1]!["attrs"]!["data"]!["contacts"]![0]!;
        Assert.Equal((jana, "Jana Nováková", PhoneIntl, "PM projektu"),
            (card["objectId"]!.GetValue<string>(), card["name"]!.GetValue<string>(), card["phones"]![0]!.GetValue<string>(), card["role"]!.GetValue<string>()));
        var mentions = stored[2]!["content"]!.AsArray().Where(x => x!["type"]!.GetValue<string>() == "contactInline").ToArray();
        Assert.Equal([petr, petr], mentions.Select(x => x!["attrs"]!["data"]!["contacts"]![0]!["objectId"]!.GetValue<string>()));

        // The next write of the same person is no new contact.
        var again = Structured(await CallAsync(agent, "atlas_update_section", new
        {
            pageId = page.Id, expectedVersion = Structured(written)["version"]!.GetValue<int>(), headingId = "tym",
            markdown = $"## Tým\n\nJen [Petr](a-contact:?name=Petr&email={m_petr.ToUpperInvariant()}).\n",
        }))["contacts"]!;
        Assert.Equal(petr, Assert.Single(again["used"]!.AsArray())!.GetValue<string>());
        Assert.Empty(again["created"]!.AsArray());
    }

    [PostgreSqlFact]
    public async Task A_contact_the_directory_refuses_names_its_place_and_nothing_is_saved()
    {
        await using var f = await SiteAsync();
        var (space, agent) = await AgentAsync(f);
        var since = await f.Site.ScalarAsync("SELECT clock_timestamp()");
        var draft = await CallAsync(agent, "atlas_create_draft", new
        {
            spaceId = space, title = "Lidé", markdown = $"```a-contact\n- name: Jana\n- name: {new string('x', 450)}\n```\n",
        });
        Assert.Equal("invalid_contact", ErrorCode(draft));
        Assert.Contains("Kontakt č. 2", Text(draft), StringComparison.Ordinal);
        Assert.Equal(0L, await f.Site.ScalarAsync("""SELECT count(*) FROM "Page" p JOIN "PageRevision" r ON r."PageId"=p."Id" WHERE r."Title"='Lidé' AND p."SpaceId"=@space::uuid""", ("space", space)));
        // No contact of this test's administrator (the token's account) since this test began.
        Assert.Equal(0L, await f.Site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "CreatedAt" >= @since AND "CreatedBy"=@admin::uuid""",
            ("since", since!), ("admin", f.AdminId)));
    }

    [PostgreSqlFact]
    public async Task Upsert_finds_before_it_creates_and_the_merge_waits_for_a_persons_confirmation()
    {
        await using var f = await SiteAsync();
        var (_, agent) = await AgentAsync(f);
        var created = Ok(await CallAsync(agent, "atlas_upsert_contact", new { contact = new { name = "Jana", emails = new[] { m_jana } } }));
        Assert.True(created["created"]!.GetValue<bool>());
        var jana = created["contactId"]!.GetValue<string>();
        var found = Ok(await CallAsync(agent, "atlas_upsert_contact", new { contact = new { name = "J. Nováková", emails = new[] { m_jana.ToUpperInvariant() } } }));
        Assert.Equal((jana, false), (found["contactId"]!.GetValue<string>(), found["created"]!.GetValue<bool>()));
        Assert.Equal("contact_conflict", found["warnings"]![1]!["type"]!.GetValue<string>());
        // A phone alone is a weak match (K3): created, with the candidate.
        Ok(await CallAsync(agent, "atlas_upsert_contact", new { contactId = jana, contact = new { phones = new[] { Phone }, aliases = new[] { "JN" } } }));
        // AI parity: the aliases people find her by are read and set as the web's card does.
        Assert.Equal("JN", Ok(await CallAsync(agent, "atlas_get_contact", new { contactId = jana }))["contact"]!["aliases"]![0]!.GetValue<string>());
        var weak = Ok(await CallAsync(agent, "atlas_upsert_contact", new { contact = new { name = "Jana Domů", phones = new[] { PhoneE164 } } }));
        Assert.True(weak["created"]!.GetValue<bool>());
        Assert.Equal(("possible_duplicate", jana), (weak["warnings"]![0]!["type"]!.GetValue<string>(), weak["warnings"]![0]!["contactId"]!.GetValue<string>()));
        var home = weak["contactId"]!.GetValue<string>();

        // A merge the target's e-mails could not hold: the preview refuses it as the merge would, nothing is cut.
        Ok(await CallAsync(agent, "atlas_upsert_contact", new { contactId = home, contact = new { emails = Enumerable.Range(1, 20).Select(i => $"home{i}-{m_jana}").ToArray() } }));
        Assert.Equal("contact_merge_over_capacity", ErrorCode(await CallAsync(agent, "atlas_merge_contacts", new { sourceId = home, targetId = jana })));
        Ok(await CallAsync(agent, "atlas_upsert_contact", new { contactId = home, contact = new { emails = Array.Empty<string>() } }));
        var preview = Ok(await CallAsync(agent, "atlas_merge_contacts", new { sourceId = home, targetId = jana }));
        Assert.False(preview["confirmed"]!.GetValue<bool>());
        Assert.Equal(2L, await LiveAsync(f, jana, home));
        var merged = Ok(await CallAsync(agent, "atlas_merge_contacts", new { sourceId = home, targetId = jana, confirm = true }));
        Assert.Equal(jana, merged["contactId"]!.GetValue<string>());
        Assert.Equal(1L, await LiveAsync(f, jana, home));
        // The merged contact redirects: a reference to it reads the target.
        var live = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/contacts/live", new { ids = new[] { home } }), HttpStatusCode.OK);
        Assert.Equal(("ok", jana), (live["items"]![home]!["state"]!.GetValue<string>(), live["items"]![home]!["contactId"]!.GetValue<string>()));
    }

    [PostgreSqlFact]
    public async Task Contact_writes_leave_a_space_switched_out_of_ai_alone_and_the_duplicates_are_listed_as_on_the_web()
    {
        await using var f = await SiteAsync();
        var (_, agent) = await AgentAsync(f);
        var hidden = await f.SpaceAsync("Mimo AI");
        var jana = await ContactAsync(f, $$"""{"name":"Jana","emails":["{{m_jana}}"],"phones":["{{Phone}}"]}""");
        var secret = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope = "space", spaceId = hidden, data = JsonNode.Parse($$"""{"name":"Jana Domů","phones":["{{PhoneIntl}}"]}""") }),
            HttpStatusCode.Created))["id"]!.GetValue<string>();

        // The pairs of the whole directory: this test's are those of its two contacts.
        var ours = new[] { jana, secret };
        IEnumerable<JsonNode> Ours(JsonNode list) => list["pairs"]!.AsArray().Select(x => x!)
            .Where(x => ours.Contains(x["a"]!["contactId"]!.GetValue<string>()) && ours.Contains(x["b"]!["contactId"]!.GetValue<string>()));
        var web = Assert.Single(Ours(await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/contacts/duplicates"), HttpStatusCode.OK)));
        var pair = Assert.Single(Ours(Ok(await CallAsync(agent, "atlas_list_contact_duplicates"))));
        Assert.Equal(web.ToJsonString(), pair.ToJsonString());
        Assert.Equal("phone", pair["reason"]!.GetValue<string>());
        Assert.Equal(new[] { jana, secret }.Order(), new[] { pair["a"]!["contactId"]!.GetValue<string>(), pair["b"]!["contactId"]!.GetValue<string>() }.Order());

        Assert.False(Ok(await CallAsync(agent, "atlas_list_contact_duplicates"))["truncated"]!.GetValue<bool>());
        var read = Ok(await CallAsync(agent, "atlas_get_contact", new { contactId = secret }));
        Assert.Equal(("Jana Domů", 0), (read["contact"]!["name"]!.GetValue<string>(), read["usageCount"]!.GetValue<int>()));
        Assert.Equal(Phone, Ok(await CallAsync(agent, "atlas_get_contact", new { contactId = jana }))["contact"]!["phones"]![0]!.GetValue<string>());

        await f.ExcludeFromAiAsync(hidden);
        Assert.Equal("contact_missing", ErrorCode(await CallAsync(agent, "atlas_get_contact", new { contactId = secret })));
        Assert.Empty(Ours(Ok(await CallAsync(agent, "atlas_list_contact_duplicates"))));
        Assert.Equal("contact_missing", ErrorCode(await CallAsync(agent, "atlas_upsert_contact", new { contactId = secret, contact = new { title = "PM" } })));
        Assert.Equal("contact_missing", ErrorCode(await CallAsync(agent, "atlas_merge_contacts", new { sourceId = secret, targetId = jana, confirm = true })));
        Assert.Equal("contact_missing", ErrorCode(await CallAsync(agent, "atlas_merge_contacts", new { sourceId = jana, targetId = secret, confirm = true })));
        Assert.Equal(0L, await f.Site.ScalarAsync("""SELECT count(*) FROM "ContentResource" WHERE "Id" IN (@jana::uuid, @secret::uuid) AND ("TrashedAt" IS NOT NULL OR "Data" ? 'title')""",
            ("jana", jana), ("secret", secret)));

        // The web's directory-wide reads are throttled per user as search is.
        var endpoints = f.Site.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToArray();
        Assert.All(new[] { "/api/contacts/live", "/api/contacts/duplicates" }, route => Assert.Equal("interactive-search", endpoints.Single(e => e.RoutePattern.RawText == route)
            .Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName));
    }

    [PostgreSqlFact]
    public async Task A_board_takes_contacts_by_reference_from_fields_or_an_entity_and_warns_about_a_second_card()
    {
        await using var f = await SiteAsync();
        var (space, agent) = await AgentAsync(f);
        var board = Ok(await CallAsync(agent, "atlas_create_board", new { spaceId = space, title = "Tým" }))["boardId"]!.GetValue<string>();
        var edited = Ok(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId = board,
            changes = new object[]
            {
                new { type = "create", item = new { kind = "object", x = 0, y = 0, width = 280, height = 160, z = 1,
                    props = new { entity = new { name = "Jana", emails = new[] { m_jana } }, view = "card" } } },
                new { type = "create", item = new { kind = "object", x = 300, y = 0, width = 280, height = 160, z = 2, props = new { entity = "email:" + m_jana } } },
            }
        }));
        var contacts = edited["contacts"]!.AsArray();
        var jana = contacts[0]!["contactId"]!.GetValue<string>();
        Assert.Equal((0, true, 1, jana, false), (contacts[0]!["index"]!.GetValue<int>(), contacts[0]!["created"]!.GetValue<bool>(),
            contacts[1]!["index"]!.GetValue<int>(), contacts[1]!["contactId"]!.GetValue<string>(), contacts[1]!["created"]!.GetValue<bool>()));
        Assert.Contains(contacts[1]!["warnings"]!.AsArray(), warning => warning!["type"]!.GetValue<string>() == "contact_repeated");
        Assert.Contains("Kontakt už tu je – vložen podruhé.", Text(await CallAsync(agent, "atlas_edit_board", new
        {
            boardId = board, changes = new object[] { new { type = "create", item = new { kind = "object", x = 0, y = 300, width = 280, height = 160, z = 3, props = new { contactId = jana } } } }
        })), StringComparison.Ordinal);
        Assert.Equal(3L, await f.Site.ScalarAsync("""SELECT count(*) FROM "BoardItem" WHERE "TargetResourceId"=@jana""", ("jana", Guid.Parse(jana))));

        // An unknown entity names the change.
        var refused = await CallAsync(agent, "atlas_edit_board", new
        {
            boardId = board, changes = new object[] { new { type = "create", item = new { kind = "object", x = 0, y = 0, width = 280, height = 160, z = 4, props = new { entity = "email:nikdo@x.cz" } } } }
        });
        Assert.Equal("entity_missing", ErrorCode(refused));
        Assert.Contains("Změna č. 0", Text(refused), StringComparison.Ordinal);

        // The entity list names the directory contact to reference.
        // The site's outbox delivers in memory, after the commit.
        await Events.OutboxTests.WaitForAsync(f.Site.ConnectionString, $"""SELECT count(*) FROM "EntityOccurrence" WHERE "ResourceId" = '{jana}'""");
        var entities = Structured(await CallAsync(agent, "atlas_list_entities", new { identity = "email:" + m_jana }));
        Assert.Equal(jana, entities["items"]![0]!["contactId"]!.GetValue<string>());
    }

    // A site of the class: its tests run one after another, so a test asserts on what the directory gained since it began.
    private static Task<McpSite> SiteAsync() => McpSite.CreateAsync(settings: settings => settings["Modules:Boards:Enabled"] = "true", sharedAs: nameof(ContactMcpTests));

    [PostgreSqlFact]
    public async Task The_personal_api_returns_what_a_page_write_did_with_its_contacts_as_mcp_does()
    {
        await using var f = await SiteAsync();
        var (space, _) = await AgentAsync(f);
        var jana = await ContactAsync(f, $$"""{"name":"Jana","emails":["{{m_jana}}"],"phones":["{{Phone}}"]}""");
        var (_, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"]);
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);

        // A phone alone is a weak match (K3): a new contact, and the candidate said.
        var created = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages",
            new { spaceId = space, title = "Lidé", markdown = $"Volejte [Jana Domů](a-contact:?name=Jana%20Dom%C5%AF&phone={Uri.EscapeDataString(PhoneE164)}).\n" }), HttpStatusCode.Created);
        var warning = created["contacts"]!["warnings"]![0]!;
        Assert.Equal(("possible_duplicate", jana), (warning["type"]!.GetValue<string>(), warning["contactId"]!.GetValue<string>()));
        Assert.Single(created["contacts"]!["created"]!.AsArray());

        var saved = await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{created["id"]!.GetValue<string>()}",
            new { markdown = $"Volejte [Jana](a-contact:?name=Jana&email={m_jana}).\n", expectedVersion = created["version"]!.GetValue<int>() }), HttpStatusCode.OK);
        Assert.Equal(jana, Assert.Single(saved["contacts"]!["used"]!.AsArray())!.GetValue<string>());
    }

    private static async Task<(string Space, McpClient Agent)> AgentAsync(McpSite f)
    {
        var space = await f.FirstSpaceAsync();
        await f.AdminTokenAsync("read");
        var (_, secret) = await IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["mcp"]);
        return (space, await f.ClientAsync(secret));
    }

    private static async Task<string> ContactAsync(McpSite f, string data) => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/objects",
        new { typeId = "atlas.contact", scope = "wiki", data = JsonNode.Parse(data) }), HttpStatusCode.Created))["id"]!.GetValue<string>();

    private static async Task<long> LiveAsync(McpSite f, string a, string b) => (long)(await f.Site.ScalarAsync("""
        SELECT count(*) FROM "ContentResource" WHERE "Id" IN (@a::uuid, @b::uuid) AND "TrashedAt" IS NULL
        """, ("a", a), ("b", b)))!;

    private static JsonNode Ok(ModelContextProtocol.Protocol.CallToolResult result)
    {
        Assert.True(result.IsError != true, Text(result));
        return Structured(result);
    }
}
