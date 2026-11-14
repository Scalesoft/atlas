using Atlas.Server.Platform.Live;

namespace Atlas.Server.Platform.Logs;

/// <summary>
/// The server's own log on /hubs/live (Správa wiki → Systémový log, „Živě“): <c>logs:{Guid.Empty}</c>, for effective wiki administrators
/// only, as /api/admin/logs. <see cref="LogsWritten"/> carries no line: it says that lines up to a level were stored, and the viewer reads
/// what its filter shows over HTTP. Sent at most every <see cref="LiveThrottle.Interval"/>.
/// </summary>
public sealed class LogLiveChannel(AtlasDatabase database) : WikiAdminFeedChannel(database)
{
    public const string Prefix = "logs";

    public const string Event = "LogsWritten";

    public static readonly string Name = NameOf(Prefix);

    public override string Kind => Prefix;
}

/// <summary>Server → client on <c>logs:</c>: lines were stored, the most severe of them at <paramref name="Level"/> (lowercase, as the
/// log API names levels), so a viewer filtered to errors skips a batch of information.</summary>
public sealed record LogsWritten(string Level);
