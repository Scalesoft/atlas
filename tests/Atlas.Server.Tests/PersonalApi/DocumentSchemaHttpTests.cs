using System.Net;
using System.Net.Http.Json;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>What HTTP adds to the editor schema check (the editor package owns the rules; EditorDocumentValidationTests holds Atlas's cases): the personal API takes no rich JSON
/// at all, the web's save answers the problem body the editor's JSON is fixed from, and nothing is stored.</summary>
public sealed class DocumentSchemaHttpTests
{
    private const string Valid = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Text"}]}]}}""";

    // The production incident: an image attribute the editor's image node does not have.
    private const string Invalid = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"},{"type":"image","attrs":{"src":"/a.png","align":"center"}}]}}""";

    [PostgreSqlFact]
    public async Task Rich_json_is_refused_from_a_token_and_an_invalid_web_save_names_the_path_and_stores_nothing()
    {
        await using var site = await RightsSite.CreateAsync();
        await site.SetLevelAsync("write");
        using var api = await site.TokenClientAsync(["read", "write"]);
        var spaceId = await site.FirstSpaceAsync(api);
        var refused = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages",
            new { spaceId, parentId = (string?)null, title = "Příručka", contentJson = Valid, markdown = "Text" }), HttpStatusCode.BadRequest);
        Assert.Equal("content_json_not_accepted", refused["code"]!.GetValue<string>());
        var page = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages",
            new { spaceId, parentId = (string?)null, title = "Příručka", markdown = "Text" }), HttpStatusCode.Created);
        var id = page["id"]!.GetValue<string>();
        var version = page["version"]!.GetValue<int>();
        var stored = page["contentJson"]!.GetValue<string>();
        Assert.Equal("content_json_not_accepted", (await AtlasHttpSite.Json(await api.PutAsJsonAsync($"/api/v1/pages/{id}",
            new { contentJson = Invalid, markdown = "Text", expectedVersion = version }), HttpStatusCode.BadRequest))["code"]!.GetValue<string>());

        var problem = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(site.Owner, HttpMethod.Put, $"/api/pages/{id}",
            new { title = "Příručka", contentJson = Invalid, markdown = "Text", expectedVersion = version }), HttpStatusCode.BadRequest);
        Assert.Equal(("invalid_document", "document.content[1].attrs.align"), (problem["code"]!.GetValue<string>(), problem["path"]!.GetValue<string>()));
        Assert.Contains("document.content[1].attrs.align", problem["title"]!.GetValue<string>(), StringComparison.Ordinal);

        var current = await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{id}"), HttpStatusCode.OK);
        Assert.Equal((version, stored), (current["version"]!.GetValue<int>(), current["contentJson"]!.GetValue<string>()));
    }

    /// <summary>The editor nests lists without limit; 20 levels with a link exceed System.Text.Json's default depth of 64.</summary>
    [PostgreSqlFact]
    public async Task A_deeply_nested_list_with_a_link_is_stored_and_read_back()
    {
        var block = """{"type":"paragraph","content":[{"type":"text","text":"Odkaz","marks":[{"type":"link","attrs":{"href":"https://example.com"}}]}]}""";
        for (var level = 0; level < 20; level++)
        {
            block = $$$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{{block}}}]}]}""";
        }
        var content = $$$"""{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{{{block}}}]}}""";
        await using var site = await RightsSite.CreateAsync();
        await site.SetLevelAsync("write");
        using var api = await site.TokenClientAsync(["read", "write"]);
        var spaceId = await site.FirstSpaceAsync(api);
        var page = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages",
            new { spaceId, parentId = (string?)null, title = "Osnova", markdown = "Text" }), HttpStatusCode.Created);
        var id = page["id"]!.GetValue<string>();

        // Only the web editor writes rich JSON; the personal API reads it back.
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(site.Owner, HttpMethod.Put, $"/api/pages/{id}",
            new { title = "Osnova", contentJson = content, markdown = "- Odkaz", expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);

        var stored = await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{id}"), HttpStatusCode.OK);
        Assert.Equal(content, stored["contentJson"]!.GetValue<string>());
    }
}
