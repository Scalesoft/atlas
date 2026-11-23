using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Wolverine;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.Mcp.Publishing;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Mcp.McpSite;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// G5 (D10) for every MCP tool the server registers, not only the ones a test remembers (SEC-Z-4): a space switched out of AI and MCP
/// is invisible to MCP. Each tool is called by the space's administrator with the ids of a page, a board, a tree node, a contact, an
/// attachment and a tag of such a space, its name as the query, and a plain value for anything else required. A required id the test has
/// none of fails here unless <see cref="OwnIds"/> says why the tool needs no id of the space for it. A tool answers a missing one (not_found,
/// target_not_found, contact_missing…), or succeeds
/// without showing anything of the space; nothing there changes. A tool whose plain call stops at its own argument check before
/// G5 gets the arguments that reach it in <see cref="Reaching"/>; any other error fails here.
/// </summary>
[Trait("Category", "Guard")]
public sealed class McpG5CoverageTests
{
    private const string Marker = "Kvasarlex";

    /// <summary>What the space shows: its name and the titles and text of its content. The query alone is the marker, so an echo of it is
    /// not a leak.</summary>
    private static readonly string[] Shown = [Marker + " prostor", Marker + " stránka", Marker + " obsah", Marker + " tabule", Marker + " Kontakt", Marker + " příloha"];

    /// <summary>Required ids that address no object of a space, with the reason; any other required id needs one of the space.</summary>
    private static readonly Dictionary<string, string> OwnIds = new(StringComparer.Ordinal)
    {
        ["blockId"] = "A block of the page given by pageId: the tool decides G5 on that page first.",
        ["headingId"] = "A section of the page given by pageId: the tool decides G5 on that page first.",
        ["revisionId"] = "A revision of the page given by pageId: the tool decides G5 on that page first.",
        ["noteId"] = "A personal note belongs to its owner, not to a space.",
        ["jobId"] = "A background job is the platform's, not a space's content: its runs of a space belong to the space's administrators (3.3 plan §4.6).",
        ["runId"] = "A run of a background job, as jobId.",
    };

