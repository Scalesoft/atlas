using System.Text;
using Atlas.Server.Features.PageLinks.Contracts;

namespace Atlas.Server.Features.Assistant.Contracts;

/// <summary>The icon, colour and tag names of a page, before or after a proposed change.</summary>
public sealed record AiProposalLabels(string? Icon, string? Color, IReadOnlyList<string> Tags)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Tags = ").Append(Tags.Count);
        return true;
    }
}

/// <summary>
/// The text of a draft item (spec §7.2, §9.2): the titles of the changed sections and their Markdown before and after, placeholders
/// shown as <c>⟦komponenta: …⟧</c>. A rename: the old and the new title, no sections. A new draft: its headings, no text before, the
/// first lines after.
/// </summary>
public sealed record AiProposalText(IReadOnlyList<string> Sections, string Before, string After)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Sections = ").Append(Sections.Count);
        return true;
    }
}

/// <summary>
/// A move (WP-40): the path „Prostor › … › Stránka“ before and after, as the user sees the trees when the proposal is made; the
/// subpages that move with the page (those the user sees); <see cref="OtherSpace"/>: the page leaves its space and takes the target
/// space's default access.
/// </summary>
public sealed record AiProposalMove(string Before, string After, int Subpages, bool OtherSpace)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Subpages = ").Append(Subpages).Append(", OtherSpace = ").Append(OtherSpace);
        return true;
    }
}

/// <summary>
/// A publication (WP-41, spec §6.3): from <c>McpPublishSummaries</c>: the version to publish,
/// the published one (null = first publication), the sections changed against it, the change summary and the link findings of the
/// check before publication.
/// </summary>
public sealed record AiProposalPublish(int Version, int? PublishedVersion, int ChangedSections, string? ChangeSummary, PublicationCheck Findings)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Version = ").Append(Version).Append(", ChangedSections = ").Append(ChangedSections).Append(", Findings = ").Append(Findings.Links.Count);
        return true;
    }
}

/// <summary>
/// One item of a proposal card (spec §6.1, §9.2): one change of one page. <see cref="Index"/> is what a decision selects;
/// <see cref="Title"/>, <see cref="Path"/> and <see cref="Before"/> were read by the server under the user's rights when the proposal
/// was made, <see cref="After"/> is the server's reading of the model's arguments (never the model's own claim of the current state).
/// <see cref="Kind"/>: <c>labels</c> (<see cref="Before"/> and <see cref="After"/>), or a draft write with <see cref="Text"/>:
/// <c>content</c> (changed sections), <c>rename</c> or <c>draft</c> (a new page: <see cref="PageId"/> empty until it is created, then
/// the new page), <c>move</c> with <see cref="Move"/>, <c>publish</c> with <see cref="Publish"/>. <see cref="Status"/> after the decision: <c>applied</c>, <c>failed</c> (with <see cref="Code"/>)
/// or <c>skipped</c> (not selected); null while undecided or declined.
/// </summary>
public sealed record AiProposalItemView(int Index, Guid PageId, string Title, string Path, string Kind, AiProposalLabels? Before,
    AiProposalLabels? After, string? Status = null, string? Code = null, AiProposalText? Text = null, AiProposalMove? Move = null,
    AiProposalPublish? Publish = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Index = ").Append(Index).Append(", PageId = ").Append(PageId).Append(", Kind = ").Append(Kind)
            .Append(", Status = ").Append(Status).Append(", Code = ").Append(Code);
        return true;
    }
}

/// <summary>
/// A proposal of an agent write (the NDJSON <c>proposal</c> line, <c>GET /api/ai/proposals/{id}</c> and the decision's answer).
/// <see cref="State"/>: <c>pending</c>, <c>applying</c>, <c>applied</c>, <c>failed</c>, <c>declined</c>, <c>expired</c> (also a
/// pending one past <see cref="ExpiresAt"/>), <c>superseded</c>.
/// </summary>
public sealed record AiProposalView(Guid Id, Guid MessageId, string Tool, string State, DateTime ExpiresAt, IReadOnlyList<AiProposalItemView> Items)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Tool = ").Append(Tool).Append(", State = ").Append(State).Append(", Items = ").Append(Items.Count);
        return true;
    }
}

/// <summary><c>POST /api/ai/proposals/{id}/decision</c>: <see cref="Approve"/> and, when approving, the indexes of the chosen items.
/// Nothing else is read: the arguments that run are the stored ones (spec §6.2).</summary>
public sealed record DecideAiProposalRequest(bool? Approve, IReadOnlyList<int>? Items);
