using System.Text;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Assistant.Contracts;

/// <summary>The scope of a question (plan phase 3 R3): <c>wiki</c>, <c>space</c> (<see cref="SpaceId"/>) or <c>page</c>
/// (<see cref="PageId"/>); it only narrows.</summary>
public sealed record AiScope(string Kind, Guid? SpaceId, Guid? PageId);

/// <summary>
/// One source of an answer as the UI shows it (plan phase 3 §7, §20): <see cref="Number"/> is the <c>[n]</c> of the answer's prompt,
/// the rest is what the actor may read now. Every source given to the model is listed ("Proč tato odpověď?", spec RAG §5 point 8),
/// <see cref="Cited"/> marks the ones the sentences cite. <see cref="Changed"/>: the source has a newer published revision than the
/// answer used (the title is the current one, the heading and the excerpt are left out). <see cref="Excerpt"/> (at most 320
/// characters) only while the indexed text the answer used is still current. <see cref="PageId"/>, <see cref="SpaceId"/> and
/// <see cref="SpaceName"/> are null only for a source type other than <c>page</c> (AI-11; none in production 2.0).
/// </summary>
public sealed record AiSourceView(int Number, string Type, Guid? PageId, Guid? SpaceId, string? SpaceName, string Title,
    string? HeadingTitle, string? HeadingId, string? Excerpt, bool Cited, bool Changed)
{
    public const int ExcerptLength = 320;

    /// <summary>The first 320 characters of a chunk text, cut at a word when one ends near the limit; null for no text.</summary>
    public static string? ExcerptOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var trimmed = text.Trim();
        if (trimmed.Length <= ExcerptLength)
        {
            return trimmed;
        }
        var cut = trimmed[..(ExcerptLength - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > ExcerptLength - 40 ? cut[..space] : cut).TrimEnd() + "…";
    }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Number = ").Append(Number).Append(", Type = ").Append(Type).Append(", PageId = ").Append(PageId)
            .Append(", Cited = ").Append(Cited).Append(", Changed = ").Append(Changed);
        return true;
    }
}

/// <summary>One sentence of an answer: plain text (plan phase 3 R8) and the numbers of its sources.</summary>
public sealed record AiSentenceView(string Text, IReadOnlyList<int> Sources)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Sources = ").Append(string.Join(',', Sources));
        return true;
    }
}

/// <summary>The provenance line of a model output (návrh UI §5.4): <c>company</c> or <c>cloud</c>, the provider as the
/// administration names it and the model.</summary>
public sealed record AiProvenanceView(string Location, string Provider, string Model)
{
    public static AiProvenanceView From(AiProfileSnapshot profile, string? model = null)
        => From(profile.Provider, profile.Location, string.IsNullOrWhiteSpace(model) ? profile.Model : model);

    /// <summary>The provider's name in the UI: Anthropic, Gateway, OpenAI (an OpenAI-compatible cloud) or LM Studio (an OpenAI-compatible
    /// server in the company), as Administrace → AI presents the presets.</summary>
    public static AiProvenanceView From(string provider, string location, string model)
        => new(location, provider switch
        {
            AiProvider.Anthropic => "Anthropic",
            AiProvider.Gateway => "Gateway",
            _ => location == AiLocation.Cloud ? "OpenAI" : "LM Studio",
        }, model);
}

/// <summary>
/// One message of a conversation (plan phase 3 §7, §8). A question: <see cref="Role"/> <c>user</c>, <see cref="Question"/> and
/// <see cref="Scope"/>. An answer: <see cref="Role"/> <c>assistant</c>, <see cref="State"/>, its sentences and sources only when
/// <c>answered</c> (sources also while <c>preparing</c>), <see cref="Provenance"/> only when answered, <see cref="ErrorCode"/> of a
/// failed one. Read from history, an answer whose sources the actor may no longer read is <c>withheld</c> without text or
/// sources, and its question is left out as well (null): a follow-up may quote the answer it follows, and its own answer carries
/// that answer's sources as history (R4), so both disappear together. <see cref="Steps"/>: the tool calls of an answered agent turn
/// (<c>assistant.agent</c>), null for any other answer. <see cref="ProposalId"/>: the write proposal of an answered agent turn
/// (<c>GET /api/ai/proposals/{id}</c>, spec §6.1). <see cref="ToString"/> prints no content.
/// </summary>
public sealed record AiMessageView(Guid Id, Guid ConversationId, string Role, string? State, string? Question, AiScope? Scope,
    IReadOnlyList<AiSentenceView> Sentences, IReadOnlyList<AiSourceView> Sources, AiProvenanceView? Provenance,
    DateTime CreatedAt, string? Rating, string? ErrorCode, bool Truncated = false, IReadOnlyList<Domain.AiAgentStep>? Steps = null,
    Guid? ProposalId = null)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Role = ").Append(Role).Append(", State = ").Append(State)
            .Append(", Sentences = ").Append(Sentences.Count).Append(", Sources = ").Append(Sources.Count).Append(", ErrorCode = ").Append(ErrorCode);
        return true;
    }
}