    [PostgreSqlFact]
    public async Task Every_mcp_tool_answers_a_space_out_of_ai_as_missing_and_changes_nothing_there()
    {
        await using var f = await McpSite.CreateAsync(settings: settings =>
        {
            foreach (var (key, value) in TestModules.AllEnabledSettings)
            {
                settings[key] = value;
            }
        });
        var home = await f.FirstSpaceAsync();
        var included = await PageAsync(f.Admin, home, "Viditelná", Rich(Paragraph("Text.")), "Text.");
        var space = await f.SpaceAsync(Marker + " prostor");
        var page = await PageAsync(f.Admin, space, Marker + " stránka", Rich(Paragraph(Marker + " obsah.")), Marker + " obsah.");
        var board = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/boards",
            new { title = Marker + " tabule" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var contact = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, "/api/objects",
            new { typeId = "atlas.contact", scope = "space", spaceId = space, data = new { name = Marker + " Kontakt", emails = new[] { "kvasarlex@example.com" } } }),
            HttpStatusCode.Created))["id"]!.GetValue<string>();
        var reference = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/tree/references",
            new { targetId = contact }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var agent = await f.ClientAsync(await f.AdminTokenAsync("read", "write"));
        // Labels and an attachment, so a write of them and a read by their own ids reach the space.
        var file = Encoding.UTF8.GetBytes("Kvasarlex.");
        var attachment = Structured(await CallAsync(agent, "atlas_upload_attachment", new
        {
            pageId = page.Id, kind = "file", fileName = Marker + " příloha.txt", contentBase64 = Convert.ToBase64String(file),
            sha256 = Convert.ToHexStringLower(SHA256.HashData(file))
        }))["attachmentId"]!.GetValue<string>();
        var tag = Structured(await CallAsync(agent, "atlas_set_tags", new { pageId = page.Id, add = new[] { "kvasarlex" } }))["tags"]![0]!["id"]!.GetValue<string>();
        Assert.NotEqual(true, (await CallAsync(agent, "atlas_set_decoration", new { pageId = page.Id, icon = "book-open", color = "blue-3" })).IsError);
        // The check below can see the space: before the switch, the agent finds it.
        Assert.Contains(Marker + " stránka", Text(await CallAsync(agent, "atlas_search", new { query = Marker })), StringComparison.Ordinal);
        await f.ExcludeFromAiAsync(space);
        var before = await StateAsync(f, space, home);

        var ids = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spaceId"] = space, ["targetSpaceId"] = space, ["pageId"] = page.Id, ["sourcePageId"] = page.Id, ["parentId"] = page.Id,
            ["boardId"] = board, ["referenceId"] = reference, ["contactId"] = contact, ["objectId"] = contact, ["sourceId"] = contact,
            ["targetId"] = contact, ["attachmentId"] = attachment, ["tagId"] = tag,
        };
        var note = new { kind = "note", x = 0, y = 0, width = 200, height = 120, z = 1, props = new { } };
        var reaching = Reaching(page, included, space, board, contact, home, attachment, note, tag);
        var tools = f.Site.Services.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        Assert.Empty(reaching.Keys.Except(tools.Select(tool => tool.Name)));
        var failures = new List<string>();
        foreach (var tool in tools)
        {
            var unknown = reaching.ContainsKey(tool.Name) ? [] : UnknownIds(tool.InputSchema, ids);
            if (unknown.Length > 0)
            {
                failures.Add($"{tool.Name} requires {string.Join(", ", unknown)}: give it an id of the space or a reason in OwnIds.");
                continue;
            }
            var calls = reaching.TryGetValue(tool.Name, out var own) ? own : Plain(tool.InputSchema, ids, page.Version);
            foreach (var arguments in calls)
            {
                var result = await CallAsync(agent, tool.Name, arguments);
                var answer = Text(result) + result.StructuredContent?.GetRawText();
                var code = result.IsError == true ? Structured(result)["code"]?.GetValue<string>() : null;
                if (Shown.FirstOrDefault(shown => answer.Contains(shown, StringComparison.Ordinal)) is { } leaked)
                {
                    failures.Add($"{tool.Name} {JsonSerializer.Serialize(arguments)} shows \"{leaked}\" of a space out of AI.");
                }
                else if (code is not null && !code.EndsWith("not_found", StringComparison.Ordinal) && !code.EndsWith("_missing", StringComparison.Ordinal))
                {
                    failures.Add($"{tool.Name} {JsonSerializer.Serialize(arguments)} stopped with {code} before G5: give it arguments that reach it. {Text(result)}");
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.Equal(before, await StateAsync(f, space, home));
    }

    /// <summary>GAP5-4: the in-process assistant hands what it reads to a model as MCP does, so the handlers that decide G5 themselves
    /// hide a space out of AI from it too, whatever tools the assistant is offered today.</summary>
    [PostgreSqlFact]
    public async Task On_the_assistant_channel_a_space_out_of_ai_is_missing_to_boards_tree_nodes_and_requirement_writes()
    {
        await using var f = await McpSite.CreateAsync(settings: settings =>
        {
            foreach (var (key, value) in TestModules.AllEnabledSettings)
            {
                settings[key] = value;
            }
        });
        var space = await f.SpaceAsync("Mimo AI");
        var page = await PageAsync(f.Admin, space, "Stránka", Rich(Paragraph("Text.")), "Text.");
        var board = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/boards",
            new { title = "Tabule" }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        var reference = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Post, $"/api/spaces/{space}/tree/references",
            new { targetId = page.Id }), HttpStatusCode.Created))["id"]!.GetValue<string>();
        await f.ExcludeFromAiAsync(space);

        Assert.Equal(404, (await AssistantAsync(f, bus => bus.InvokeAsync<BoardDto>(new GetBoard(Guid.Parse(board))))).Status);
        Assert.Equal(404, (await AssistantAsync(f, bus => bus.InvokeAsync(new RemoveTreeReference(Guid.Parse(reference))))).Status);
        var write = await AssistantAsync(f, bus => bus.InvokeAsync<RequirementWriteResult>(new RemoveRequirement(Guid.Parse(page.Id), "x", page.Version)));
        Assert.Equal((404, McpVisibility.MissingPage), (write.Status, write.Message));
        Assert.Equal(1L, await f.Site.ScalarAsync("""SELECT count(*) FROM "TreeReference" WHERE "Id"=@id""", ("id", Guid.Parse(reference))));
    }

    /// <summary>G5 asks <see cref="RequestChannel.IsAiBound"/>: a branch on IsMcp would leave the assistant out (GAP5-4). IsMcp stays for
    /// what only a token over /mcp may do, the publish of MCP's own confirmation.</summary>
    [Fact]
    public void Only_the_mcp_publish_token_check_asks_whether_the_channel_is_mcp()
    {
        // The record's own printing reads every property.
        var askers = ServerIl.Calls.Where(call => call.Callee.DeclaringType == typeof(RequestChannel) && call.Callee.Name == "get_" + nameof(RequestChannel.IsMcp)
            && call.Type != typeof(RequestChannel)).Select(call => call.Type).Distinct().ToArray();
        Assert.Equal([typeof(McpPublishChannel)], askers);
    }

    /// <summary>The refusal of one bus call made as the assistant's tool session makes it: the administrator's cookie request with the marker.</summary>
    private static async Task<ApiException> AssistantAsync(McpSite f, Func<IMessageBus, Task> call)
    {
        var stamp = (string)(await f.Site.ScalarAsync("""SELECT "SecurityStamp" FROM "UserAccount" WHERE "Id"=@id""", ("id", Guid.Parse(f.AdminId))))!;
        await using var scope = f.Site.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, f.AdminId), new Claim("stamp", stamp)], "Cookies")),
        };
        context.Items[HttpRequestChannel.AssistantMarker] = true;
        accessor.HttpContext = context;
        try
        {
            return await Assert.ThrowsAsync<ApiException>(() => call(scope.ServiceProvider.GetRequiredService<IMessageBus>()));
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    /// <summary>The tools whose plain call stops at their own argument check, with arguments that pass it; and the tools that take
    /// two places, called with each of them out of AI.</summary>
    private static Dictionary<string, Dictionary<string, object?>[]> Reaching((string Id, int Version) page, (string Id, int Version) included, string space,
        string board, string contact, string home, string attachment, object note, string tag)
    {
        var bytes = Encoding.UTF8.GetBytes("x");
        return new(StringComparer.Ordinal)
        {
            ["atlas_apply_labels"] = [new() { ["changes"] = new[] { new { pageId = page.Id, addTags = new[] { "x" } } } }],
            ["atlas_arrange_pages"] = [new() { ["expectedTreeVersion"] = 1, ["moves"] = new[] { new { pageId = page.Id } } }],
            ["atlas_set_decoration"] = [new() { ["pageId"] = page.Id, ["clear"] = true }],
            // The former direct tool (write since 3.6): a mode is required.
            ["atlas_set_publication"] = [new() { ["pageId"] = page.Id, ["mode"] = "Live" }],
            // runId is a number: the plain call would send a Guid; a job that does not exist is the call that reaches the handler.
            ["atlas_cancel_job_run"] = [new() { ["jobId"] = "none", ["runId"] = 1 }],
            ["atlas_set_tags"] = [new() { ["pageId"] = page.Id, ["add"] = new[] { "x" } }],
            // A kind is required; the page's and the board's overview each.
            ["atlas_get_overview"] = [new() { ["kind"] = "contacts", ["pageId"] = page.Id }, new() { ["kind"] = "requirements", ["boardId"] = board }],
            ["atlas_update_embed"] = [new() { ["pageId"] = page.Id, ["expectedVersion"] = page.Version, ["attachmentId"] = attachment, ["alt"] = "x" }],
            ["atlas_upload_attachment"] = [new()
            {
                ["pageId"] = page.Id, ["kind"] = "file", ["fileName"] = "x.txt", ["contentBase64"] = Convert.ToBase64String(bytes),
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes))
            }],
            ["atlas_edit_board"] = [new() { ["boardId"] = board, ["changes"] = new[] { new { type = "create", item = note } } }],
            // sourceId is optional since accountId is its alternative (account contacts); the pair of contacts is the call that reaches G5.
            ["atlas_merge_contacts"] = [new() { ["sourceId"] = contact, ["targetId"] = contact }],
            // The space's tag by its name, and merged into itself: a topic only that space shows is missing before anything is checked.
            ["atlas_find_experts"] = [new() { ["topic"] = Marker }],
            ["atlas_merge_topics"] = [new() { ["tagId"] = tag, ["intoId"] = tag, ["confirm"] = true }],
            ["atlas_add_tree_reference"] =
            [
                new() { ["spaceId"] = space, ["targetId"] = Guid.NewGuid() },
                new() { ["spaceId"] = home, ["targetId"] = contact },
                new() { ["spaceId"] = home, ["targetId"] = page.Id },
            ],
            ["atlas_move_page_to_space"] =
            [
                new()
                {
                    ["pageId"] = page.Id, ["targetSpaceId"] = home, ["expectedSourceTreeVersion"] = 1, ["expectedTargetTreeVersion"] = 1,
                    ["expectedVersion"] = page.Version, ["includeSubtree"] = true, ["acknowledgeAccessChange"] = true
                },
                new()
                {
                    ["pageId"] = included.Id, ["targetSpaceId"] = space, ["expectedSourceTreeVersion"] = 1, ["expectedTargetTreeVersion"] = 1,
                    ["expectedVersion"] = included.Version, ["includeSubtree"] = true, ["acknowledgeAccessChange"] = true
                },
            ],
            ["projector_insert_from_deployment"] =
            [
                new() { ["pageId"] = page.Id, ["sourcePageId"] = included.Id, ["expectedVersion"] = page.Version },
                new() { ["pageId"] = included.Id, ["sourcePageId"] = page.Id, ["expectedVersion"] = included.Version },
            ],
            ["projector_create_deployment"] =
            [
                new() { ["sourcePageId"] = page.Id, ["spaceId"] = home, ["title"] = "x" },
                // With a module the target space's catalog would be read: G5 answers before it.
                new() { ["sourcePageId"] = included.Id, ["spaceId"] = space, ["title"] = "x", ["moduleKeys"] = new[] { "x" } },
            ],
            ["projector_insert_requirements"] = [new() { ["pageId"] = page.Id, ["keys"] = new[] { "x" }, ["expectedVersion"] = page.Version }],
            ["projector_update_requirements"] = [new() { ["pageId"] = page.Id, ["updates"] = new[] { new { blockId = "x", title = "x" } }, ["expectedVersion"] = page.Version }],
        };
    }

    /// <summary>The plain calls from the tool's input schema: every required property set (the space's ids by name, the marker as a
    /// query, a plain value of its type otherwise), and one call per optional id of the space, since optional ids are often alternatives.</summary>
    private static IEnumerable<Dictionary<string, object?>> Plain(JsonElement schema, IReadOnlyDictionary<string, string> ids, int version)
    {
        if (!schema.TryGetProperty("properties", out var properties))
        {
            return [[]];
        }
        var required = schema.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(x => x.GetString()!).ToHashSet() : [];
        var always = new Dictionary<string, object?>();
        var optional = new List<(string Name, object Value)>();
        foreach (var property in properties.EnumerateObject())
        {
            var name = property.Name;
            object? value = ids.TryGetValue(name, out var id) ? id
                : name is "pageIds" ? new[] { ids["pageId"] }
                : name is "query" or "q" or "term" ? Marker
                : null;
            if (value is not null && !required.Contains(name))
            {
                optional.Add((name, value));
            }
            else if (required.Contains(name))
            {
                always[name] = value ?? (name == "expectedVersion" ? version : OwnIds.ContainsKey(name) ? Guid.NewGuid() : Of(property.Value));
            }
        }
        return optional.Count == 0 ? [always] : optional.Select(x => new Dictionary<string, object?>(always) { [x.Name] = x.Value });
    }

    /// <summary>The required ids of a tool that are neither an id of the space nor in <see cref="OwnIds"/>: a random one would make the
    /// tool answer not_found for a missing object, whatever G5 does.</summary>
    private static string[] UnknownIds(JsonElement schema, IReadOnlyDictionary<string, string> ids)
        => schema.TryGetProperty("required", out var names)
            ? names.EnumerateArray().Select(x => x.GetString()!)
                .Where(name => (name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("Ids", StringComparison.Ordinal))
                    && !ids.ContainsKey(name) && name != "pageIds" && !OwnIds.ContainsKey(name)).ToArray()
            : [];

    private static object Of(JsonElement schema)
    {
        if (schema.TryGetProperty("enum", out var values))
        {
            return values[0].Clone();
        }
        var type = schema.TryGetProperty("type", out var t) ? t.ValueKind == JsonValueKind.Array ? t[0].GetString() : t.GetString() : "string";
        return type switch
        {
            "integer" or "number" => 1,
            // A confirmation the plain call gives, so a tool goes as far as it would.
            "boolean" => true,
            "array" => Array.Empty<object>(),
            "object" => new Dictionary<string, object>(),
            _ => "x"
        };
    }

    /// <summary>Everything a write could change in the space: its pages with their tags, icon and attachments, boards, contacts and tree nodes.
    /// Pages and tree nodes also of the test's other space (home), the only other place the calls name: the site's other tests write theirs.</summary>
    private static async Task<string> StateAsync(McpSite f, string space, string home)
        => (string)(await f.Site.ScalarAsync("""
            SELECT concat_ws('|',
              (SELECT string_agg(p."Id" || ':' || p."SpaceId" || ':' || coalesce(CAST(p."TrashedAt" AS text),'-') || ':' || (SELECT count(*) FROM "PageRevision" r WHERE r."PageId"=p."Id"), ',' ORDER BY p."Id") FROM "Page" p WHERE p."SpaceId" IN (@space,@home)),
              (SELECT string_agg(b."Id" || ':' || b."Title" || ':' || b."IsTrashed" || ':' || coalesce(CAST(b."ParentPageId" AS text),'-'), ',') FROM "Board" b WHERE b."SpaceId"=@space),
              (SELECT string_agg(c."Id" || ':' || CAST(c."Data" AS text) || ':' || coalesce(CAST(c."TrashedAt" AS text),'-'), ',') FROM "ContentResource" c WHERE c."SpaceId"=@space),
              (SELECT string_agg(t."Id" || ':' || coalesce(CAST(t."ParentPageId" AS text),'-') || ':' || t."IsTrashed", ',' ORDER BY t."Id") FROM "TreeReference" t WHERE t."SpaceId" IN (@space,@home)),
              (SELECT string_agg(g."Id" || ':' || g."TagId" || ':' || coalesce(CAST(g."ActiveTo" AS text),'-'), ',' ORDER BY g."Id") FROM "PageTag" g JOIN "Page" p ON p."Id"=g."PageId" WHERE p."SpaceId"=@space),
              (SELECT string_agg(d."PageId" || ':' || d."RevisionId" || ':' || coalesce(d."Icon",'-') || ':' || coalesce(d."Color",'-'), ',' ORDER BY d."PageId") FROM "PageDecoration" d JOIN "Page" p ON p."Id"=d."PageId" WHERE p."SpaceId"=@space),
              (SELECT string_agg(a."Id" || ':' || coalesce(CAST(a."ActiveTo" AS text),'-'), ',' ORDER BY a."Id") FROM "Attachment" a JOIN "Page" p ON p."Id"=a."PageId" WHERE p."SpaceId"=@space))
            """, ("space", Guid.Parse(space)), ("home", Guid.Parse(home))))!;
}
