using System.Net;
using Atlas.Server.Tests.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>GET /api/auth/csrf (3.6 QA F9): the token stays uncached and the /api no-store header no longer makes antiforgery log its "overridden" warning on every call.</summary>
public sealed class CsrfEndpointTests
{
    [PostgreSqlFact]
    public async Task The_csrf_token_is_not_cached_and_logs_no_warning()
    {
        var logs = new CaptureLoggerProvider();
        await using var f = await McpSite.CreateAsync(services: services => services.AddSingleton<ILoggerProvider>(logs));

        var response = await f.Admin.GetAsync("/api/auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning && entry.Category.Contains("Antiforgery") || entry.Message.Contains("Cache-Control and Pragma"));
    }
}
