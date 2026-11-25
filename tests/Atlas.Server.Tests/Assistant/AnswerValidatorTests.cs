using Atlas.Server.Features.Assistant.Answering;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Plan phase 3 Q7/§7/R8: citation validation is a pure function; the UI draws citations only from the passed sources.</summary>
public sealed class AnswerValidatorTests
{
    private static AnswerSentence Sentence(string text, params int[] sources) => new(text, sources);

    [Fact]
    public void Numbers_outside_the_passed_sources_are_dropped_and_counted()
    {
        var result = AnswerValidator.Validate(new AnswerResult(true, [Sentence("Postup je ve wiki.", 1, 9, 0, -2), Sentence("Další věta.", 3)]), sourceCount: 3);

        Assert.True(result.Answerable);
        Assert.Equal([1], result.Sentences[0].Sources);
        Assert.Equal([3], result.Sentences[1].Sources);
        Assert.Equal([1, 3], result.Cited);
        Assert.Equal(3, result.InvalidCitations);
        Assert.Equal(0, result.DroppedSentences);
    }

    [Fact]
    public void An_answerable_sentence_without_a_valid_source_is_dropped_and_nothing_left_means_no_sources()
    {
        var invented = AnswerValidator.Validate(new AnswerResult(true, [Sentence("Vymyšlené tvrzení.", 9), Sentence("Bez zdroje.")]), sourceCount: 2);

        Assert.True(invented.NoSources);
        Assert.Empty(invented.Sentences);
        Assert.Empty(invented.Cited);
        Assert.Equal(1, invented.InvalidCitations);
        Assert.Equal(2, invented.DroppedSentences);

        var mixed = AnswerValidator.Validate(new AnswerResult(true, [Sentence("Dobrá.", 2), Sentence("Bez zdroje.")]), sourceCount: 2);
        Assert.Equal(["Dobrá."], mixed.Sentences.Select(s => s.Text));
        Assert.Equal(1, mixed.DroppedSentences);
    }

    [Fact]
    public void An_abstention_or_a_missing_result_is_no_sources_even_with_sentences()
    {
        Assert.True(AnswerValidator.Validate(new AnswerResult(false, []), 3).NoSources);
        Assert.True(AnswerValidator.Validate(new AnswerResult(false, [Sentence("Něco.", 1)]), 3).NoSources);
        Assert.True(AnswerValidator.Validate(new AnswerResult(true, []), 3).NoSources);
        Assert.True(AnswerValidator.Validate(null, 3).NoSources);
        Assert.True(AnswerValidator.Validate(new AnswerResult(true, [Sentence("Věta.", 1)]), 0).NoSources);
    }

    [Theory]
    [InlineData("Voucher platí 24 hodin [1].", "Voucher platí 24 hodin.")]
    [InlineData("Voucher platí 24 hodin (2) na recepci [zdroj 1, 2].", "Voucher platí 24 hodin na recepci.")]
    [InlineData("Viz <b>recepce</b><script>x()</script>.", "Viz recepcex().")]
    [InlineData("Obrázek ![kanárek](http://evil.example/a.png) zmizí.", "Obrázek zmizí.")]
    [InlineData("Otevřete [Service Desk](https://sd.example/odkaz) a založte požadavek.", "Otevřete Service Desk a založte požadavek.")]
    [InlineData("Použijte `rdp.example.test` jako bránu.", "Použijte `rdp.example.test` jako bránu.")]
    [InlineData("- Hotovo **34 %** [3].", "Hotovo **34 %**.")]
    [InlineData("• Čeká se na **termín**.", "Čeká se na **termín**.")]
    public void Sentence_text_is_plain_text_without_citation_markers_html_images_and_link_targets(string raw, string expected)
    {
        var result = AnswerValidator.Validate(new AnswerResult(true, [Sentence(raw, 1)]), 1);

        Assert.Equal(expected, result.Sentences.Single().Text);
    }

    [Fact]
    public void Sentences_are_cut_to_600_characters_and_at_most_twelve_are_kept()
    {
        var long600 = new string('a', 800);
        var result = AnswerValidator.Validate(new AnswerResult(true, [.. Enumerable.Range(1, 14).Select(i => Sentence(i == 1 ? long600 : $"Věta {i}.", 1))]), 1);

        Assert.Equal(12, result.Sentences.Count);
        Assert.Equal("Věta 12.", result.Sentences[^1].Text);
        Assert.Equal(AnswerValidator.MaxSentenceLength, result.Sentences[0].Text.Length);
    }

    [Fact]
    public void An_empty_sentence_after_cleaning_is_dropped_without_counting_as_a_missing_source()
    {
        var result = AnswerValidator.Validate(new AnswerResult(true, [Sentence("[1]", 1), Sentence("Platná.", 1)]), 1);

        Assert.Equal(["Platná."], result.Sentences.Select(s => s.Text));
        Assert.Equal(0, result.DroppedSentences);
    }
}
