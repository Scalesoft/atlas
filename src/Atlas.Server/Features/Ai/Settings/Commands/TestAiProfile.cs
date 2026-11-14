namespace Atlas.Server.Features.Ai.Settings.Commands;

/// <summary>Step 1 of "Test spojení": WikiAdmin, then the saved revision of the kind as a snapshot. The model is called after
/// this transaction has ended (plan §3: never a model call inside a handler or under a lock).</summary>
public sealed record BeginAiProfileTest(string Kind);

/// <summary>Step 2: the kind's current revision must still be the tested one, else 409 (a save during the test).</summary>
public sealed record CompleteAiProfileTest(string Kind, Guid RevisionId);
