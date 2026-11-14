using NHibernate;
using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Queries;

/// <summary>
/// The admin's view of AI (AI-25): the stored configuration read now (not the cache, so it matches the sections' versions),
/// each kind's state from the circuit breaker, the index tile (V2) and a week of <c>AiUsage</c>. A kind whose current revision
/// the breaker has not seen yet (saved a moment ago) is <c>Unknown</c>, as it is for capabilities.
/// </summary>
public sealed class GetAiOverviewHandler(ISession session, ModuleSettingsAccess access, AiAvailability availability, IAiIndexOverview index,
    TimeProvider clock)
{
    public static readonly TimeSpan UsageWindow = TimeSpan.FromDays(7);

    public async Task<AiOverview> Handle(GetAiOverview query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var master = await CurrentAiSettings.LoadSwitchAsync(session, ct);
        var enabled = master?.Enabled ?? false;
        var profiles = new List<AiProfileStatus>();
        var revisions = new Dictionary<string, AiProfileRevision?>();
        foreach (var kind in AiProfileKind.All)
        {
            var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, kind, ct);
            revisions[kind] = assignment is null ? null : await session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        }
        foreach (var kind in AiProfileKind.All)
        {
            var revision = revisions[kind];
            if (kind == AiProfileKind.Quick && revision is { UseChatProfile: true })
            {
                var shared = Status(AiProfileKind.Chat, revisions[AiProfileKind.Chat], enabled && revision.Enabled);
                profiles.Add(shared with
                {
                    Kind = kind,
                    State = !enabled || !revision.Enabled ? nameof(AiProfileState.Disabled) : shared.State,
                    UseChatProfile = true,
                });
            }
            else
            {
                profiles.Add(Status(kind, revision, enabled));
            }
        }
        return new AiOverview(enabled, CurrentAiSettings.ToView(master).Version, profiles, await index.ReadAsync(ct), await UsageAsync(ct));
    }

    private AiProfileStatus Status(string kind, AiProfileRevision? revision, bool enabled)
    {
        if (revision is null)
        {
            return new AiProfileStatus(kind, nameof(AiProfileState.NotConfigured), null, null, null, null, null);
        }
        if (!enabled || !revision.Enabled)
        {
            return new AiProfileStatus(kind, nameof(AiProfileState.Disabled), null, null, null, revision.Model, revision.Location);
        }
        var health = availability.Health(kind);
        return health.RevisionId == revision.Id && health.State is AiProfileState.Unknown or AiProfileState.Available or AiProfileState.Unavailable
            ? new AiProfileStatus(kind, health.State.ToString(), health.Since, health.LastErrorCode, health.LastSuccessAt, revision.Model, revision.Location,
                Warning: health.ServedModel is { } served ? AiProfileStatus.ModelMismatch(revision.Model, served) : null)
            :new AiProfileStatus(kind, nameof(AiProfileState.Unknown), null, null, null, revision.Model, revision.Location);
    }

    /// <summary>Per revision, because each revision has its own prices; the estimate sums what is known.</summary>
    private async Task<AiUsageSummary> UsageAsync(CancellationToken ct)
    {
        var rows = await session.CreateSQLQuery("""
                SELECT count(*) AS calls,
                       count(*) FILTER (WHERE u."Outcome" NOT IN ('ok', 'canceled')) AS errors,
                       sum(u."InputTokens") AS input_tokens,
                       sum(u."OutputTokens") AS output_tokens,
                       r."InputPricePerMillion" AS input_price,
                       r."OutputPricePerMillion" AS output_price
                FROM "AiUsage" u
                JOIN "AiProfileRevision" r ON r."Id" = u."ProfileRevisionId"
                WHERE u."OccurredAt" >= :since
                GROUP BY u."ProfileRevisionId", r."InputPricePerMillion", r."OutputPricePerMillion"
                """)
            .AddScalar("calls", NHibernateUtil.Int64)
            .AddScalar("errors", NHibernateUtil.Int64)
            .AddScalar("input_tokens", NHibernateUtil.Decimal)
            .AddScalar("output_tokens", NHibernateUtil.Decimal)
            .AddScalar("input_price", NHibernateUtil.Decimal)
            .AddScalar("output_price", NHibernateUtil.Decimal)
            .SetParameter("since", clock.GetUtcNow().UtcDateTime - UsageWindow, NHibernateUtil.UtcDateTime)
            .ListAsync<object[]>(ct);
        long calls = 0, errors = 0;
        long? input = null, output = null;
        decimal? cost = null;
        foreach (var row in rows)
        {
            calls += (long)row[0];
            errors += (long)row[1];
            var rowInput = row[2] is decimal i ? (long?)i : null;
            var rowOutput = row[3] is decimal o ? (long?)o : null;
            input = rowInput is null ? input : (input ?? 0) + rowInput;
            output = rowOutput is null ? output : (output ?? 0) + rowOutput;
            if (AiCostEstimate.Estimate(rowInput, rowOutput, row[4] as decimal?, row[5] as decimal?) is { } estimate)
            {
                cost = (cost ?? 0) + estimate;
            }
        }
        return new AiUsageSummary((int)Math.Min(calls, int.MaxValue), (int)Math.Min(errors, int.MaxValue), input, output, cost);
    }
}
