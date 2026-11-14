using Atlas.Server.Features.Assistant.Contracts;

namespace Atlas.Server.Features.AiWriting;

public sealed record IntentRequest(Guid PageId, string? Text, string? Heading = null, int CatalogVersion = 1)
{
    public override string ToString() => nameof(IntentRequest);
}
public sealed record TransformRequest(Guid PageId, string? Text, string? Action, string? Instruction = null)
{
    public override string ToString() => nameof(TransformRequest);
}
public sealed record OutlineRequest(Guid PageId, string? Topic)
{
    public override string ToString() => nameof(OutlineRequest);
}
public sealed record PublishedDiffRequest(Guid PageId, int ExpectedVersion);
public sealed record IntentSuggestion(string Intent, int SpanStart, int SpanEnd, string Query, double Confidence)
{
    public override string ToString() => nameof(IntentSuggestion);
}
public sealed record IntentResponse(IReadOnlyList<IntentSuggestion> Suggestions);
public sealed record TagProposal(IReadOnlyList<Atlas.Server.Features.PageTags.TagDto> Tags, AiProvenanceView Provenance);
public sealed record TextProposal(string Text, AiProvenanceView Provenance)
{
    public override string ToString() => nameof(TextProposal);
}
public sealed record PublishIssue(int SpanStart, int SpanEnd, string Original, string Correction)
{
    public override string ToString() => nameof(PublishIssue);
}
public sealed record PublishCheckResponse(IReadOnlyList<PublishIssue> Issues, AiProvenanceView Provenance);
public sealed record WritingResult(string? Text = null, IReadOnlyList<IntentSuggestion>? Suggestions = null, IReadOnlyList<PublishIssue>? Issues = null, IReadOnlyList<Guid>? TagIds = null)
{
    public override string ToString() => nameof(WritingResult);
}
