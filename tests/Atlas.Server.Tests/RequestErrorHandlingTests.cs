using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
namespace Atlas.Server.Tests;

public sealed class RequestErrorHandlingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_cancellation_does_not_log_an_error_or_write_a_500_body(bool brokenStream)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = cancellation.Token };
        var body = new MemoryStream(); context.Response.Body = body;
        var logger = new RecordingLogger();
        await RequestErrorHandling.InvokeAsync(context, () => Task.FromException(brokenStream ? (Exception)new IOException("Client disconnected") : new OperationCanceledException(cancellation.Token)), logger);
        Assert.Equal(499, context.Response.StatusCode); Assert.Empty(logger.Messages); Assert.Equal(0, body.Length);
    }
    [Fact]
    public async Task Kestrel_request_limit_keeps_413_without_reflecting_exception_details()
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services }; context.Response.Body = new MemoryStream();
        var logger = new RecordingLogger();
        await RequestErrorHandling.InvokeAsync(context, () => throw new BadHttpRequestException("secret-multipart-detail", 413), logger);
        Assert.Equal(413, context.Response.StatusCode);
        Assert.DoesNotContain("secret-multipart-detail", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.Empty(logger.Messages);
    }
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Import_errors_are_redacted_and_a_started_download_is_aborted_without_appending_JSON()
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services }; context.Request.Path = "/api/admin/imports/preview";
        var response = new StartedResponse(); var lifetime = new Lifetime();
        context.Features.Set<IHttpResponseFeature>(response); context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var logger = new RecordingLogger();
        await RequestErrorHandling.InvokeAsync(context, () => throw new IOException("private-import-content"), logger);
        Assert.True(lifetime.Aborted); Assert.Equal(0, response.Body.Length);
        Assert.DoesNotContain("private-import-content", Assert.Single(logger.Messages));
    }
    [Theory]
    [InlineData("/api/vaults/7a1f/access")]
    [InlineData("/api/vault-locations")]
    [InlineData("/api/secrets/7a1f/grants")]
    [InlineData("/api/security/me")]
    [InlineData("/api/setup/complete")]
    [InlineData("/api/admin/backups/test")]
    [InlineData("/api/storage-profiles")]
    [InlineData("/mcp")]
    public async Task Errors_off_the_content_routes_are_logged_without_exception_details(string path)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services }; context.Request.Path = path; context.Response.Body = new MemoryStream();
        var logger = new RecordingLogger();
        await RequestErrorHandling.InvokeAsync(context, () => throw new InvalidOperationException("private-vault-detail"), logger);
        Assert.Equal(500, context.Response.StatusCode);
        Assert.DoesNotContain("private-vault-detail", Assert.Single(logger.Messages));
    }
    /// <summary>SEC-A-8: the full exception is logged only on content routes that carry no credentials; every other API route
    /// (an allow-list, not a deny-list) logs its type and trace id.</summary>
    [Theory]
    [InlineData("/api/pages/7a1f")]
    [InlineData("/api/boards/7a1f")]
    public async Task Errors_on_content_routes_keep_their_diagnostics(string path)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services }; context.Request.Path = path; context.Response.Body = new MemoryStream();
        var logger = new RecordingLogger();
        await RequestErrorHandling.InvokeAsync(context, () => throw new InvalidOperationException("page-diagnostic-detail"), logger);
        Assert.Contains("page-diagnostic-detail", Assert.Single(logger.Messages));
    }
    [Fact]
    public async Task Ai_unavailable_becomes_problem_details_with_the_code()
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        foreach (var failure in new Exception[] { AiGate.Unavailable(), new AiCallException(AiOutcome.Http5xx) })
        {
            var context = new DefaultHttpContext { RequestServices = services }; context.Request.Path = "/api/search"; context.Response.Body = new MemoryStream();
            var logger = new RecordingLogger();
            // The gate's refusal, and a model call that failed mid-request: the same 503 for the client, never a 500.
            await RequestErrorHandling.InvokeAsync(context, () => throw failure, logger);
            Assert.Equal(503, context.Response.StatusCode);
            var problem = JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray()).RootElement;
            Assert.Equal(("ai_unavailable", "Tahle funkce teď není k dispozici."), (problem.GetProperty("code").GetString(), problem.GetProperty("title").GetString()));
            Assert.DoesNotContain(logger.Messages, line => line.Contains("Request", StringComparison.Ordinal));
        }
    }
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + exception?.Message);
    }
    private sealed class StartedResponse : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborted = true;
    }
}


