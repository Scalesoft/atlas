using System.Text.RegularExpressions;

namespace Atlas.Server.Features.AiWriting;

/// <summary>Only ordinary Markdown and the client's opaque component tokens may leave the model.</summary>
public static partial class WritingValidation
{
    public static readonly string[] AllowedIntents = ["contact.inline", "contact.card", "secret.reference", "callout.wrap", "diagram.fromText"];
    /// <summary>
    /// The components a writing proposal may create (WP-32, D13): a contact and a chart in the editor's dialect, from facts of the text
    /// only. <see cref="RequireProposal"/> reads them as the page does and refuses every other or invalid <c>a-</c> component.
    /// </summary>
    public const string ComponentRule = "Z komponent smíš vytvořit jen kontakt a graf, a to jen z údajů v textu. " + Mcp.Tools.McpDraftTools.ContactRule
        + " Kontakt, který text už obsahuje, použij i s jeho objectId; jiný napiš bez objectId a Atlas ho propojí s adresářem. Kontakt jako blok ```a-contact s jedním kontaktem v YAML "
        + "(- name: Jana Nová / phones: [\"+420 777 123 456\"] / emails: [jana@example.com]), několik lidí = několik bloků za sebou, nebo v textu [Jana Nová](a-contact:?name=Jana%20Nová&phone=+420%20777%20123%20456), "
        + "graf jako blok ```a-chart s hlavičkou (title, kind: bar|line|pie|doughnut), řádkem --- a tabulkou Markdownu (první řádek řady, první sloupec kategorie, "
        + "čísla s desetinnou čárkou). Jiné komponenty ani jiné bloky ```a-… nevytvářej.";

    public static readonly string[] Actions = ["proofread", "shorten", "simplify", "formal", "informal", "translate-en", "translate-cs", "bullets", "steps", "table", "custom"];

    [GeneratedRegex(@"⟦c:[0-9a-f]{16}:[1-9][0-9]{0,5}⟧")]
    private static partial Regex Tokens();
    [GeneratedRegex(@"(^|\s):{1,3}[a-z][\w-]*(\{|\[|\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Directives();

    public static string Input(string? text, int minimum, int maximum)
    {
        if (text is null || text.Length < minimum || text.Length > maximum)
            throw new ApiException(400, "Neplatná délka textu.");
        RequireSafe(text);
        return text;
    }

    public static bool Safe(string text) => !text.Contains(":secret", StringComparison.OrdinalIgnoreCase) && !Directives().IsMatch(text) && !WritingSecrets.Contains(Tokens().Replace(text, ""))
        && !Tokens().Replace(text, "").Contains('⟦') && !Tokens().Replace(text, "").Contains('⟧');

    public static void RequireSafe(string text)
    {
        if (!Safe(text)) throw new ApiException(400, "Text obsahuje nepodporovanou komponentu nebo citlivý údaj.");
    }

    public static void RequireProposal(string original, string proposal)
    {
        if (!Tokens().Matches(original).Select(x => x.Value).SequenceEqual(Tokens().Matches(proposal).Select(x => x.Value))
            || Tokens().Replace(proposal, "").IndexOfAny(['⟦', '⟧']) >= 0)
            throw new ApiException(400, "Komponenty musí zůstat beze změny.", "components_changed");
        // The page's dialect: a contact or chart the editor reads is fine, any other or invalid a- component is a warning.
        if (!Safe(proposal) || Atlas.Server.Platform.Content.MarkdownToRich.Parse(Tokens().Replace(proposal, ""), Atlas.Server.Platform.Content.MarkdownToRich.PagePlugins).Warnings.Count > 0) throw Failed();
        if (string.IsNullOrWhiteSpace(proposal) || proposal.Length > 16_000)
            throw Failed();
    }

    public static IReadOnlyList<IntentSuggestion> Intents(IReadOnlyList<IntentSuggestion>? suggestions, string text)
        => (suggestions ?? []).Where(x => x is not null && AllowedIntents.Contains(x.Intent, StringComparer.Ordinal)
            && x.SpanStart >= 0 && x.SpanEnd > x.SpanStart && x.SpanEnd <= text.Length
            && x.Query is { Length: > 0 and <= 120 } && Safe(x.Query)
            && double.IsFinite(x.Confidence) && x.Confidence >= .5 && x.Confidence <= 1).Take(5).ToArray();

    public static void RequireOutline(string text)
    {
        RequireProposal("", text);
        var parsed = Atlas.Server.Platform.Content.MarkdownToRich.Parse(text);
        var hasHeading = false;
        var tipCount = 0;
        foreach (var block in parsed.Blocks)
        {
            var type = Atlas.Server.Platform.Content.RichDocument.TypeOf(block);
            if (type == "heading")
            {
                var level = block?["attrs"]?["level"]?.GetValue<int>();
                if (level is not (2 or 3)) throw Failed();
                hasHeading = true;
                tipCount = 0;
            }
            else if (!hasHeading || ++tipCount > 1 || !Tip(block))
                throw Failed();
        }
        if (!hasHeading || parsed.Warnings.Count > 0 || text.Length > 6000) throw Failed();
    }
    private static bool Tip(System.Text.Json.Nodes.JsonNode? block)
        => Atlas.Server.Platform.Content.RichDocument.TypeOf(block) == "callout"
            && block?["attrs"]?["variant"]?.GetValue<string>() == "tip"
            && block["content"] is System.Text.Json.Nodes.JsonArray { Count: 1 } content
            && Atlas.Server.Platform.Content.RichDocument.TypeOf(content[0]) == "paragraph"
            && content[0]?["content"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } words
            && words.Sum(x => x?["text"]?.GetValue<string>()?.Length ?? 0) <= 300;

    public static ApiException Failed() => new(502, "Návrh se nepodařilo připravit.", "ai_failed");
}
