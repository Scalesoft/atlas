namespace Atlas.Server.Features.Ai.Usage.Commands;

/// <summary>Usage the recorder buffered, written as one INSERT. Sent by <see cref="AiUsageFlusher"/> in a system scope, never
/// over HTTP. The entries hold no content, so logging the message would leak nothing.</summary>
public sealed record RecordAiUsage(IReadOnlyList<AiUsageEntry> Entries);
