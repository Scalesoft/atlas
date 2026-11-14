using Atlas.Server.Features.AiWriting;
using Xunit;

namespace Atlas.Server.Tests.AiWriting;

public sealed class WritingValidationTests
{
    [Fact]
    public void Components_are_opaque_and_keep_their_exact_order()
    {
        const string original = "Text ⟦c:0123456789abcdef:1⟧ a ⟦c:0123456789abcdef:2⟧.";
        WritingValidation.RequireProposal(original, "Lepší text ⟦c:0123456789abcdef:1⟧ a ⟦c:0123456789abcdef:2⟧.");
        var changed = Assert.Throws<ApiException>(() => WritingValidation.RequireProposal(original, "⟦c:0123456789abcdef:2⟧ ⟦c:0123456789abcdef:1⟧"));
        Assert.Equal("components_changed", changed.Code);
        Assert.Throws<ApiException>(() => WritingValidation.RequireProposal(original, original + " :secret{id=abc}"));
        Assert.Throws<ApiException>(() => WritingValidation.RequireProposal(original, original + "\n:::diagram\ngraph LR\n:::"));
    }

    [Fact]
    public void A_contact_and_a_chart_of_the_dialect_are_accepted_and_every_other_component_is_refused()
    {
        const string original = "Volejte Janě na ⟦c:0123456789abcdef:1⟧.";
        WritingValidation.RequireProposal(original, "Volejte [Jana Nová](a-contact:?name=Jana%20Nová&phone=+420%20777%20123%20456) kvůli ⟦c:0123456789abcdef:1⟧.\n\n"
            + "```a-contact\n- name: Jana Nová\n  phones: [\"+420 777 123 456\"]\n```\n\n```a-chart\ntitle: Hovory\n---\n| Měsíc | Hovory |\n| --- | --- |\n| Leden | 12,5 |\n```");
        foreach (var proposal in new[]
        {
            "```a-toc\ntitle: Obsah\n```",
            "Viz [stránku](a-page:42).",
            "```a-contact\n- name: Jana\n  fax: 123\n```",
            "```a-chart\n---\n| Měsíc | Hovory |\n| --- | --- |\n| Leden | hodně |\n```",
        })
        {
            Assert.Equal("ai_failed", Assert.Throws<ApiException>(() => WritingValidation.RequireProposal("", proposal)).Code);
        }
        // The prompt names the two forms and forbids the rest.
        Assert.Contains("```a-contact", WritingValidation.ComponentRule, StringComparison.Ordinal);
        Assert.Contains("```a-chart", WritingValidation.ComponentRule, StringComparison.Ordinal);
        Assert.Contains("Jiné komponenty", WritingValidation.ComponentRule, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_intents_are_filtered_by_catalogue_span_and_confidence()
    {
        var valid = new IntentSuggestion("contact.inline", 0, 5, "Pavel", .8);
        var result = WritingValidation.Intents([valid, valid with { Intent = "page.link" }, valid with { SpanEnd = 100 }, valid with { Confidence = double.NaN }], "Pavel píše návod.");
        Assert.Equal([valid], result);
    }

    [Fact]
    public void Outline_cannot_insert_components_or_a_document_title()
    {
        WritingValidation.RequireOutline("## Instalace\n\n> [!TIP]\n> Krátce popište instalaci.\n\n### Ověření\n\n> [!TIP]\n> Uveďte kontrolu.");
        Assert.Throws<ApiException>(() => WritingValidation.RequireOutline("# Název\nText"));
        Assert.Throws<ApiException>(() => WritingValidation.RequireOutline("## Nadpis\n\n> [!WARNING]\n> Pokyn."));
        Assert.Throws<ApiException>(() => WritingValidation.RequireOutline("## Nadpis\n\n- Celý věcný seznam"));
        Assert.Throws<ApiException>(() => WritingValidation.RequireOutline("## Hesla\n:secret{id=abc}"));
    }

    [Fact]
    public void Plaintext_keys_are_refused_while_code_and_hashes_remain_text()
    {
        Assert.False(WritingValidation.Safe("Použij sk-123456789abcdefghijklmnopqrstuv"));
        Assert.False(WritingValidation.Safe("-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----"));
        Assert.False(WritingValidation.Safe("heslo Srv#2026!rack"));
        Assert.False(WritingValidation.Safe("aZ7kP9rT2mQ6vX8bN5sL"));
        Assert.True(WritingValidation.Safe("password = provider.ClientSecret"));
        Assert.True(WritingValidation.Safe("git commit 0123456789abcdef0123456789abcdef"));
    }
}
