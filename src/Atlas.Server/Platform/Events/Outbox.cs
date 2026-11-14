using Atlas.Server.Platform.Modules;
using FluentMigrator;
using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Atlas.Server.Platform.Events;

[Migration(2026102922, "The durable outbox and ContentRevised of pages")]
public sealed class OutboxMigration() : EmbeddedSchemaMigration("2026102922_outbox.sql");

[Migration(2026102995, "Outbox delivery per consumer; the board sync log trimmed to what can be read back")]
public sealed class OutboxConsumersMigration() : EmbeddedSchemaMigration("2026102995_outbox_consumers_retention.sql");

[Migration(2026103020, "The pending outbox rows by type, for the fan-out before every batch")]
public sealed class OutboxPendingTypeMigration() : EmbeddedSchemaMigration("2026103020_outbox_pending_type.sql");

[Migration(2026110420, "Revisions are announced by the code that writes them; the outbox triggers of pages and contacts go")]
public sealed class RevisionTriggersMigration() : EmbeddedSchemaMigration("2026110420_revision_triggers.sql");

[Migration(2026110421, "The outbox on Wolverine's durable queue: undelivered rows go to the upgrade job, the table goes")]
public sealed class OutboxOnWolverineMigration() : EmbeddedSchemaMigration("2026110421_outbox_on_wolverine.sql");

/// <summary>
/// The durable outbox of the current message (Atlas 3 plan §10, 3.3 plan §4.8). A handler calls <see cref="Enqueue"/>; right before the
/// commit (NHibernateTransactionMiddleware) the messages are sent to Wolverine's durable local queue <see cref="Queue"/> through the
/// handler's own transaction (NHibernateEnvelopeTransaction), so a rollback leaves nothing, and Wolverine delivers them after the commit.
/// Delivery is at least once: a consumer is an ordinary, idempotent Wolverine handler and tells a repeat by its envelope's id.
/// </summary>
/// <remarks>
/// An event with several consumers (ContentRevised) is delivered to each on its own: every consumer declares its delivery type
/// (<see cref="IOutboxDelivery{TEvent}"/>), and the outbox sends one delivery per consumer that takes the event, each with its own
/// attempts (<see cref="MaxAttempts"/>, then a Wolverine dead letter). A disabled module has no handler, so it is sent nothing: what it
/// missed is rebuilt from the current state, not replayed. Messages of one ordering key share a partition of the queue, so they run one at
/// a time in the order they were sent, but a retry waits apart and lets the later ones pass: a consumer rebuilds from the current state, so
/// an older message never undoes a newer one. A server without durable storage keeps the queue in memory; a test host without it has no
/// queue and sends nothing.
/// </remarks>
public sealed class Outbox(OutboxConsumers consumers)
{
    /// <summary>The durable local queue of the outbox's messages (<see cref="OutboxQueue.UseAtlasOutbox"/>).</summary>
    public const string Queue = "outbox";

    /// <summary>Wolverine's name of the queue in its envelope tables (<c>received_at</c>).</summary>
    public const string QueueUri = "local://outbox/";

    /// <summary>The module of messages declared outside every module: always enabled.</summary>
    public const string PlatformModule = "platform";

    /// <summary>The attempts of a delivery before it is a dead letter; between them 10 s, 20 s, 40 s … about 21 minutes in all.</summary>
    public const int MaxAttempts = 8;

    public static readonly TimeSpan[] Backoff = [.. Enumerable.Range(0, MaxAttempts - 1).Select(retry => TimeSpan.FromSeconds(10 << retry))];

    private List<(object Message, string? Key, TimeSpan? Delay)>? m_pending;
    private IMessageContext? m_messages;

    /// <summary>Sends <paramref name="message"/> with this message's transaction. Only inside a Wolverine handler or a job's step.</summary>
    /// <param name="orderingKey">Messages of one key run one at a time, in order (usually the resource id); null for no order.</param>
    /// <param name="delay">Delivered no sooner than this after the commit (a durable scheduled envelope).</param>
    public void Enqueue(object message, string? orderingKey = null, TimeSpan? delay = null)
    {
        (m_pending ??= []).Add((message, orderingKey, delay));
    }

    /// <summary>Whether a consumer of this host takes <typeparamref name="TEvent"/>: an event that nobody consumes yet, or whose consumer's
    /// module is disabled, need not be raised nor its timers scheduled (and <see cref="Enqueue"/> refuses an event without consumers).</summary>
    public bool IsConsumed<TEvent>() => consumers.HasQueue && consumers.Consumes(typeof(TEvent));

    /// <summary>The stored type name: full name and assembly, without a version.</summary>
    public static string TypeName(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

    /// <summary>The module whose namespace declares <paramref name="type"/>, by the rule that keeps a disabled module's handlers out.</summary>
    public static string ModuleOf(Type type)
    {
        var ns = type.Namespace ?? "";
        return AtlasModules.All.FirstOrDefault(module => module.GetType().Namespace is { } root
            && (ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal)))?.Descriptor.Id ?? PlatformModule;
    }

    /// <summary>The message context the messages go through: the handler's, enlisted in its transaction.</summary>
    public void Begin(IMessageContext messages)
    {
        m_pending = null;
        m_messages = messages;
    }

