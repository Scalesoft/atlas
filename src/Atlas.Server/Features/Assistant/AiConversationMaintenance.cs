using Atlas.Server.Features.Assistant.Commands;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Features.Assistant;

/// <summary>What one retention round did.</summary>
public sealed record AiConversationMaintenanceResult(int CanceledTurns, int PurgedConversations);

/// <summary>
/// Retention of conversations (plan phase 3 §8, O3): answers left preparing for 10 minutes (a crashed client, a restart) become
/// canceled; unlisted conversations from the omnisearch go 24 hours and listed ones 180 days after their last activity, in batches
/// of 500 conversations, each its own short transaction (<see cref="AiConversationRetention"/>). No content is logged.
/// </summary>
public sealed class AiConversationMaintenanceRunner(IServiceScopeFactory scopes)
{
    public async Task<AiConversationMaintenanceResult> RunOnceAsync(DateTimeOffset now, CancellationToken ct, int batch = AiConversationRetention.PurgeBatch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch);
        var (unlistedBefore, listedBefore, preparedBefore) = AiConversationRetention.CutOffs(now.UtcDateTime);
        var canceled = await InvokeAsync<CanceledAiTurns>(new CancelStaleAiTurns(preparedBefore, now.UtcDateTime), ct);
        var purged = 0;
        while (true)
        {
            var round = await InvokeAsync<PurgedAiConversations>(new PurgeAiConversations(unlistedBefore, listedBefore, batch), ct);
            purged += round.Count;
            // A shorter batch is the last one; conversations locked by a running turn were skipped and wait for the next round.
            if (round.Count < batch)
            {
                break;
            }
        }
        return new AiConversationMaintenanceResult(canceled.Count, purged);
    }

    private async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }
}

/// <summary>The hourly job "assistant.maintenance": <see cref="AiConversationMaintenanceRunner"/> at the clock's now (tests call the runner).</summary>
public sealed class AiConversationMaintenance(AiConversationMaintenanceRunner runner, TimeProvider clock) : IJobDefinition
{
    public string Id => "assistant.maintenance";

    public string Title => "Údržba konverzací asistenta";

    public string Description => "Zruší odpovědi, které zůstaly viset v přípravě, a smaže prošlé konverzace.";

    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromHours(1));

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        try
        {
            var result = await runner.RunOnceAsync(clock.GetUtcNow(), ct);
            context.Summarize(result);
            if (result.CanceledTurns + result.PurgedConversations > 0)
            {
                context.Log.LogInformation("Conversation retention canceled {Canceled} stale answers and purged {Purged} conversations",
                    result.CanceledTurns, result.PurgedConversations);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a database error may quote a parameter.
            throw new JobFailedException($"Conversation retention failed ({exception.GetType().Name})", retry: true);
        }
    }
}
