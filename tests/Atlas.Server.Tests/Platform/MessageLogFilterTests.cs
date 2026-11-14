using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Atlas.Server.Features.Auth.Commands;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class MessageLogFilterTests
{
    /// <summary>
    /// Wolverine logs every failed invocation as Error under a category equal to the message type.
    /// An ApiException is an expected answer, so that category is silenced; the client still gets the
    /// problem body and genuine failures are still logged by RequestErrorHandling with a trace id.
    /// </summary>
    [PostgreSqlFact]
    public async Task An_expected_api_failure_writes_nothing_under_the_message_category()
    {
        var logs = new CaptureLoggerProvider();
        await using var host = await AtlasTestHost.CreateAsync("atlas_message_log_", services => services.AddSingleton<ILoggerProvider>(logs));
        var user = await host.SeedUserAsync("admin");
        var command = new RecordSignIn(user.Id, user.SecurityStamp + "-rotated", "builtin");
        await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<DateTime>(command));
        Assert.DoesNotContain(logs.Entries, entry => entry.Category == typeof(RecordSignIn).FullName);
    }

    /// <summary>
    /// The rule is suffix anchored on purpose: filter categories match by prefix, so a plain full name
    /// would also silence ILogger&lt;RecordSignInHandler&gt;, which handlers use for their own diagnostics.
    /// </summary>
    [PostgreSqlFact]
    public async Task The_filter_silences_messages_without_silencing_handlers_or_platform()
    {
        var logs = new CaptureLoggerProvider();
        await using var host = await AtlasTestHost.CreateAsync("atlas_message_log_scope_", services => services.AddSingleton<ILoggerProvider>(logs));
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        Assert.False(factory.CreateLogger(typeof(RecordSignIn)).IsEnabled(LogLevel.Error));
        Assert.True(factory.CreateLogger(typeof(RecordSignInHandler)).IsEnabled(LogLevel.Warning));
        Assert.True(factory.CreateLogger(typeof(AtlasDatabase)).IsEnabled(LogLevel.Warning));
    }
}