    /// <summary>Right before the commit: the enqueued messages, as envelopes in the handler's transaction.</summary>
    internal async Task WriteAsync()
    {
        if (m_pending is not { } pending)
        {
            return;
        }
        m_pending = null;
        var messages = m_messages ?? throw new InvalidOperationException("The outbox sends only inside a handler's transaction.");
        if (!consumers.HasQueue)
        {
            return;
        }
        var queue = messages.EndpointFor(Queue);
        foreach (var (message, key, delay) in pending)
        {
            foreach (var delivery in consumers.Deliveries(message))
            {
                await queue.SendAsync(delivery, new DeliveryOptions { GroupId = key, ScheduleDelay = delay });
            }
        }
    }
}

/// <summary>A message the outbox sends as it is, on its queue with its retries and dead letters: a consumer's delivery
/// (<see cref="IOutboxDelivery{TEvent}"/>) or a message of one consumer (NotificationRequested).</summary>
public interface IOutboxMessage;

/// <summary>
/// One consumer's own delivery of the outbox event <typeparamref name="TEvent"/>: a record in the consumer's namespace (so its module)
/// with one Wolverine handler and a constructor that takes the event. The outbox sends one per event the consumer
/// <see cref="Takes"/>.
/// </summary>
public interface IOutboxDelivery<TEvent> : IOutboxMessage
{
    TEvent Event { get; }

    /// <summary>Whether the consumer takes <paramref name="message"/>: it is sent no delivery of the others (the mention notices take
    /// publications only). A consumer checks what it takes again when handling, as a safeguard.</summary>
    static abstract bool Takes(TEvent message);
}

/// <summary>
/// The consumers of the outbox events: every <see cref="IOutboxDelivery{TEvent}"/> that a handler of this host takes, so a disabled
/// module's are left out and a test host's probes are in.
/// </summary>
public sealed class OutboxConsumers(IWolverineRuntime runtime)
{
    private readonly Lazy<ILookup<Type, Func<object, object?>>> m_deliveries = new(() => ((WolverineRuntime)runtime).Handlers.Chains
        .Select(chain => chain.MessageType).Distinct()
        .Select(type => (Type: type, Event: type.GetInterfaces()
            .FirstOrDefault(face => face.IsGenericType && face.GetGenericTypeDefinition() == typeof(IOutboxDelivery<>))?.GetGenericArguments()[0]))
        .Where(item => item.Event is not null)
        .OrderBy(item => item.Type.FullName, StringComparer.Ordinal)
        .ToLookup(item => item.Event!, item => Delivery(item.Type, item.Event!)));

    /// <summary>Whether this host runs the outbox's queue (<see cref="OutboxQueue.UseAtlasOutbox"/>); a test host without it sends nothing.</summary>
    public bool HasQueue => m_queue.Value;

    private readonly Lazy<bool> m_queue = new(() => runtime.Options.Transports.AllEndpoints().Any(endpoint => endpoint.EndpointName == Outbox.Queue));

    /// <summary>Whether some consumer of this host takes events of <paramref name="eventType"/>.</summary>
    public bool Consumes(Type eventType) => m_deliveries.Value.Contains(eventType);

    /// <summary>What the outbox sends for <paramref name="message"/>: an outbox message itself, an event a delivery for each consumer
    /// that takes it.</summary>
    public IEnumerable<object> Deliveries(object message)
    {
        if (message is IOutboxMessage)
        {
            return [message];
        }
        var deliveries = m_deliveries.Value;
        if (!deliveries.Contains(message.GetType()))
        {
            throw new InvalidOperationException($"{message.GetType()} is neither an outbox message nor an event with consumers.");
        }
        return deliveries[message.GetType()].Select(delivery => delivery(message)).OfType<object>();
    }

    private static Func<object, object?> Delivery(Type type, Type @event)
    {
        var takes = type.GetMethod("Takes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, [@event])
            ?? throw new InvalidOperationException($"{type} declares no public static Takes({@event.Name}).");
        var create = type.GetConstructor([@event]) ?? throw new InvalidOperationException($"{type} has no constructor that takes {@event.Name}.");
        return message => (bool)takes.Invoke(null, [message])! ? create.Invoke([message]) : null;
    }
}

/// <summary>Wolverine's side of the outbox, next to the durable storage (JobQueue.UseAtlasDurability).</summary>
public static class OutboxQueue
{
    /// <param name="durable">In Wolverine's durable storage (JobQueue.UseAtlasDurability); without it in memory, lost with the process.</param>
    /// <param name="retries">The waits between attempts; <see cref="Outbox.Backoff"/> unless a test host shortens them.</param>
    public static WolverineOptions UseAtlasOutbox(this WolverineOptions options, bool durable = true, TimeSpan[]? retries = null)
    {
        var queue = options.LocalQueue(Outbox.Queue);
        if (durable)
        {
            queue.UseDurableInbox();
        }
        // Five partitions by ordering key: one key's messages one at a time, at most five at once, so a burst cannot drain the pool.
        queue.PartitionProcessingByGroupId(PartitionSlots.Five);
        options.Policies.Add(new Retries(retries ?? Outbox.Backoff));
        return options;
    }

    /// <summary>A failed outbox message waits apart and comes again (its partition goes on), until a dead letter after the last wait.</summary>
    private sealed class Retries(TimeSpan[] waits) : IHandlerPolicy
    {
        public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
        {
            foreach (var chain in chains.Where(chain => typeof(IOutboxMessage).IsAssignableFrom(chain.MessageType)))
            {
                chain.OnException<Exception>().ScheduleRetry(waits);
            }
        }
    }
}
