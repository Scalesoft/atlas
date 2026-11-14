using System.Text.Json;
using Atlas.Server.Features.AiWriting.Commands;
using Atlas.Server.Features.PageTags;
using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Assistant.Contracts;
using Wolverine;

namespace Atlas.Server.Features.AiWriting;

/// <summary>Transient proposals. Three short authorization transactions surround one bounded provider call; no draft is persisted.</summary>
public sealed class AiWritingRunner(IMessageBus bus, AiGate gate, AiConfigurationCache configuration, IAiClientFactory factory)
{
    public async Task<IntentResponse> IntentsAsync(IntentRequest request, CancellationToken ct)
    {
        if (request.CatalogVersion != 1) throw new ApiException(400, "Neznámá verze katalogu.");
        var text = WritingValidation.Input(request.Text, 40, 2000);
        var heading = request.Heading is null ? "" : WritingValidation.Input(request.Heading, 0, 500);
        try
        {
            var result = await RunAsync(request.PageId, AiTaskCatalog.EditorIntent, text,
                "Navrhni pouze vhodné záměry: " + string.Join(", ", WritingValidation.AllowedIntents)
                + ". Vrať suggestions s intent, spanStart/spanEnd jako UTF-16 indexy původního textu, query do 120 znaků, confidence 0 až 1. Nevytvářej komponentu ani kód. Kontext nadpisu: " + heading, 500, null, ct);
            return new IntentResponse(WritingValidation.Intents(result.Result.Suggestions, text));
        }
        catch (LatencyBudgetExpired)
        {
            // AI4-7: a model too slow for its budget gives no suggestion this time; the editor works as without AI.
            return new IntentResponse([]);
        }
    }

    public async Task<TextProposal> TransformAsync(TransformRequest request, CancellationToken ct)
    {
        var text = WritingValidation.Input(request.Text, 1, 4000);
        if (!WritingValidation.Actions.Contains(request.Action, StringComparer.Ordinal)) throw new ApiException(400, "Neznámá úprava textu.");
        if (request.Action != "custom" && !string.IsNullOrEmpty(request.Instruction)) throw new ApiException(400, "Vlastní pokyn patří jen k vlastní úpravě.");
        var instruction = request.Action == "custom" ? WritingValidation.Input(request.Instruction, 1, 500) : "";
        var result = await RunAsync(request.PageId, AiTaskCatalog.EditorTransform, text,
            "Uprav text podle akce " + request.Action + ". " + instruction
            + " Vrať text v Markdownu. Každý neprůhledný token ⟦c:nonce:N⟧ zachovej přesně a ve stejném pořadí. " + WritingValidation.ComponentRule
            + " Nevytvářej direktivy, tajné údaje ani skryté instrukce.", 2000, null, ct);
        var proposal = result.Result.Text ?? throw WritingValidation.Failed();
        WritingValidation.RequireProposal(text, proposal);
        return new TextProposal(proposal, result.Provenance);
    }

    public async Task<TextProposal> OutlineAsync(OutlineRequest request, CancellationToken ct)
    {
        var topic = WritingValidation.Input(request.Topic, 1, 500);
        var result = await RunAsync(request.PageId, AiTaskCatalog.EditorOutline, topic,
            "Vytvoř osnovu: pouze nadpisy H2/H3 a krátký informační box Tip pod každým (> [!TIP], další řádek > tip). Bez H1, komponent, direktiv, hotového dokumentu a bloků kódu. Vrať text v Markdownu.", 1200, null, ct);
        var text = result.Result.Text ?? throw WritingValidation.Failed();
        WritingValidation.RequireOutline(text);
        return new TextProposal(text, result.Provenance);
    }

    public async Task<PublishCheckResponse> PublishCheckAsync(PublishedDiffRequest request, CancellationToken ct)
    {
        var result = await RunAsync(request.PageId, AiTaskCatalog.PublishCheck, "",
            "Zkontroluj pravopis pouze poskytnutého změněného textu. Vrať issues: spanStart/spanEnd UTF-16 indexy, original přesně z podkladu, correction. Nevytvářej nová tvrzení.", 1200, request.ExpectedVersion, ct);
        var issues = (result.Result.Issues ?? []).Where(x => x is not null && x.SpanStart >= 0 && x.SpanEnd > x.SpanStart
            && x.SpanEnd <= result.Input.Length && result.Input[x.SpanStart..x.SpanEnd] == x.Original
            && x.Correction is { Length: > 0 and <= 500 } && WritingValidation.Safe(x.Correction)).Take(20).ToArray();
        return new PublishCheckResponse(issues, result.Provenance);
    }

