using Atlas.Server.Features.Assistant.Feedback.Contracts;

namespace Atlas.Server.Features.Assistant.Feedback.Commands;

// Messages that carry a question or a note live in Commands: AtlasMessageLogging keeps their categories silent, and
// nothing here prints a field (plan phase 3 §3, §0 point 3).

/// <summary>The owner rates an answer; saving again overwrites.</summary>
public sealed record RateAiMessage(Guid MessageId, AiFeedbackRequest Request);

/// <summary>"Nahlásit chybějící obsah" under an answer without sources, or after a thumb down with the reason missing_info.</summary>
public sealed record ReportMissingContent(Guid MessageId);

/// <summary>"Nahlásit chybějící obsah" under a zero-result omnisearch.</summary>
public sealed record ReportMissingSearch(ReportMissingSearchRequest Request);
