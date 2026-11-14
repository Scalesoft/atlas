using System.Text;

namespace Atlas.Server.Features.Assistant.Contracts;

/// <summary>
/// One row of "Moje konverzace" (plan phase 3 §8). <see cref="Title"/> is the first question shortened to 80 characters, or null
/// when the conversation is not available (an answer's source can no longer be read: "Konverzace není dostupná", spec RAG §4:
/// no name of a protected answer in the overview). <see cref="Provenance"/> is the conversation's pinned chat profile.
/// </summary>
public sealed record AiConversationSummary(Guid Id, string? Title, bool Available, DateTime LastActivityAt, AiProvenanceView Provenance)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Available = ").Append(Available).Append(", LastActivityAt = ").Append(LastActivityAt);
        return true;
    }
}

/// <summary>A page of "Moje konverzace", newest activity first. <see cref="Next"/> is the <c>before</c> of the next page, null
/// on the last one.</summary>
public sealed record AiConversationList(IReadOnlyList<AiConversationSummary> Items, DateTime? Next);

/// <summary>
/// A conversation read from history (plan phase 3 §8, R4, R5), revalidated on this read. <see cref="Available"/>: no answer is
/// withheld now. <see cref="CanContinue"/>: available and the current chat profile is configured, switched on and compatible
/// with the pinned one (the same provider, location, URL origin and model). <see cref="Outdated"/>: a chat profile is configured
/// but not compatible, so the conversation stays read-only ("Tato konverzace používá předchozí nastavení. · Začít novou").
/// </summary>
public sealed record AiConversationView(Guid Id, bool Available, bool CanContinue, bool Outdated, AiProvenanceView Provenance,
    IReadOnlyList<AiMessageView> Messages)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Available = ").Append(Available).Append(", CanContinue = ").Append(CanContinue)
            .Append(", Outdated = ").Append(Outdated).Append(", Messages = ").Append(Messages.Count);
        return true;
    }
}
