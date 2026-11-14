using System.Text.Json.Nodes;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>Search reads the stored projection, so Projector's requirement blocks are found by their text (spec 2026-10-27 §1.7).</summary>
public sealed class RequirementSearchTests
{
    [PostgreSqlFact]
    public async Task A_published_page_is_found_by_the_title_and_the_note_of_a_requirement()
    {
        await using var f = await SearchFixture.CreateAsync();
        var content = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "requirements.json")))![0]!["content"]!;
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "KBN Production", content.ToJsonString(), null), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);

        Assert.Equal(page.Id, Assert.Single((await f.Find(f.Reader, "certifikát")).Items).Id);
        Assert.Equal(page.Id, Assert.Single((await f.Find(f.Reader, "poznámka pod požadavkem")).Items).Id);
    }
}
