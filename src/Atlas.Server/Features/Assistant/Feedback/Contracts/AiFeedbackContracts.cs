namespace Atlas.Server.Features.Assistant.Feedback.Contracts;

public static class AiFeedbackRating
{
    public const string Up = "up";
    public const string Down = "down";

    public static bool IsKnown(string? rating) => rating is Up or Down;
}

/// <summary>The reasons offered under a thumb down (UX design §5). <see cref="MissingInfo"/> opens "Nahlásit chybějící obsah".</summary>
public static class AiFeedbackReason
{
    public const string Inaccurate = "inaccurate";
    public const string MissingInfo = "missing_info";
    public const string WrongSources = "wrong_sources";
    public const string Unsuitable = "unsuitable";
    public const string Other = "other";

    public static bool IsKnown(string? reason) => reason is Inaccurate or MissingInfo or WrongSources or Unsuitable or Other;
}

public static class AiFeedbackLimits
{
    public const int NoteMaxLength = 500;
}

/// <summary><c>PUT /api/ai/messages/{id}/feedback</c>. A thumb up carries neither reason nor note.</summary>
public sealed record AiFeedbackRequest(string? Rating, string? Reason, string? Note);

/// <summary><c>POST /api/search/report-missing</c>: the zero-result rescue of the omnisearch. The space is the filter, else the
/// omnisearch context.</summary>
public sealed record ReportMissingSearchRequest(string? Query, Guid? SpaceId, Guid? ContextSpaceId);
