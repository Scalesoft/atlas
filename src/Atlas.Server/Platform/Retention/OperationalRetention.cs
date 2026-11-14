using System.Collections.Concurrent;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Platform.Retention;

/// <summary>What a log is for, which decides what a full one does (docs/architecture.md §5.2).</summary>
public enum LogClass
{
    /// <summary>A feature reads it: aged out and trimmed to its count, never stopped.</summary>
    Required,
    /// <summary>Statistics only: aged out, and its writers stop while it holds its count of rows.</summary>
    Optional,
    /// <summary>An audit security asks for: aged out only, never trimmed by volume nor stopped.</summary>
    Audit,
}

/// <summary>
/// One operational log or journal that grows with use. <see cref="Time"/> ages a row out after <see cref="Days"/> (none: the count
/// alone bounds it), <see cref="Order"/> (default the time) ranks it for the count, which is per <see cref="Partition"/> when one is
/// named. Identifiers are constants of this file, never input.
/// </summary>
public sealed record OperationalLog(string Name, string Table, LogClass Class, string Time, int? Days, long? MaxRows = null,
    string? Partition = null, string? Order = null, string? Where = null);

/// <summary>A configured limit of one log (<c>Retention:Logs:{name}</c>); a missing value keeps the default.</summary>
public sealed class RetentionLimit
{
    public int? Days { get; set; }
    public long? MaxRows { get; set; }
}

/// <summary>The <c>Retention</c> section of the configuration: the limits of <see cref="OperationalRetention.Logs"/> and the job's bounds.</summary>
public sealed class RetentionOptions
{
    /// <summary>Rows one statement deletes, in its own transaction.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Statements one log gets per run; the rest waits for the next hour.</summary>
    public int MaxBatchesPerRun { get; set; } = 100;

    public Dictionary<string, RetentionLimit> Logs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The log with its configured days and count; an audit keeps no count.</summary>
    public OperationalLog Effective(OperationalLog log)
    {
        if (!Logs.TryGetValue(log.Name, out var limit))
        {
            return log;
        }
        return log with { Days = (limit.Days ?? log.Days) is { } days ? Math.Max(1, days) : null, MaxRows = log.Class == LogClass.Audit ? null : limit.MaxRows ?? log.MaxRows };
    }

    public OperationalLog Effective(string name) => Effective(OperationalRetention.Logs.Single(log => log.Name == name));

    public static RetentionOptions From(IConfiguration configuration)
    {
        var options = new RetentionOptions();
        configuration.GetSection("Retention").Bind(options);
        return options;
    }
}

/// <summary>Whether an optional log takes rows: false while it holds its count (<see cref="OperationalRetention"/> decides hourly).</summary>
public sealed class OptionalLogGate
{
    private readonly ConcurrentDictionary<string, bool> m_full = new(StringComparer.Ordinal);

    public bool Accepts(string log) => !m_full.GetValueOrDefault(log);

    internal void Set(string log, bool full) => m_full[log] = full;
}

