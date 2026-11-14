using System.Text.Json;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Search.Contracts;
using Xunit;

namespace Atlas.Server.Tests.Search;

public sealed class SearchWidgetTests
{
    private static string Rich(string name) => JsonSerializer.Serialize(new
    {
        format = "atlas-rich", version = 1,
        plugins = new[] { new { id = "contacts", version = 2 }, new { id = "heading-anchors", version = 1 }, new { id = "youtrack", version = 1 } },
        document = new { type = "doc", content = new object[] {
            new { type = "heading", attrs = new { level = 2, headingId = "team" }, content = new[] { new { type = "text", text = "Team" } } },
            new { type = "contactCard", attrs = new { data = new { version = 1, contacts = new[] {
                new { name, title = "Support", organization = "Ocean", username = "jnova", emails = new[] { "support@example.cz" }, phones = new[] { "+420 123 456 789" } },
                new { name = "Unrelated person", title = "Sales", organization = "Elsewhere", username = "sales", emails = new[] { "sales@example.cz" }, phones = Array.Empty<string>() }
            } } } },
            new { type = "paragraph", content = new object[] {
                new { type = "contactInline", attrs = new { data = new { version = 1, contacts = new[] { new { name = "Ocean inline" } } } } },
                new { type = "youtrackIssue", attrs = new { id = "2-8", key = "OC-8", title = "Ocean login", state = "Open", url = "https://tracker.example/issue/OC-8" } }
            } }
        } }
    });

    [PostgreSqlFact]
    public async Task Search_returns_matching_contact_and_issue_widgets_with_their_document_context()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Team directory", Rich("Jana Nová"),
            "Jana Nová Support Ocean support@example.cz +420 123 456 789 Ocean inline OC-8 Ocean login Open"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(page.Version), f.Writer, default);

        var response = await f.Find(f.Reader, "Ocean");
        var hit = Assert.Single(response.Items);
        var json = JsonSerializer.SerializeToElement(hit, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(json.TryGetProperty("widgets", out var widgets), "Search must expose typed widget results.");
        Assert.Equal(3, widgets.GetArrayLength());
        Assert.Equal("contact", widgets[0].GetProperty("kind").GetString());
        Assert.Equal("Jana Nová", widgets[0].GetProperty("title").GetString());
        Assert.Contains("support@example.cz", widgets[0].GetProperty("detail").GetString());
        Assert.Contains("jnova", widgets[0].GetProperty("detail").GetString());
        Assert.Equal("team", widgets[0].GetProperty("headingId").GetString());
        // The contact itself, as the page's contact block holds it, so a picker (a board's) takes it over without parsing detail.
        var contact = widgets[0].GetProperty("contact");
        Assert.Equal(("Jana Nová", "Support", "Ocean", "jnova", "support@example.cz", "+420 123 456 789"), (contact.GetProperty("name").GetString(),
            contact.GetProperty("title").GetString(), contact.GetProperty("organization").GetString(), contact.GetProperty("username").GetString(),
            contact.GetProperty("emails")[0].GetString(), contact.GetProperty("phones")[0].GetString()));
        Assert.Equal("Ocean inline", widgets[1].GetProperty("contact").GetProperty("name").GetString());
        Assert.Equal("Ocean inline", widgets[1].GetProperty("title").GetString());
        Assert.Equal("youtrack", widgets[2].GetProperty("kind").GetString());
        Assert.Equal("OC-8", widgets[2].GetProperty("key").GetString());
        Assert.Equal("Open", widgets[2].GetProperty("state").GetString());
    }

    [PostgreSqlFact]
    public async Task Widget_details_follow_the_authorized_revision_and_never_reveal_a_working_contact_to_readers()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Directory", Rich("Public Ocean"), "Public Ocean"), f.Writer, default);
        var published = await f.Pages.PublishAsync(page.Id, new(page.Version), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Directory", Rich("Private Ocean"), "Private Ocean", published.Version), f.Writer, default);

        foreach (var scope in new[] { "all", "published" })
        {
            var result = await f.Find(f.Reader, "Ocean", new SearchOptions(Content: scope));
            var json = JsonSerializer.SerializeToElement(Assert.Single(result.Items), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(json.TryGetProperty("widgets", out var widgets), "The published hit must carry its own widgets.");
            Assert.Equal("Public Ocean", widgets[0].GetProperty("title").GetString());
            Assert.DoesNotContain("Private Ocean", json.GetRawText());
        }
        var drafts = await f.Find(f.Writer, "Ocean", new SearchOptions(Content: "drafts"));
        var draft = JsonSerializer.SerializeToElement(Assert.Single(drafts.Items), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Private Ocean", draft.GetProperty("widgets")[0].GetProperty("title").GetString());
        Assert.Empty((await f.Find(f.Reader, "Ocean", new SearchOptions(Content: "drafts"))).Items);
    }

    [PostgreSqlFact]
    public async Task Widget_matches_use_the_same_technical_aliases_as_the_page_index()
    {
        await using var f = await SearchFixture.CreateAsync();
        await f.Pages.CreateAsync(new(f.SpaceId, null, "Skills", Rich("C# developer"), "C# developer"), f.Writer, default);
        var response = await f.Find(f.Writer, "C#");
        var hit = Assert.Single(response.Items);
        Assert.Contains(hit.Widgets, widget => widget.Title == "C# developer");
    }
}
