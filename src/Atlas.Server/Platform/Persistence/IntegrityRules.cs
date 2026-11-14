using NHibernate.Event;
using NHibernate.Persister.Entity;

namespace Atlas.Server.Platform.Persistence;

/// <summary>
/// The integrity rules of the rows the application only appends to (audit facts, access history) or never deletes, held for every
/// write of an NHibernate session: an update or a delete that breaks one throws, so the transaction rolls back. Database triggers held
/// them until 3.6 (no logic in the database, migration 2026110945) and the messages are theirs. Native SQL cannot pass here:
/// IntegrityRulesConventionTests holds it to the same rules, IntegrityRulesTests proves each rule.
/// </summary>
public sealed class IntegrityRules : IPreUpdateEventListener, IPreDeleteEventListener
{
    public const string AuditAppendOnly = "Access audit is append-only";
    public const string HistoryImmutable = "Access history is immutable; close the current interval and insert a new row";
    public const string HistoryNotDeleted = "Access history cannot be deleted";

    /// <summary>The refusal of a delete (null: a delete is allowed) and of an update that breaks the rule (null: allowed).</summary>
    internal sealed record Rule(string? Delete, Func<Row, string?> Update);

    /// <summary>The old and new state of one updated row.</summary>
    internal sealed class Row(IEntityPersister persister, object[] old, object[] state)
    {
        public bool Changed(string property)
        {
            var index = Array.IndexOf(persister.PropertyNames, property);
            return !persister.PropertyTypes[index].IsEqual(old[index], state[index]);
        }

        public bool ChangedExcept(string property) => persister.PropertyNames.Any(name => name != property && Changed(name));

        public object? Old(string property) => old[Array.IndexOf(persister.PropertyNames, property)];

        public object? New(string property) => state[Array.IndexOf(persister.PropertyNames, property)];
    }

    private static readonly Rule Audit = new(AuditAppendOnly, _ => AuditAppendOnly);

    /// <summary>An interval of access history: only its ActiveTo may change, once, from NULL (closing it).</summary>
    private static readonly Rule History = new(HistoryNotDeleted,
        row => row.ChangedExcept("ActiveTo") || (row.Old("ActiveTo") is not null && row.Changed("ActiveTo")) ? HistoryImmutable : null);

    /// <summary>By table, as the triggers named them. Tables only native SQL writes are in IntegrityRulesConventionTests.</summary>
    internal static readonly IReadOnlyDictionary<string, Rule> Tables = new Dictionary<string, Rule>(StringComparer.Ordinal)
    {
        ["PageAccessEvent"] = Audit,
        ["GroupMember"] = History,
        ["SpaceGrant"] = History,
        ["ResourceGrant"] = History,
        ["UserApiPermission"] = History,
        ["ProtectionSetting"] = History,
        ["HubConnectionRevision"] = History,
        ["McpSetting"] = History,
        ["GroupAiPolicy"] = History,
        ["UserAiPolicy"] = History,
        ["AiProfileRevision"] = new Rule("AI profile revisions are immutable; insert a new revision and move the assignment",
            _ => "AI profile revisions are immutable; insert a new revision and move the assignment"),
        // Pending, then decided once (approved, rejected or cancelled); the request itself never changes.
        ["VaultAccessRequest"] = new Rule("Access requests cannot be deleted", row =>
            (string?)row.Old("State") != "pending" || (string?)row.New("State") == "pending"
            || new[] { "VaultId", "SecretId", "VaultSpaceId", "RequesterId", "Level", "Reason", "CreatedAt" }.Any(row.Changed)
                ? "An access request is decided once and its request is immutable" : null),
        ["SecurityConfiguration"] = new Rule("The security configuration row cannot be deleted", row =>
            !row.Changed("Version") ? row.Changed("ProxyHopLimit") ? "A security configuration change must increment Version" : null
            : (long)row.New("Version")! != (long)row.Old("Version")! + 1 ? "Security configuration versions increase by one" : null),
        ["TrustedAddress"] = new Rule("Trusted addresses are archived, not deleted", row =>
            row.Changed("Purpose") || row.Old("ArchivedAt") is not null ? "A trusted address keeps its purpose and an archived one stays archived" : null),
        ["HubGroupBinding"] = new Rule("Disable a Hub binding; deleting it would turn external membership into local membership", row =>
            row.Changed("ConnectionId") || row.Changed("ExternalGroupId") ? "A Hub binding cannot change identity" : null),
        ["HubIdentityState"] = new Rule(null, row =>
            (long)row.New("Generation")! < (long)row.Old("Generation")! || row.Changed("UserId") || row.Changed("ConnectionId") || row.Changed("ExternalUserId")
                ? "Hub identity is stable and generation cannot decrease" : null),
    };

    /// <summary>The mapped tables these rules hold (IntegrityRulesConventionTests: every guarded table with a mapping is here).</summary>
    public static IReadOnlyCollection<string> GuardedTables => [.. Tables.Keys];

    public bool OnPreUpdate(PreUpdateEvent @event)
    {
        if (@event.Entity is UserAccount account && !AccountHistory.IsRecording(@event.Session, account.Id))
        {
            throw new InvalidOperationException(AccountHistory.FlushedWithoutHistory);
        }
        // An update NHibernate cannot compare (a detached entity without its old state) is refused: the rule cannot be checked.
        if (RuleOf(@event.Persister) is { } rule
            && (@event.OldState is null ? "The rows of this table change only as loaded entities" : rule.Update(new Row(@event.Persister, @event.OldState, @event.State)))
                is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }
        return false;
    }

    public bool OnPreDelete(PreDeleteEvent @event)
    {
        if (RuleOf(@event.Persister) is { Delete: { } refusal })
        {
            throw new InvalidOperationException(refusal);
        }
        return false;
    }

    public Task<bool> OnPreUpdateAsync(PreUpdateEvent @event, CancellationToken cancellationToken) => Task.FromResult(OnPreUpdate(@event));

    public Task<bool> OnPreDeleteAsync(PreDeleteEvent @event, CancellationToken cancellationToken) => Task.FromResult(OnPreDelete(@event));

    private static Rule? RuleOf(IEntityPersister persister)
        => persister is AbstractEntityPersister mapped && Tables.TryGetValue(mapped.TableName.Trim('"', '`'), out var rule) ? rule : null;
}