/// <summary>
/// The one cleanup of every operational log (docs/architecture.md §5.2), the job "retention" hourly: each log loses the rows past its age,
/// then those past its count (the oldest first), a bounded batch per short transaction and a bounded number of batches per run.
/// An optional log that still holds its count afterwards is closed to its writers until a later run finds room.
/// </summary>
public sealed class OperationalRetention(AtlasDatabase database, RetentionOptions options, OptionalLogGate gate) : IJobDefinition
{
    public static readonly IReadOnlyList<OperationalLog> Logs =
    [
        // The board sync log: a client further behind than the count or the age reloads the board (resync_required). The batch writer
        // trims each board at once; this catches boards nobody writes to any more.
        new("BoardOperation", "BoardOperation", LogClass.Required, "AppliedAt", 30, 500, Partition: "BoardId", Order: "Sequence"),
        // The outbox's dead letters in Wolverine's table, kept for the administrator (Správa wiki → Přehled); Wolverine cleans the rest.
        new("OutboxDead", "wolverine_dead_letters", LogClass.Required, "failed_at", 30, 10_000, Where: $"received_at = '{Events.Outbox.QueueUri}'"),
        // The inbox (inbox spec §7): any item a year after its last event, a read one after 90 days, at most 1000 per account.
        new("Notification", "Notification", LogClass.Required, "UpdatedAt", 365, 1000, Partition: "RecipientId"),
        new("NotificationRead", "Notification", LogClass.Required, "UpdatedAt", 90, Where: "\"ReadAt\" IS NOT NULL"),
        // Who marked the projector catalog: audit. (ObjectPurgeEvent is part of the access audit since 2026102975, kept like it: O6.)
        new("ProjectorCatalogEvent", "ProjectorCatalogEvent", LogClass.Audit, "OccurredAt", 1825),
        // The search miss log and what an editor resolved in it (search gaps), and the AI usage report: statistics.
        new("SearchQueryLog", "SearchQueryLog", LogClass.Optional, "OccurredOn", 90, 1_000_000),
        new("SearchGapResolution", "SearchGapResolution", LogClass.Required, "ResolvedAt", 90),
        new("AiUsage", "AiUsage", LogClass.Optional, "OccurredAt", 180, 1_000_000),
        // Page readership (a row per page and reader each minute of reading): Analytics reads up to 366 days back.
        new("PageView", "PageView", LogClass.Required, "ViewedAt", 400),
        // The AI adoption counters (Kvalita AI) of the last 90 days, and the deleted conversations' answers that count towards today's
        // AI limit (ConversationStore.DailyCountAsync reads the current day).
        new("AiInteractionDaily", "AiInteractionDaily", LogClass.Required, "Day", 90),
        new("AiDeletedDailyUsage", "AiDeletedDailyUsage", LogClass.Required, "Day", 1),
        // Per account: the recently visited pages of Knihovna and the sign-in facts (the latest one is the account's last sign-in, so
        // a dormant account keeps it: no age).
        new("PageVisit", "PageVisit", LogClass.Required, "LastVisitedAt", null, 100, Partition: "UserId"),
        new("UserSignIn", "UserSignIn", LogClass.Required, "AuthenticatedAt", null, 100, Partition: "UserId"),
        // Expiring one-time rows, a month after they expire (SEC-T-4): the invitation and reset links of
        // built-in accounts, the server's recovery tokens; the Wiki.js import previews with their reports three months.
        new("BuiltinAccountToken", "BuiltinAccountToken", LogClass.Required, "ExpiresAt", 30),
        new("ServerRecoveryToken", "ServerRecoveryToken", LogClass.Required, "ExpiresAt", 30),
        new("WikiImportBatch", "WikiImportBatch", LogClass.Required, "ExpiresAt", 90),
        // The anonymous thumbs under AI answers, which outlive their conversations (2026100710): statistics, as long as the AI usage.
        new("AiFeedback", "AiFeedback", LogClass.Required, "CreatedAt", 180),
        // The runs of the background jobs (3.3 plan §4.5): the newest 20 finished runs of each job, none older than 30 days; a queued or
        // running run is never touched.
        new("JobRun", "JobRun", LogClass.Required, "FinishedAt", 30, 20, Partition: "JobId", Where: "\"State\" IN ('succeeded', 'failed', 'cancelled')"),
        // The server's own log (Správa wiki → Systémový log, AppLogWriter): read by its viewer, so trimmed and never stopped; "Id" is its
        // order (the BRIN index on the time cannot rank the count).
        new("AppLog", "AppLog", LogClass.Required, "At", 14, 200_000, Order: "Id"),
    ];

