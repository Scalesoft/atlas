using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Atlas.Server.Features.Hub.Directory;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubDirectoryClientTests
{
    private static readonly Uri BaseUrl = new("https://hub.example.test/hub");
    private const string UserId = "10000000-0000-4000-8000-000000000002";
    private const string Token = "synthetic-test-token";
    private static string Permissions => CapturedBody("hub-reader-permissions.json");

    [Fact]
    public async Task Reads_complete_nested_membership_in_one_snapshot_after_permission_check()
    {
        using var transport = new FakeTransport(Json(Permissions), Json(Fixture("active-user")));
        using var http = new HttpClient(transport);
        var result = await new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(BaseUrl, Token, UserId);

        Assert.Equal(UserId, result.ExternalUserId);
        Assert.Equal(HubDirectoryAccountState.Active, result.State);
        Assert.Equal(5, result.Groups.Length);
        Assert.Contains(result.Groups, group => group.Name == "Atlas Contract Parent");
        Assert.Contains(result.Groups, group => group.Name == "Atlas Contract Child");
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal("https://hub.example.test/hub/api/rest/users/me?fields=id%2Cbanned%2Cguest%2CtransitiveProjectRoles%28project%28id%2Cglobal%29%2Crole%28permissions%28key%29%29%29", transport.Requests[0]);
        Assert.Equal($"https://hub.example.test/hub/api/rest/users/{UserId}?fields=id%2Cbanned%2Cguest%2CeraseTimestamp%2CtransitiveGroups%28id%2Cname%29", transport.Requests[1]);
    }

    [Theory]
    [InlineData("banned-user", HubDirectoryAccountState.Disabled, 5)]
    [InlineData("nested-membership-revoked", HubDirectoryAccountState.Active, 3)]
    public async Task Reports_complete_account_and_membership_changes(string fixture, HubDirectoryAccountState state, int groups)
    {
        using var http = new HttpClient(new FakeTransport(Json(Permissions), Json(Fixture(fixture))));
        var result = await new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(BaseUrl, Token, UserId);
        Assert.Equal(state, result.State);
        Assert.Equal(groups, result.Groups.Length);
    }

    [Theory]
    [InlineData("{}", "invalid-response")]
    [InlineData("{", "invalid-response")]
    [InlineData("{\"id\":\"wrong\",\"banned\":false,\"guest\":false,\"transitiveGroups\":[]}", "identity-mismatch")]
    [InlineData("{\"id\":\"person\",\"id\":\"person\",\"banned\":false,\"guest\":false,\"transitiveGroups\":[]}", "invalid-response")]
    [InlineData("{\"id\":\"person\",\"banned\":false,\"guest\":false}", "invalid-response")]
    [InlineData("{\"id\":\"person\",\"banned\":false,\"guest\":false,\"eraseTimestamp\":null,\"transitiveGroups\":[]}", "invalid-response")]
    [InlineData("{\"id\":\"person\",\"banned\":false,\"guest\":false,\"transitiveGroups\":[{\"id\":\"g\",\"name\":\"a\"},{\"id\":\"g\",\"name\":\"b\"}]}", "invalid-response")]
    [InlineData("{\"id\":\"person\",\"banned\":false,\"guest\":false,\"transitiveGroups\":[{\"id\":\"g\",\"name\":\"a\",\"name\":\"b\"}]}", "invalid-response")]
    public async Task Never_turns_invalid_snapshot_into_success(string body, string code)
    {
        using var http = new HttpClient(new FakeTransport(Json(Permissions), Json(body)));
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(BaseUrl, Token, "person"));
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("\"global\": true", "\"global\": false")]
    [InlineData("jetbrains.jetpass.group-read", "unrelated-permission")]
    [InlineData("\"banned\": false", "\"banned\": true")]
    public async Task Requires_current_global_visibility_before_reading_membership(string before, string after)
    {
        using var transport = new FakeTransport(Json(Permissions.Replace(before, after, StringComparison.Ordinal)));
        using var http = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(BaseUrl, Token, UserId));
        Assert.Equal("insufficient-permissions", error.Code);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(401, "unauthorized")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "not-visible")]
    [InlineData(429, "rate-limited")]
    [InlineData(503, "unavailable")]
    [InlineData(302, "invalid-response")]
    public async Task Classifies_failure_without_exposing_body_or_following_redirect(int status, string code)
    {
        using var response = Json(Token, (HttpStatusCode)status);
        response.Headers.Location = new Uri("https://attacker.test/");
        response.Headers.TryAddWithoutValidation("Retry-After", "99999");
        using var transport = new FakeTransport(Json(Permissions), response);
        using var http = new HttpClient(transport);
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(BaseUrl, Token, UserId));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(Token, error.ToString());
        Assert.Null(error.InnerException);
        if (status is 429 or 503) Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejects_oversized_body_even_without_content_length(bool knownLength)
    {
        var bytes = new byte[4 * 1024 * 1024 + 1];
        Array.Fill(bytes, (byte)' ');
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new UnseekableStream(bytes))
        };
        using var http = new HttpClient(new FakeTransport(Json(Permissions), response));
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(BaseUrl, Token, UserId));
        Assert.Equal("response-too-large", error.Code);
    }

    [Fact]
    public async Task Encodes_opaque_identifiers_and_search_without_following_upstream_next()
    {
        using var transport = new FakeTransport(Json(Permissions), Json("""{"id":"person/a?b#c","banned":false,"guest":false,"eraseTimestamp":1,"transitiveGroups":[]}"""),
            Json(Permissions), Json(CapturedBody("hub-group-search.json")), Json(Permissions), Json("""{"id":"group/a?b#c","name":"Parent"}"""));
        using var http = new HttpClient(transport);
        var client = new HubDirectoryClient(http, TimeProvider.System);
        var user = await client.ReadUserAsync(BaseUrl, Token, "person/a?b#c");
        var search = await client.SearchGroupsAsync(BaseUrl, Token, "Atlas & Contract");
        var group = await client.ReadGroupAsync(BaseUrl, Token, "group/a?b#c");
        Assert.Equal(HubDirectoryAccountState.Disabled, user.State);
        Assert.Equal("group/a?b#c", group.Id);
        Assert.Equal(2, search.Groups.Length);
        Assert.True(search.HasMore);
        Assert.Contains("/users/person%2Fa%3Fb%23c?", transport.Requests[1]);
        Assert.EndsWith("usergroups?fields=id%2Cname&%24top=50&query=Atlas%20%26%20Contract", transport.Requests[3]);
        Assert.Contains("/usergroups/group%2Fa%3Fb%23c?", transport.Requests[5]);
        Assert.Equal(6, transport.Requests.Count);
    }

    [Theory]
    [InlineData("http://hub.example.test/hub", "person")]
    [InlineData("https://user@hub.example.test/hub", "person")]
    [InlineData("https://hub.example.test/hub?other", "person")]
    [InlineData("https://hub.example.test/hub#fragment", "person")]
    [InlineData("https://hub.example.test/hub", "me")]
    [InlineData("https://hub.example.test/hub", "\n")]
    public async Task Rejects_invalid_destination_or_reserved_identity_before_sending_token(string url, string id)
    {
        using var transport = new FakeTransport();
        using var http = new HttpClient(transport);
        await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(new Uri(url), Token, id));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Rejects_malformed_discovery_page_instead_of_presenting_a_partial_success()
    {
        using var http = new HttpClient(new FakeTransport(Json(Permissions), Json("""{"skip":"wrong","top":50,"usergroups":[]}""")));
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, TimeProvider.System).SearchGroupsAsync(BaseUrl, Token, ""));
        Assert.Equal("invalid-response", error.Code);
    }

    [Theory]
    [InlineData("count", "response-too-large")]
    [InlineData("depth", "invalid-response")]
    public async Task Bounds_membership_count_and_json_depth(string limit, string code)
    {
        var groups = limit == "count" ? string.Join(',', Enumerable.Range(0, 10001).Select(i => $$"""{"id":"{{i}}","name":"Group"}""")) : "";
        var unknown = limit == "depth" ? new string('[', 33) + "0" + new string(']', 33) : "null";
        var body = $$"""{"id":"person","banned":false,"guest":false,"transitiveGroups":[{{groups}}],"unknown":{{unknown}}}""";
        using var http = new HttpClient(new FakeTransport(Json(Permissions), Json(body)));
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, TimeProvider.System).ReadUserAsync(BaseUrl, Token, "person"));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Deadline_covers_permission_check_and_snapshot_together_while_caller_cancellation_propagates()
    {
        var clock = new TestClock();
        var calls = 0;
        using var transport = new CallbackTransport(async cancellationToken =>
        {
            if (++calls == 1)
            {
                clock.Advance(TimeSpan.FromSeconds(19));
                return Json(Permissions);
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json("{}");
        });
        using var http = new HttpClient(transport);
        var pending = new HubDirectoryClient(http, clock).ReadUserAsync(BaseUrl, Token, UserId);
        clock.Advance(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => pending);
        Assert.Equal("timeout", error.Code);
        using var cancellation = new CancellationTokenSource();
        var cancelled = new HubDirectoryClient(http, clock).ReadUserAsync(BaseUrl, Token, UserId, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Theory]
    [InlineData("Thu, 01 Oct 2026 12:00:30 GMT", 30)]
    [InlineData("Thu, 01 Oct 2026 11:59:00 GMT", 1)]
    public async Task Uses_injected_clock_for_retry_date_and_clamps_expired_date(string date, int expectedSeconds)
    {
        using var response = Json("", HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", date);
        using var http = new HttpClient(new FakeTransport(response));
        var error = await Assert.ThrowsAsync<HubDirectoryException>(() => new HubDirectoryClient(http, new TestClock()).ReadUserAsync(BaseUrl, Token, UserId));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), error.RetryAfter);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static string Fixture(string name, [CallerFilePath] string source = "")
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "hub-2026.2-contract.json")));
        return fixture.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("body").GetRawText();
    }

    private static string CapturedBody(string file, [CallerFilePath] string source = "")
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", file)));
        return fixture.RootElement.GetProperty("body").GetRawText();
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class CallbackTransport(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        private readonly List<TestTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(callback, state, now + dueTime);
            timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            now += duration;
            foreach (var timer in timers.Where(timer => timer.Due <= now).ToArray()) timer.Fire();
        }
        private sealed class TestTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = due;
            public void Fire() { Dispose(); callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Due = DateTimeOffset.MaxValue;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeTransport(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("hub.example.test", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            Requests.Add(request.RequestUri.AbsoluteUri);
            return Task.FromResult(responses[Requests.Count - 1]);
        }
    }
}
