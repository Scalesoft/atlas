using System.Net;
using Microsoft.Extensions.Options;
using Xunit;
namespace Atlas.YouTrack.Tests;

public sealed class YouTrackResolutionTests
{
    private static readonly Guid Space = Guid.NewGuid();
    private static YouTrackOptions Settings() => new() { Enabled = true, BaseUrl = "https://tracker.example/youtrack", Token = "fixture-secret", SpaceProjects = new() { [Space] = ["0-7"] } };
    private static YouTrackClient Client(Transport transport) => new(new HttpClient(transport), Options.Create(Settings()));
    private const string Issue = """{"id":"2-1","idReadable":"TEAM-1","summary":"Allowed issue","project":{"id":"0-7"},"customFields":[{"name":"State","$type":"StateIssueCustomField","value":{"name":"In Progress"}}]}""";

    [Theory]
    [InlineData("TEAM-1")]
    [InlineData("2-1")]
    [InlineData("https://tracker.example/youtrack/issue/TEAM-1/a-friendly-title")]
    [InlineData("https://tracker.example/youtrack/issue/TEAM-1#focus=Comments-4-12.0-0")]
    [InlineData("https://tracker.example/youtrack/issue/TEAM-1?tab=history")]
    public async Task Resolves_only_configured_origin_and_preserves_state(string reference)
    {
        var transport = new Transport(Issue);
        var result = await Client(transport).ResolveAsync(Settings(), Space, reference);
        Assert.NotNull(result);
        Assert.Equal("In Progress", result.State);
        Assert.Equal("https://tracker.example/youtrack/issue/TEAM-1", result.Url);
        Assert.StartsWith("/youtrack/api/issues/", transport.Uri!.AbsolutePath);
        Assert.DoesNotContain("fixture-secret", transport.Uri.ToString());
    }

    [Theory]
    [InlineData("""{"name":"Due Date","$type":"DateIssueCustomField","value":1794700800000}""", "2026-11-15")]
    [InlineData("""{"name":"Due Date","$type":"DateIssueCustomField","value":null}""", null)]
    [InlineData("""{"name":"Start","$type":"DateIssueCustomField","value":1794700800000}""", null)]
    [InlineData("""{"name":"Due Date","$type":"DateIssueCustomField","value":99999999999999999}""", null)]
    public async Task Reads_the_due_date_field_and_treats_a_missing_or_empty_one_as_no_date(string field, string? expected)
    {
        var json = Issue.Replace("]}", "," + field + "]}");
        var result = await Client(new Transport(json)).ResolveAsync(Settings(), Space, "TEAM-1");
        Assert.Equal("In Progress", result!.State);
        Assert.Equal(expected, result.DueDate);
    }

    [Fact]
    public async Task An_issue_without_custom_fields_has_no_due_date()
    {
        var result = await Client(new Transport(Issue.Replace(""","customFields":[{"name":"State","$type":"StateIssueCustomField","value":{"name":"In Progress"}}]""", ""))).ResolveAsync(Settings(), Space, "TEAM-1");
        Assert.Null(result!.DueDate);
    }

    [Theory]
    [InlineData("https://evil.example/issue/TEAM-1")]
    [InlineData("https://tracker.example/other/issue/TEAM-1")]
    [InlineData("https://user@tracker.example/youtrack/issue/TEAM-1")]
    [InlineData("../admin/users")]
    public async Task Untrusted_references_never_contact_upstream(string reference)
    {
        var transport = new Transport(Issue);
        await Assert.ThrowsAsync<ArgumentException>(() => Client(transport).ResolveAsync(Settings(), Space, reference));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Unknown_and_disallowed_issues_both_return_null()
    {
        Assert.Null(await Client(new Transport("", HttpStatusCode.NotFound)).ResolveAsync(Settings(), Space, "TEAM-1"));
        Assert.Null(await Client(new Transport(Issue.Replace("0-7", "0-8"))).ResolveAsync(Settings(), Space, "TEAM-1"));
    }

    [Fact]
    public void Issue_key_is_one_spelling_of_an_id_or_an_issue_url()
    {
        Assert.Equal("TEAM-1", YouTrackClient.IssueKey(Settings(), "team-1"));
        Assert.Equal("TEAM-1", YouTrackClient.IssueKey(Settings(), "https://tracker.example/youtrack/issue/team-1#focus=Comments-4-1.0-0"));
        Assert.Throws<YouTrackUnavailableException>(() => YouTrackClient.IssueKey(new YouTrackOptions(), "TEAM-1"));
    }

    [Fact]
    public async Task Many_keys_are_one_search_of_the_allowed_projects_and_a_missing_one_is_null()
    {
        var transport = new Transport("[" + Issue + "]");
        var client = Client(transport);
        Assert.Equal(["TEAM"], await client.ProjectKeysAsync(Settings(), Space));
        var found = await client.ResolveManyAsync(Settings(), Space, ["TEAM"], ["TEAM-1", "TEAM-2", "UTF-8"]);
        Assert.Equal("Allowed issue", found["TEAM-1"]!.Title);
        Assert.Null(found["TEAM-2"]);
        // Not asked, so not answered: the host must not take it for an issue YouTrack does not have.
        Assert.False(found.ContainsKey("UTF-8"));
        Assert.Equal(2, transport.Calls);
        Assert.Equal("issue id: TEAM-1, TEAM-2", Uri.UnescapeDataString(transport.Uri!.Query.Split("query=")[1]));
        Assert.Contains("$top=2", transport.Uri.Query);
    }

    [Fact]
    public async Task Many_keys_of_no_allowed_project_never_search_and_a_non_key_is_refused()
    {
        var transport = new Transport("[" + Issue + "]");
        Assert.Empty(await Client(transport).ResolveManyAsync(Settings(), Space, ["TEAM"], ["UTF-8", "ISO-9001", "2-1"]));
        Assert.Equal(0, transport.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => Client(transport).ResolveManyAsync(Settings(), Space, ["TEAM"], ["TEAM-1) or (project: X"]));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Connection_test_reads_projects_with_bounded_payload()
    {
        var transport = new Transport("""[{"id":"0-7","name":"Team","shortName":"TEAM"}]""");
        var projects = await Client(transport).TestConnectionAsync(Settings());
        Assert.Equal("0-7", Assert.Single(projects).Id);
        Assert.Equal("/youtrack/api/admin/projects", transport.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task YouTrack_field_queries_are_not_mistaken_for_external_url_schemes()
    {
        var transport = new Transport("[" + Issue + "]");
        Assert.Single(await Client(transport).SearchAsync(Settings(), Space, "project:TEAM"));
        Assert.Equal("/youtrack/api/issues", transport.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task A_full_issue_reference_in_search_uses_resolve()
    {
        var transport = new Transport(Issue);
        Assert.Single(await Client(transport).SearchAsync(Settings(), Space, "https://tracker.example/youtrack/issue/TEAM-1"));
        Assert.Equal("/youtrack/api/issues/TEAM-1", transport.Uri!.AbsolutePath);
    }

    /// <summary>Answers a lookup of project 0-7 with its short name TEAM and any other request with <paramref name="body"/>, whose URI it
    /// records.</summary>
    private sealed class Transport(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/admin/projects/0-7", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"0-7","shortName":"TEAM"}""") });
            }
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
