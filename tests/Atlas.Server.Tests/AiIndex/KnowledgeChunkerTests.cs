using System.Security.Cryptography;
using System.Text;
using Atlas.Server.Features.AiIndex.Domain;
using Xunit;

namespace Atlas.Server.Tests.AiIndex;

public sealed class KnowledgeChunkerTests
{
    /// <summary>The AI index reads the stored projection, so Projector's requirement lines reach it as text (spec 2026-10-27 §1.7).</summary>
    [Fact]
    public void Requirement_blocks_are_indexed_with_their_title_state_and_key()
    {
        var content = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "requirements.json")))![0]!;
        var markdown = Atlas.Server.Platform.Content.EditorMarkdownWriter.Write(content["content"]!["document"]!.AsObject());

        var text = string.Join("\n", KnowledgeChunker.Chunk("KBN — Production", markdown, [new("Portál", "portal")], "").Select(x => x.Text));

        Assert.Contains("SSL certifikát", text, StringComparison.Ordinal);
        Assert.Contains("hotovo 10. 9. 2026 · vstup · od: IT KBN · uloženo: Keepass › KBN · klíč: portal.ssl", text, StringComparison.Ordinal);
        Assert.Contains("Poznámka pod požadavkem…", text, StringComparison.Ordinal);
        // The summary names the page, so the near-equal summaries of copied deployments embed apart (K5), and whom it waits on (K4).
        Assert.Contains("**Stav nasazení KBN — Production:** 33 % hotovo (1 z 3) · čeká na: SMS brána (IT KBN) · nedodané vstupy: SMS brána (IT KBN)",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sections_split_by_headings_pack_paragraphs_to_1800_characters_and_keep_title_heading_and_anchor()
    {
        var longParagraph = string.Join(' ', Enumerable.Repeat("slovo", 500));
        var markdown = string.Join('\n',
            "Úvodní odstavec před nadpisem.",
            "",
            "## Přístup",
            "Připojení přes [VPN](#/pages/1) a ![schéma](https://example.com/vpn.png).",
            "",
            "```bash",
            "# není nadpis, je v bloku kódu",
            "```",
            "",
            "## Přístup",
            "Druhá sekce se stejným názvem.",
            "",
            "### Dlouhá",
            longParagraph,
            "",
            "Krátký odstavec za dlouhým.");
        IndexHeading[] headings = [new("Přístup", "pristup"), new("Přístup", "pristup-1"), new("Dlouhá", null)];

        var chunks = KnowledgeChunker.Chunk("VPN", markdown, headings, "passage: ");

        Assert.Equal(["Úvodní odstavec před nadpisem."], Bodies(chunks, null));
        // Text before the first heading points at the start of the page: the title alone is its context.
        Assert.Equal("VPN\nÚvodní odstavec před nadpisem.", chunks[0].Text);
        // Duplicate titles keep their own anchors in document order; the heading inside the code fence does not count.
        var first = chunks.Single(x => x.HeadingId == "pristup");
        Assert.Equal("Přístup", first.HeadingTitle);
        Assert.StartsWith("VPN › Přístup\nPřipojení přes VPN a schéma.", first.Text);
        Assert.Contains("# není nadpis, je v bloku kódu", first.Text);
        Assert.DoesNotContain("example.com", first.Text);
        Assert.Equal("VPN › Přístup\nDruhá sekce se stejným názvem.", chunks.Single(x => x.HeadingId == "pristup-1").Text);
        // A paragraph over the limit continues in the next chunk, which the following short paragraph then shares.
        var longChunks = chunks.Where(x => x.HeadingTitle == "Dlouhá").ToArray();
        Assert.Equal(2, longChunks.Length);
        Assert.EndsWith("slovo", longChunks[0].Text);
        Assert.EndsWith("\n\nKrátký odstavec za dlouhým.", longChunks[1].Text);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(x => x.Ordinal));
        // The embedded text carries the profile's document prefix; the hash is of exactly that text.
        Assert.All(chunks, chunk =>
        {
            Assert.Equal("passage: " + chunk.Text, chunk.EmbeddingText);
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.EmbeddingText)), chunk.TextHash);
            Assert.True(chunk.Text.Length <= 2600);
        });
    }

    [Fact]
    public void Short_paragraphs_share_a_chunk_and_an_empty_page_is_one_chunk_of_its_title()
    {
        var chunks = KnowledgeChunker.Chunk("Kontakty", "# Tým\nAlice\n\nBob\n\n\n", [new IndexHeading("Tým", "tym")], "");
        Assert.Equal("Kontakty › Tým\nAlice\n\nBob", Assert.Single(chunks).Text);

        var empty = Assert.Single(KnowledgeChunker.Chunk("Prázdná", "   \n", [], ""));
        Assert.Equal(("Prázdná", null, null), (empty.Text, empty.HeadingTitle, empty.HeadingId));
        Assert.Equal(KnowledgeChunker.TitleText("Prázdná", ""), empty.EmbeddingText);
    }

    [Fact]
    public void A_paragraph_over_the_limit_is_split_into_chunks_that_cover_all_of_it_exactly_once()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("slovo", 1000))[..5000].TrimEnd();
        var chunks = KnowledgeChunker.Chunk("Wiki", "## Sekce\n" + paragraph, [new IndexHeading("Sekce", "sekce")], "");

        Assert.Equal(3, chunks.Count);
        Assert.Equal(Enumerable.Range(0, 3), chunks.Select(x => x.Ordinal));
        Assert.All(chunks, chunk =>
        {
            Assert.StartsWith("Wiki › Sekce\n", chunk.Text);
            Assert.Equal("sekce", chunk.HeadingId);
            Assert.True(BodyOf(chunk).Length <= KnowledgeChunker.MaxBodyCharacters);
            Assert.True(chunk.Text.Length <= 2600);
        });
        Assert.Equal(paragraph, string.Join(' ', chunks.Select(BodyOf)));
    }

    [Fact]
    public void An_over_long_paragraph_is_split_at_a_sentence_boundary_when_one_is_in_reach()
    {
        var sentence = string.Join(' ', Enumerable.Repeat("slovo", 39)) + " slovo."; // 234 characters
        var paragraph = string.Join(' ', Enumerable.Repeat(sentence, 10));
        var chunks = KnowledgeChunker.Chunk("Wiki", "## Sekce\n" + paragraph, [new IndexHeading("Sekce", "sekce")], "");

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, chunk => Assert.EndsWith("slovo.", BodyOf(chunk)));
        Assert.All(chunks, chunk => Assert.StartsWith("slovo", BodyOf(chunk)));
        Assert.Equal(paragraph, string.Join(' ', chunks.Select(BodyOf)));
    }

    [Fact]
    public void A_word_longer_than_the_limit_is_hard_split_without_losing_a_character()
    {
        var word = new string('x', 4000);
        var chunks = KnowledgeChunker.Chunk("Wiki", "## Sekce\n" + word, [new IndexHeading("Sekce", "sekce")], "");

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.True(BodyOf(chunk).Length <= KnowledgeChunker.MaxBodyCharacters));
        Assert.Equal(word, string.Concat(chunks.Select(BodyOf)));
    }

    private static string BodyOf(KnowledgeChunkDraft chunk) => chunk.Text[(chunk.Text.IndexOf('\n') + 1)..];

    private static IEnumerable<string> Bodies(IEnumerable<KnowledgeChunkDraft> chunks, string? heading)
        => chunks.Where(x => x.HeadingTitle == heading).Select(x => x.Text[(x.Text.IndexOf('\n') + 1)..]);
}
