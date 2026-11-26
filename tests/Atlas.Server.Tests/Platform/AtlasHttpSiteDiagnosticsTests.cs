using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Sdk;

namespace Atlas.Server.Tests.Platform;

/// <summary>An answer a test did not expect must say why by itself: on CI the failure message is all there is.</summary>
public sealed class AtlasHttpSiteDiagnosticsTests
{
    [Fact]
    public async Task An_unexpected_500_names_the_request_and_carries_the_servers_log_of_it()
    {
        var logs = new CaptureLoggerProvider();
        AtlasHttpSite.WatchLogs(logs);
        logs.CreateLogger("Atlas.Server.Platform.Hosting.RequestErrorHandling")
            .LogError(new InvalidOperationException("boom in the handler"), "Request {TraceId} failed", "trace-4711");
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/projector/pages/1/catalog"),
            Content = new StringContent("""{"title":"Požadavek se nepodařilo dokončit.","status":500,"traceId":"trace-4711"}"""),
        };

        var failure = await Assert.ThrowsAsync<FailException>(() => AtlasHttpSite.Json(response, HttpStatusCode.OK));

        Assert.Contains("Expected 200 OK, got 500 InternalServerError for PUT /api/projector/pages/1/catalog", failure.Message, StringComparison.Ordinal);
        Assert.Contains("boom in the handler", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_answer_without_a_trace_carries_its_body()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"code":"version_conflict"}""") };

        var failure = await Assert.ThrowsAsync<FailException>(() => AtlasHttpSite.Json(response, HttpStatusCode.OK));

        Assert.Contains("got 409 Conflict", failure.Message, StringComparison.Ordinal);
        Assert.Contains("version_conflict", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Server log", failure.Message, StringComparison.Ordinal);
    }
}
