using System.Text.Json;
using Atlas.Server.Platform.Events.Commands;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Platform.Events.Commands
{
    /// <summary>The Atlas outbox's undelivered rows at the upgrade to Wolverine (<see cref="OutboxUpgradeJob"/>), sent through the outbox.</summary>
    public sealed record SendUpgradedOutboxMessages(IReadOnlyList<UpgradedOutboxMessage> Messages);

    /// <param name="Type">The stored type name (<see cref="Outbox.TypeName"/>).</param>
    /// <param name="Key">The ordering key.</param>
    public sealed record UpgradedOutboxMessage(string Type, JsonElement Payload, string? Key);

    /// <param name="Sent">How many were sent; a type this version no longer has is dropped.</param>
    public sealed record SentUpgradedOutboxMessages(int Sent);

    public sealed class SendUpgradedOutboxMessagesHandler(Outbox outbox, ILogger<SendUpgradedOutboxMessagesHandler> logger)
    {
        public SentUpgradedOutboxMessages Handle(SendUpgradedOutboxMessages command)
        {
            var sent = 0;
            foreach (var item in command.Messages)
            {
                if (Type.GetType(item.Type) is not { } type)
                {
                    logger.LogWarning("An undelivered outbox message of the unknown type {Type} is dropped", item.Type);
                    continue;
                }
                outbox.Enqueue(item.Payload.Deserialize(type, JsonSerializerOptions.Web)!, item.Key);
                sent++;
            }
            return new SentUpgradedOutboxMessages(sent);
        }
    }
}

namespace Atlas.Server.Platform.Events
{
    /// <summary>
    /// Once after the upgrade to the outbox on Wolverine (3.3): migration 2026110421 queues one run whose checkpoint holds every row the
    /// Atlas outbox had not delivered (a dead one too, with fresh attempts), and the run sends them through the outbox. An event goes to
    /// today's consumers, a delivery as it was. Without such rows the migration queues nothing and the job never runs.
    /// </summary>
    public sealed class OutboxUpgradeJob : IJobDefinition
    {
        public const string JobId = "events.outbox-upgrade";

        public string Id => JobId;

        public string Title => "Převod nedoručených událostí";

        public string Description => "Jednou po aktualizaci na verzi 3.3 předá novému doručování události, které původní fronta nestihla doručit.";

        public async Task RunAsync(JobContext context, CancellationToken ct)
        {
            var messages = context.Progress<List<UpgradedOutboxMessage>>() ?? [];
            var sent = (await context.Services.GetRequiredService<IMessageBus>().InvokeAsync<SentUpgradedOutboxMessages>(new SendUpgradedOutboxMessages(messages), ct)).Sent;
            context.Log.LogInformation("Sent {Sent} of {Count} undelivered outbox messages", sent, messages.Count);
            context.Summarize(new { sent, dropped = messages.Count - sent });
        }
    }
}
