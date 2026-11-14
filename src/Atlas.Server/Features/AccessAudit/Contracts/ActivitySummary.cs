namespace Atlas.Server.Features.AccessAudit.Contracts;

/// <summary>Activity since <c>From</c> (null: all time); every list is sorted by count descending, then by key or name.</summary>
public sealed record ActivitySummary(DateTime? From, int Revisions, int AiAssistedRevisions, ActivityCount[] RevisionChannels,
    ActivityPerson[] TopEditors, ActivityCount[] PageOpens, int ToolCalls, int ToolFailures, ActivityCount[] ToolChannels,
    ActivityTool[] TopTools, ActivityPerson[] TopToolUsers);

public sealed record ActivityCount(string Key, int Count);

public sealed record ActivityPerson(Guid UserId, string Name, int Count);

public sealed record ActivityTool(string Channel, string Operation, int Calls, int Failures);