    /// <summary>
    /// The operational tables (BackupCoverage.Operational) no log above ages, each with why: kept by the owner's decision or until
    /// the owner decides (§5.2), bounded without an age, or cleaned by their own feature. The backup coverage test holds every
    /// operational table to a log or to this list, so a new one comes with its decision.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KeptForever = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AccessAuditEvent"] = "the access audit, kept for good (owner, O6)",
        ["ApiActivity"] = "referenced by the immutable access audit, ages only with it (O6)",
        ["ObjectPurgeEvent"] = "referenced by the immutable access audit, ages only with it (O6)",
        ["BackupDestinationChange"] = "referenced by the immutable access audit, ages only with it (O6)",
        ["ReauthChallenge"] = "the consumed ones are referenced by the immutable access audit (O6)",
        ["SecretReleaseEvent"] = "an immutable audit of revealed secrets beside the access audit: kept like it until the owner decides an age",
        ["PageAccessEvent"] = "an immutable audit of page access beside the access audit: kept like it until the owner decides an age",
        ["HubChange"] = "an immutable audit of the hub beside the access audit: kept like it until the owner decides an age",
        ["VaultAccessRequest"] = "an immutable record of vault access requests beside the access audit: kept like it until the owner decides an age",
        ["UserAccountRevision"] = "the change history of accounts: audit, referenced by the access audit",
        ["UserGroupRevision"] = "the change history of groups: audit, referenced by the access audit",
        ["SecurityConfigurationRevision"] = "the change history of the security configuration: audit, referenced by the access audit",
        ["TrustedAddressRevision"] = "the change history of trusted addresses: audit, referenced by the access audit",
        ["VaultLocationRevision"] = "the change history of vault locations: audit",
        ["HubGroupBindingRevision"] = "the change history of hub group bindings: audit",
        ["OidcStateToken"] = "cleaned by its own job by state and expiry (OidcTokenCleanup)",
        ["OidcFreshAuthStatus"] = "one row per identity provider",
        ["HubIdentityState"] = "one row per hub identity",
        ["SearchQueryLogKey"] = "one row: the hashing key of the search miss log",
        ["AiConversation"] = "the assistant deletes expired conversations itself (ConversationStore), their messages with them",
        ["AiMessage"] = "deleted with its conversation",
        ["AiToolProposal"] = "deleted with its conversation",
        ["AiSourceDependency"] = "deleted with its message",
        ["AiDuplicateDismissal"] = "one row per pair of pages, deleted with either page",
        ["ApiLevelsReview"] = "a one-time review notice of the security overview",
        ["GitBackupPage"] = "one row per page, deleted with it",
        ["ContactLinkRun"] = "one row per link run an administrator starts",
        ["RequirementConversionUse"] = "one row per approved case kind of the 3.6 requirement conversion (four); the table goes in the next release",
        ["RequirementConversionBackup"] = "one row per revision the 3.6 requirement conversion rewrote, deleted with its page; the table goes in the next release",
        ["Job"] = "one row per registered job: its pause and next time, which the start keeps in step with the code",
        // Wolverine's own message storage (3.3): it expires and deletes these itself; its dead letters age above (OutboxDead).
        ["wolverine_incoming_envelopes"] = "čistí je Wolverine",
        ["wolverine_outgoing_envelopes"] = "čistí je Wolverine",
        ["wolverine_dead_letter_history"] = "čistí je Wolverine",
        ["wolverine_nodes"] = "čistí je Wolverine",
        ["wolverine_node_assignments"] = "čistí je Wolverine",
        ["wolverine_node_records"] = "čistí je Wolverine",
        ["wolverine_control_queue"] = "čistí je Wolverine",
        ["wolverine_agent_restrictions"] = "čistí je Wolverine",
    };

    /// <summary>One pass over every log; returns the rows each lost.</summary>
    public async Task<Dictionary<string, int>> RunAsync(CancellationToken ct)
    {
        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var configured in Logs)
        {
            var current = options.Effective(configured);
            var age = current.Days is null ? 0 : await DeleteAsync(AgeSql(current), current, ct);
            var count = current.MaxRows is { } max && current.Class != LogClass.Audit ? await DeleteAsync(CountSql(current), current, ct, max) : 0;
            deleted[current.Name] = age + count;
            if (current.Class == LogClass.Optional && current.MaxRows is { } limit)
            {
                gate.Set(current.Name, await CountAsync(current, limit, ct) >= limit);
            }
        }
        return deleted;
    }

    public string Id => "retention";

    public string Title => "Údržba provozních logů";

    public string Description => "Smaže provozní logy a žurnály starší než jejich lhůta a nad jejich počet a zavře zaplněný volitelný log jeho zapisovačům.";

    // No run right after the start: the first one is the first occurrence (the optional logs stay open until then).
    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromHours(1));

    public bool RunsWhenQuiet => true;

    async Task IJobDefinition.RunAsync(JobContext context, CancellationToken ct)
    {
        var deleted = await RunAsync(ct);
        foreach (var (name, rows) in deleted.Where(x => x.Value > 0))
        {
            context.Log.LogInformation("Retention removed {Rows} rows of {Log}", rows, name);
        }
        context.Summarize(new { removed = deleted.Values.Sum() });
    }

    private static string Filter(OperationalLog current) => current.Where is { } where ? $"({where}) AND " : "";

    private static string AgeSql(OperationalLog current) => $"""
        DELETE FROM "{current.Table}" WHERE ctid = ANY(ARRAY(
          SELECT ctid FROM "{current.Table}" WHERE {Filter(current)}"{current.Time}" < now() - make_interval(days => :days) LIMIT :batch))
        """;

    /// <summary>The rows past the count, newest first by <see cref="OperationalLog.Order"/>, per partition when there is one.</summary>
    private static string CountSql(OperationalLog current)
    {
        var order = $"\"{current.Order ?? current.Time}\" DESC, ctid DESC";
        var where = current.Where is { } condition ? $"WHERE {condition}" : "";
        var partition = current.Partition is { } column ? $"PARTITION BY \"{column}\" " : "";
        return $"""
            DELETE FROM "{current.Table}" WHERE ctid = ANY(ARRAY(
              SELECT ctid FROM (SELECT ctid, row_number() OVER ({partition}ORDER BY {order}) AS n FROM "{current.Table}" {where}) ranked
              WHERE n > :max LIMIT :batch))
            """;
    }

    private async Task<int> DeleteAsync(string sql, OperationalLog current, CancellationToken ct, long? max = null)
    {
        var total = 0;
        for (var batch = 0; batch < options.MaxBatchesPerRun; batch++)
        {
            using var session = database.OpenSession();
            using var transaction = session.BeginTransaction();
            var query = session.CreateSQLQuery(sql).SetParameter("batch", options.BatchSize);
            query = max is { } limit ? query.SetParameter("max", limit) : query.SetParameter("days", current.Days);
            var deleted = await query.ExecuteUpdateAsync(ct);
            await transaction.CommitAsync(ct);
            total += deleted;
            if (deleted < options.BatchSize)
            {
                break;
            }
        }
        return total;
    }

    /// <summary>The rows of the log, counted only up to <paramref name="limit"/>.</summary>
    private async Task<long> CountAsync(OperationalLog current, long limit, CancellationToken ct)
    {
        using var session = database.OpenSession();
        var where = current.Where is { } condition ? $"WHERE {condition}" : "";
        return Convert.ToInt64(await session.CreateSQLQuery($"""SELECT count(*) FROM (SELECT 1 FROM "{current.Table}" {where} LIMIT :limit) bounded""")
            .SetParameter("limit", limit).UniqueResultAsync(ct));
    }
}
