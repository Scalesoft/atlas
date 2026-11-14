using System.Net;
using Microsoft.Extensions.Options;
using Xunit;
namespace Atlas.YouTrack.Tests;

public sealed class YouTrackClientTests
{
    private static readonly Guid SpaceId = Guid.Parse("15c6aa37-bdda-4f2e-9fa8-bb8e7f01c6f0");
    private static YouTrackOptions Config() => new() { Enabled = true, BaseUrl = "https://tracker.example/youtrack/", Token = "test-secret", SpaceProjects = new() { [SpaceId] = ["0-7"] } };
    private static YouTrackClient Client(RecordingHandler handler, YouTrackOptions? options = null) => new(new HttpClient(handler), Options.Create(options ?? Config()));

    [Fact]
    public async Task Unconfigured_or_unmapped_space_never_contacts_upstream()
    {
        var transport = new RecordingHandler("[]");
        await Assert.ThrowsAsync<YouTrackUnavailableException>(() => Client(transport, new()).SearchAsync(SpaceId, "hello"));
        await Assert.ThrowsAsync<YouTrackUnavailableException>(() => Client(transport).SearchAsync(Guid.NewGuid(), "hello"));
        Assert.Equal(0, transport.Calls);
    }
    [Fact]
    public async Task Results_are_filtered_by_stable_project_id_even_when_query_contains_or()
    {
        var transport = new RecordingHandler("""[{"id":"2-1","idReadable":"OK-1","summary":"Allowed","project":{"id":"0-7"}},{"id":"2-2","idReadable":"SECRET-2","summary":"Forbidden","project":{"id":"0-8"}}]""");
        var issues = await Client(transport).SearchAsync(SpaceId, "hello or project: SECRET");
        var issue = Assert.Single(issues);
        Assert.Equal("OK-1", issue.Key);
        Assert.Equal("https://tracker.example/youtrack/issue/OK-1", issue.Url);
        Assert.Equal("Bearer test-secret", transport.Authorization);
        Assert.Equal("/youtrack/api/issues", transport.Uri!.AbsolutePath);
        Assert.DoesNotContain("test-secret", transport.Uri.ToString());
    }
    [Fact]
    public async Task Search_asks_only_the_allowed_projects_so_other_projects_cannot_crowd_them_out()
    {
        var transport = new RecordingHandler("""[{"id":"2-1","idReadable":"OK-1","summary":"Allowed","project":{"id":"0-7"}}]""");
        Assert.Single(await Client(transport).SearchAsync(SpaceId, "login bug"));
        Assert.Equal("project: OK and (login bug)", Uri.UnescapeDataString(transport.Uri!.Query.Split("query=")[1]));
        Assert.Equal(["/youtrack/api/admin/projects/0-7", "/youtrack/api/issues"], transport.Paths);
    }
    [Fact]
    public async Task Search_without_a_readable_allowed_project_asks_for_no_issues()
    {
        var transport = new RecordingHandler("[]", project: "");
        Assert.Empty(await Client(transport).SearchAsync(SpaceId, "hello"));
        Assert.Equal(["/youtrack/api/admin/projects/0-7"], transport.Paths);
    }
    [Theory]
    [InlineData(401)][InlineData(403)][InlineData(302)][InlineData(500)]
    public async Task Provider_failures_do_not_expose_response_body(int status)
    {
        var transport = new RecordingHandler("sensitive provider response", (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<YouTrackUnavailableException>(() => Client(transport).SearchAsync(SpaceId,"hello"));
        Assert.DoesNotContain("sensitive", error.ToString());
    }
    [Fact]
    public async Task Malformed_and_missing_project_results_cannot_escape_scope()
    {
        var transport = new RecordingHandler("""[{"id":"x","idReadable":"BAD-1","summary":"No project"},{"id":"x","idReadable":"OK-2","summary":"bad\ntext","project":{"id":"0-7"}}]""");
        Assert.Empty(await Client(transport).SearchAsync(SpaceId,"hello"));
    }
    [Fact]
    public async Task Invalid_queries_do_not_contact_upstream()
    {
        var transport = new RecordingHandler("[]");
        Assert.Empty(await Client(transport).SearchAsync(SpaceId, " "));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(transport).SearchAsync(SpaceId,new string('a',257)));
        Assert.Equal(0,transport.Calls);
    }
    [Fact]
    public async Task Stalled_response_body_times_out_without_caller_cancellation()
    {
        using var http = new HttpClient(new StalledBodyHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = new YouTrackClient(http, Options.Create(Config()));

        await Assert.ThrowsAsync<YouTrackUnavailableException>(() => client.SearchAsync(SpaceId, "hello", caller.Token));
        Assert.False(caller.IsCancellationRequested);
    }

    [Fact]
    public async Task Caller_cancellation_while_reading_body_is_not_reported_as_provider_failure()
    {
        var upstream = new StalledBodyHandler();
        using var http = new HttpClient(upstream) { Timeout = TimeSpan.FromSeconds(5) };
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = new YouTrackClient(http, Options.Create(Config()));

        var pending = client.SearchAsync(SpaceId, "hello", caller.Token);
        await upstream.BodyStarted.Task.WaitAsync(caller.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledContent(BodyStarted) });
    }

    private sealed class StalledContent(TaskCompletionSource started) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            return new TaskCompletionSource().Task.WaitAsync(cancellationToken);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Answers a lookup of the allowed project 0-7 with its short name (<paramref name="project"/>, none when empty) and any other
    /// request with <paramref name="body"/>, whose URI it records.</summary>
    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK, string project = "OK") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Paths.Add(request.RequestUri!.AbsolutePath); Authorization=request.Headers.Authorization?.ToString();
            if (status == HttpStatusCode.OK && request.RequestUri.AbsolutePath.EndsWith("/admin/projects/0-7", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent($$"""{"id":"0-7","shortName":"{{project}}"}""") });
            }
            Uri=request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
