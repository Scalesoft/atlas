using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>The read answer (null: the output was no answer at all) and the last response, its usage summed over the attempts;
/// <see cref="Retried"/> whether the output was asked for again (the caller logs the rate).</summary>
public sealed record AnswerCompletion(AnswerResult? Result, ChatResponse Response, bool Retried = false);

/// <summary>
/// The structured answer call (K8, review cycle 1). A provider that does not enforce <c>json_schema</c> (Gateway) lets the model answer
/// <c>"sentences": ["…"]</c>; such a plain-string sentence is kept with only the citations it carries itself: the numbers it writes
/// (<c>[2]</c>) and the sources whose page title it names, as an agent answer (<c>AgentAnswer</c>). <see cref="AnswerValidator"/> then
/// checks them as any other; nothing is cited for it otherwise. An unusable output (no answer, or an answerable one with nothing
/// left after validation) is asked for once more with <see cref="Correction"/>, unless the token limit cut it off: the same limit
/// would cut the retry (P2-14). Pure apart from the model call; logs nothing.
/// </summary>
public static class AnswerOutput
{
    public const string Correction = "Odpověď nemá požadovaný tvar: každá věta musí být objekt s poli text a sources (čísla zdrojů, ze kterých věta vychází). "
        + """Vrať odpověď znovu pouze jako JSON {"answerable":true,"sentences":[{"text":"Věta.","sources":[1]}]}.""";

    public static async Task<AnswerCompletion> GetAsync(IChatClient chat, IReadOnlyList<ChatMessage> messages, IReadOnlyList<string> sourceTitles,
        ChatOptions options, CancellationToken ct)
    {
        var first = await chat.GetResponseAsync<AnswerResult>(messages, JsonSerializerOptions.Web, options, cancellationToken: ct);
        var result = Parse(first.Text, sourceTitles);
        if ((result is not null && !(result.Answerable && AnswerValidator.Validate(result, sourceTitles.Count).NoSources))
            || first.FinishReason == ChatFinishReason.Length)
        {
            return new AnswerCompletion(result, first);
        }
        var second = await chat.GetResponseAsync<AnswerResult>([.. messages, new ChatMessage(ChatRole.Assistant, first.Text), new ChatMessage(ChatRole.User, Correction)],
            JsonSerializerOptions.Web, options.Clone(), cancellationToken: ct);
        if (first.Usage is { } usage)
        {
            (second.Usage ??= new UsageDetails()).Add(usage);
        }
        return new AnswerCompletion(Parse(second.Text, sourceTitles) ?? result, second, Retried: true);
    }

    /// <summary>The answer in <paramref name="text"/>: an object with a boolean <c>answerable</c> and an array <c>sentences</c> of
    /// <c>{text, sources}</c> objects or plain strings (other items are skipped). Null for anything else.</summary>
    public static AnswerResult? Parse(string? text, IReadOnlyList<string> sourceTitles)
    {
        try
        {
            using var document = JsonDocument.Parse(text ?? "");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            // A missing property is its default, as the deserializer read it before: no answerable flag is an abstention.
            var hasAnswerable = TryProperty(root, "answerable", out var answerable);
            var hasSentences = TryProperty(root, "sentences", out var items);
            if ((hasAnswerable && answerable.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                || (hasSentences && items.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null)))
            {
                return null;
            }
            var sentences = (items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : []).Select(item => Sentence(item, sourceTitles))
                .OfType<AnswerSentence>().ToArray();
            return new AnswerResult(hasAnswerable && answerable.GetBoolean(), sentences);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A <c>{text, sources}</c> object, or a plain string with the citations it writes and the source titles it names; null
    /// for anything else.</summary>
    private static AnswerSentence? Sentence(JsonElement item, IReadOnlyList<string> sourceTitles)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var plain = item.GetString()!;
            return new AnswerSentence(plain, AnswerValidator.WrittenCitations(plain)
                .Concat(sourceTitles.Select((title, index) => (title, number: index + 1)).Where(x => AnswerValidator.NamesTitle(plain, x.title)).Select(x => x.number))
                .Distinct().ToArray());
        }
        if (item.ValueKind == JsonValueKind.Object && TryProperty(item, "text", out var sentence) && sentence.ValueKind == JsonValueKind.String)
        {
            var sources = TryProperty(item, "sources", out var numbers) && numbers.ValueKind == JsonValueKind.Array
                ? numbers.EnumerateArray().Select(Number).OfType<int>().ToArray() : [];
            return new AnswerSentence(sentence.GetString()!, sources);
        }
        return null;
    }

    private static int? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt32(out var number) => number,
        JsonValueKind.String when int.TryParse(element.GetString(), out var number) => number,
        _ => null,
    };

    /// <summary>Property names case-insensitively, as <see cref="JsonSerializerOptions.Web"/> reads them.</summary>
    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
