using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class RecordSignInLoggingTests
{
    /// <summary>
    /// A failed invocation is reported with the message's ToString(). The security stamp is a session
    /// secret, so it must not reach the log however the runtime decides to report the failure.
    /// AtlasMessageLogging silences the message category, so the assertion no longer relies on the
    /// runtime printing anything.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_failed_sign_in_never_writes_the_security_stamp_to_the_log()
    {
        var logs = new CaptureLoggerProvider();
        await using var host = await AtlasTestHost.CreateAsync("atlas_signin_logging_", services => services.AddSingleton<ILoggerProvider>(logs));
        var user = await host.SeedUserAsync("admin");
        var command = new RecordSignIn(user.Id, user.SecurityStamp + "-rotated", "builtin");
        await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<DateTime>(command));
        // Positive controls, so the redaction assertion cannot pass vacuously: the redacted ToString is the
        // only shape any runtime can print, and AtlasMessageLogging keeps the failure out of the log entirely.
        Assert.Equal("RecordSignIn { sensitive values redacted }", command.ToString());
        Assert.DoesNotContain(user.SecurityStamp, command.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, entry => entry.Category == typeof(RecordSignIn).FullName);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(user.SecurityStamp, StringComparison.Ordinal));
    }
}