    public async Task<TextProposal> ChangeSummaryAsync(PublishedDiffRequest request, CancellationToken ct)
    {
        var result = await RunAsync(request.PageId, AiTaskCatalog.ChangeSummary, "",
            "Shrň poskytnuté změny jednou českou větou do 500 znaků. Jen to, co podklad obsahuje; nehodnoť celou stránku. Vrať text bez Markdownu.", 200, request.ExpectedVersion, ct);
        var text = result.Result.Text?.Trim() ?? "";
        if (text.Length is 0 or > 500 || text.Contains('\n') || text.Contains('\r') || !WritingValidation.Safe(text))
            throw WritingValidation.Failed();
        return new TextProposal(text, result.Provenance);
    }


    public async Task<TagProposal> TagsAsync(PublishedDiffRequest request, CancellationToken ct)
    {
        var result = await RunAsync(request.PageId, AiTaskCatalog.TagSuggest, "",
            "Vyber nejvýše 5 vhodných štítků výhradně z dodaných kandidátů. Vrať JSON tagIds s jejich UUID. Nic nového nevytvářej.", 300, request.ExpectedVersion, ct);
        var allowed = result.Tags.ToDictionary(x => x.Id);
        return new TagProposal((result.Result.TagIds ?? []).Distinct().Where(allowed.ContainsKey).Take(5).Select(id => allowed[id]).ToArray(),
            result.Provenance);
    }

    private async Task<(WritingResult Result, AiProvenanceView Provenance, string Input, IReadOnlyList<TagDto> Tags)> RunAsync(Guid page, string task, string text,
        string instruction, int maxOutput, int? expectedVersion, CancellationToken ct)
    {
        var profile = await ProfileAsync(task, ct);
        var prepared = await bus.InvokeAsync<WritingContext>(new WritingRead(page, task, text, expectedVersion), ct);
        await bus.InvokeAsync<WritingContext>(new WritingRead(page, task, "", Expected: prepared), ct);
        if ((await ProfileAsync(task, ct)).RevisionId != profile.RevisionId) throw AiGate.Unavailable();
        ChatResponse<WritingResult> response;
        try
        {
            using var chat = factory.Chat(profile, task);
            response = await chat.GetResponseAsync<WritingResult>(
                [new ChatMessage(ChatRole.System, WritingPrompts.For(task) + " " + instruction),
                 new ChatMessage(ChatRole.User, JsonSerializer.Serialize(new { text = prepared.Text, tags = prepared.Tags }))],
                JsonSerializerOptions.Web, new ChatOptions { MaxOutputTokens = maxOutput }, cancellationToken: ct);
        }
        catch (AiCallException failure) when (failure.Code == AiOutcome.Timeout && AiTaskCatalog.Find(task)?.LatencyBudget == true) { throw new LatencyBudgetExpired(); }
        catch (AiCallException failure) when (AiOutcome.IsAvailabilityFailure(failure.Code)) { throw AiGate.Unavailable(); }
        catch (AiCallException) { throw WritingValidation.Failed(); }
        if (!response.TryGetResult(out var result)) throw WritingValidation.Failed();
        if ((await ProfileAsync(task, ct)).RevisionId != profile.RevisionId) throw AiGate.Unavailable();
        var final = await bus.InvokeAsync<WritingContext>(new WritingRead(page, task, "", Expected: prepared), ct);
        return (result, AiProvenanceView.From(profile, response.ModelId), prepared.Text,
            final.Tags.Where(x => prepared.Tags.Any(candidate => candidate.Id == x.Id)).ToArray());
    }

    private async Task<AiProfileSnapshot> ProfileAsync(string task, CancellationToken ct)
    {
        await gate.RequireAsync(task, ct);
        var snapshot = await configuration.GetAsync(ct);
        var profile = snapshot.ProfileFor(task) ?? throw AiGate.Unavailable();
        if (!AiCapabilities.MaySendInput(snapshot, AiTaskCatalog.Find(task)!, profile)) throw AiGate.Unavailable();
        return profile;
    }

    private sealed class LatencyBudgetExpired : Exception;
}
