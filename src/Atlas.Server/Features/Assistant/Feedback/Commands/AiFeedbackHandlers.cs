using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Feedback.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Features.SearchGaps.Commands;
using Atlas.Server.Features.SearchGaps.Contracts;

namespace Atlas.Server.Features.Assistant.Feedback.Commands;

/// <summary>
/// <c>PUT /api/ai/messages/{id}/feedback</c>. User data, so it works without AI (no AiGate). Only the owner's answered
/// message: somebody else's or unknown is 404, one that is not an answer (yet) 409. Stores task, prompt version and profile
/// revision with the rating, never a person, a question or an answer.
/// </summary>
public sealed class RateAiMessageHandler(ICurrentActor actor, AiFeedbackStore store, TimeProvider clock)
{
    public async Task Handle(RateAiMessage command, CancellationToken ct)
    {
        var owner = await actor.RequireAsync(ct);
        var request = command.Request;
        if (!AiFeedbackRating.IsKnown(request.Rating))
        {
            throw new ApiException(400, "Vyberte palec nahoru nebo dolů.");
        }
        var down = request.Rating == AiFeedbackRating.Down;
        if (request.Reason is not null && (!down || !AiFeedbackReason.IsKnown(request.Reason)))
        {
            throw new ApiException(400, down ? "Neznámý důvod hodnocení." : "Palec nahoru nemá důvod.");
        }
        // Line breaks are kept; other control and bidirectional characters are refused (Rules.OptionalText).
        var note = request.Note is null ? null : Rules.OptionalText(request.Note, "Poznámka", int.MaxValue);
        if (note is not null && (!down || note.Length > AiFeedbackLimits.NoteMaxLength))
        {
            throw new ApiException(400, down ? "Poznámka může mít nejvýše 500 znaků." : "Palec nahoru nemá poznámku.");
        }
        var answer = await store.OwnedAnswerAsync(command.MessageId, owner.Id, ct)
            ?? throw new ApiException(404, "Odpověď nebyla nalezena.");
        if (answer.State != AiMessageState.Answered)
        {
            throw new ApiException(409, "Hodnotit lze jen hotovou odpověď.");
        }
        await store.SaveAsync(answer, request.Rating!, request.Reason, note, clock.GetUtcNow().UtcDateTime, ct);
    }
}

/// <summary>
/// <c>POST /api/ai/messages/{id}/report-missing</c>: "Nahlásit chybějící obsah" (G2, R9). The question of the turn goes to the
/// gaps of its space as <c>reported</c>, by the same policy as a zero-result query (D9) and without identity. Allowed for an
/// answer without sources, and for an answer rated thumb down with the reason missing_info.
/// </summary>
public sealed class ReportMissingContentHandler(ICurrentActor actor, AiFeedbackStore store, SearchMissWriter writer, TimeProvider clock)
{
    public async Task Handle(ReportMissingContent command, CancellationToken ct)
    {
        var owner = await actor.RequireAsync(ct);
        var answer = await store.OwnedAnswerAsync(command.MessageId, owner.Id, ct)
            ?? throw new ApiException(404, "Odpověď nebyla nalezena.");
        if (answer.State != AiMessageState.NoSources)
        {
            var feedback = await store.GetAsync(answer.MessageId, ct);
            if (answer.State != AiMessageState.Answered || feedback is not { Rating: AiFeedbackRating.Down, Reason: AiFeedbackReason.MissingInfo })
            {
                throw new ApiException(409, "Chybějící obsah lze nahlásit u odpovědi bez podkladů nebo po hodnocení „Chybí informace“.");
            }
        }
        await MissingContentReport.WriteAsync(writer, owner.Id, answer.Question, answer.SpaceId, clock, ct);
    }
}

/// <summary><c>POST /api/search/report-missing</c>: the zero-result rescue of the omnisearch (S7 point 4).</summary>
public sealed class ReportMissingSearchHandler(ICurrentActor actor, SearchMissWriter writer, TimeProvider clock)
{
    public async Task Handle(ReportMissingSearch command, CancellationToken ct)
    {
        var owner = await actor.RequireAsync(ct);
        var request = command.Request;
        await MissingContentReport.WriteAsync(writer, owner.Id, request.Query, request.SpaceId ?? request.ContextSpaceId, clock, ct);
    }
}

internal static class MissingContentReport
{
    /// <summary>Normalizes the question, applies the D9 policy and stores one <c>reported</c> miss. A question the policy refuses
    /// (too short, too long or with a secret-like word) is a 400 rather than a silent 204: the person pressed a button and must
    /// not be told it worked. The space is kept only when the person reaches it (SearchMissWriter).</summary>
    public static async Task WriteAsync(SearchMissWriter writer, Guid userId, string? question, Guid? spaceId, TimeProvider clock, CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = SearchText.Normalize(question);
        }
        catch (ArgumentException)
        {
            normalized = "";
        }
        if (!SearchMissPolicy.Allows(question, normalized))
        {
            throw new ApiException(400, "Tento dotaz nelze nahlásit: je příliš krátký, příliš dlouhý, nebo obsahuje něco, co vypadá jako heslo.");
        }
        await writer.WriteAsync([new SearchMiss(userId, spaceId, normalized, SearchMissPolicy.Display(question),
            DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), SearchGapReason.Reported)], ct);
    }
}
