using Atlas.Server.Features.Search.Domain;
using Xunit;
namespace Atlas.Server.Tests;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData("  PŘÍSTUPY  uživatelů ", "pristupy uzivatelu")]
    [InlineData("OpenID / .NET", "openid net")]
    [InlineData("\" OR 1=1 --", "or 1 1")]
    public void Normalization_removes_accents_and_query_syntax(string input, string expected) => Assert.Equal(expected, SearchText.Normalize(input));

    [Theory]
    [InlineData("C#", "csharp")]
    [InlineData("c sharp", "csharp")]
    [InlineData("C++", "cplusplus")]
    [InlineData("cpp", "cplusplus")]
    [InlineData("C++/CLI a C# na .NET", "cplusplus cli a csharp na net")]
    [InlineData("C++20 a C#12", "cplusplus20 a csharp12")]
    [InlineData("abc# abc++ cppcheck", "abc abc cppcheck")]
    public void Technical_identifiers_have_distinct_safe_search_lexemes(string input, string expected) => Assert.Equal(expected, SearchText.Normalize(input));

    [Fact]
    public void Programming_language_queries_are_searchable_and_distinct()
    {
        Assert.True(SearchText.IsSearchable(SearchText.Normalize("C#")));
        Assert.True(SearchText.IsSearchable(SearchText.Normalize("C++")));
        Assert.NotEqual(SearchText.Normalize("C#"), SearchText.Normalize("C++"));
    }

    [Fact]
    public void Prefix_query_uses_only_safe_lexemes() => Assert.Equal("openid:* & prist:*", SearchText.PrefixQuery("openid prist"));

    [Theory]
    [InlineData("\"HTTP 401\"", "(http <-> 401)", true)]
    [InlineData("obnova \"SQL server\"", "(obnova:* | obnov) & (sql <-> server)", true)]
    [InlineData("\"C#\"", "(csharp)", true)]
    [InlineData("NHibernate .NET", "(nhibernate:* | nhibernat) & net:*", false)]
    public void Quoted_phrases_keep_order_and_use_safe_normalized_lexemes(string input, string expected, bool phrase)
    {
        var result = SearchText.QueryExpression(input);
        Assert.Equal(expected,result.Expression);
        Assert.Equal(phrase,result.HasPhrase);
    }

    [Theory]
    [InlineData("smlouva", "smlouv")]
    [InlineData("smlouvy", "smlouv")]
    [InlineData("smlouvou", "smlouv")]
    [InlineData("smlouvami", "smlouv")]
    [InlineData("tiskarna", "tiskarn")]
    [InlineData("tiskarnu", "tiskarn")]
    [InlineData("tiskarnach", "tiskarn")]
    [InlineData("kola", "kola")]
    [InlineData("forum", "forum")]
    [InlineData("getfreebusy", "getfreebus")]
    public void Stem_drops_one_case_ending_and_keeps_four_letters(string word, string stem) => Assert.Equal(stem, SearchText.Stem(word));

    [Theory]
    [InlineData("Tiskárnu kolo", "(tiskarnu:* | tiskarn) & kolo:*")]
    [InlineData("\"tiskárnu\"", "(tiskarnu)")]
    public void Query_words_match_their_prefix_or_their_stem(string input, string expected) => Assert.Equal(expected, SearchText.QueryExpression(input).Expression);

    [Theory]
    [InlineData("Na co čekáme v KBN?", "(cekame:* | cekam) | kbn:*")]
    [InlineData("Kdo má dodat SMS bránu pro KBN?", "dodat:* | sms:* | (branu:* | bran) | kbn:*")]
    [InlineData("Na co čekáme", "(cekame:* | cekam)")]
    [InlineData("VPN", null)]
    [InlineData("vpn kbn", "vpn:* | kbn:*")]
    [InlineData("\"SMS brána\" KBN", null)]
    [InlineData("co je to", null)]
    public void A_query_relaxes_to_its_significant_words_ored(string input, string? expected) => Assert.Equal(expected, SearchText.AnyWordExpression(input));

    [Fact]
    public void Oversized_queries_are_rejected() => Assert.Throws<ArgumentException>(() => SearchText.Normalize(new string('a', 201)));

    [Fact]
    public void Snippet_finds_accented_hit_and_does_not_emit_html()
    {
        var body = new string('x', 250) + " <script>alert(1)</script> Přístupy do systému řeší správce.";
        var snippet = SearchText.Snippet(body, "pristupy");
        Assert.Contains("Přístupy", snippet);
        Assert.DoesNotContain("<", snippet);
        Assert.True(snippet.Length <= 222);
    }

    /// <summary>Review U16: a requirement line reads as its title and state in a hit, without the task marker and the key.</summary>
    [Fact]
    public void Snippet_and_passage_read_requirement_lines_as_title_and_state()
    {
        const string markdown = "## Balíček portál\n\n- [ ] **Doména** — neřešeno · naše práce · klíč: domena\n\n"
            + "- [x] **SSL certifikát** — hotovo 10. 9. 2026 · vstup · od: IT KBN · klíč: portal.ssl · závisí na: Doména\n  - [-] **Archiv** — netýká se · předání · klíč: a.b-c";
        const string expected = "Doména — neřešeno · naše práce SSL certifikát — hotovo 10. 9. 2026 · vstup · od: IT KBN · závisí na: Doména Archiv — netýká se · předání";

        Assert.Equal("…Doména — neřešeno · naše práce…", SearchText.Snippet(markdown, "domena"));
        Assert.Equal("Balíček portál " + expected, SearchText.Passage(markdown));
    }

    /// <summary>Review 3.6: a requirement heading's attribute block is syntax, whole or cut by the headline fragment at either end.</summary>
    [Fact]
    public void Snippet_drops_a_requirement_headings_attribute_block()
    {
        const string page = "### Dodatek smlouvy {#zakazka.dodatek state=done kind=inbound party=\"Jan Novák\"}\n\nPodepsaný dodatek je v archivu.";
        Assert.Equal("Dodatek smlouvy Podepsaný dodatek je v archivu.", SearchText.Snippet(page, "dodatek"));
        Assert.Equal("Dodatek smlouvy", SearchText.Snippet("### Dodatek smlouvy {#zakazka.dodatek state=done kind=inb", "dodatek"));
        Assert.Equal("Podepsaný dodatek.", SearchText.Snippet("done kind=inbound}\n\nPodepsaný dodatek.", "dodatek"));
    }

    /// <summary>Review U2-12: a hit in a requirement block shows that block's line alone, not the whole projection around it.</summary>
    [Fact]
    public void A_hit_in_a_requirement_shows_only_its_line()
    {
        const string markdown = "**Stav nasazení:** 50 % hotovo (1 z 2) · čeká na: Portál: SMS brána (IT KBN)\n\n## Portál\n\n"
            + "- [x] **Portál: SSL** — hotovo 1. 9. 2026 · vstup · klíč: portal.ssl\n- [ ] **Portál: SMS brána** — čeká na vstup · vstup · od: IT KBN · klíč: portal.sms\n"
            + "  Smlouva je u obchodu.\n\nVolný text stránky o bráně.";

        Assert.Equal("…Portál: SMS brána — čeká na vstup · vstup · od: IT KBN…", SearchText.Snippet(markdown, "brana"));
        Assert.Equal("…Portál: SSL — hotovo 1. 9. 2026 · vstup…", SearchText.Snippet(markdown, "ssl"));
        // The search's headline fragment may start inside a block line.
        Assert.Equal("SMS brána — čeká na vstup · vstup…", SearchText.Snippet("SMS brána** — čeká na vstup · vstup · klíč: portal.sms\n- [ ] **Další** — neřešeno", "brana"));
        // A hit outside a block keeps the text around it.
        Assert.StartsWith("…", SearchText.Snippet(markdown, "smlouva"));
        Assert.Contains("Smlouva je u obchodu. Volný text", SearchText.Snippet(markdown, "smlouva"));
    }

    /// <summary>Review U3-2: the search's headline of a word it cannot see (an accented one) is the page's start, on a requirement page its
    /// summary, whose derived line names the waiting blocks; such a hit is cut from the page's text, from the block that matched.</summary>
    [Fact]
    public void A_requirement_hit_snippets_the_matched_block_not_the_summary_that_names_it()
    {
        const string summary = "**Stav nasazení:** 33 % hotovo (1 z 3) · nedodané vstupy: SMS brána (IT KBN), Strukturované požadavky";
        const string page = summary + "\n\n## Portál\n\nÚvod stránky o bráně.\n\n"
            + "- [ ] **SMS brána** — čeká na vstup · vstup · od: IT KBN · klíč: portal.sms\n"
            + "- [ ] **Strukturované požadavky** — neřešeno · vstup · klíč: portal.requirements\n  Šablona od primářů.";
        const string headline = summary + "\n\n## Portál\n\nÚvod";

        Assert.True(SearchText.NeedsPageText(headline, "sms brana"));
        Assert.True(SearchText.NeedsPageText(headline, "strukturovane"));
        Assert.False(SearchText.NeedsPageText("Úvod stránky o SMS bráně", "sms"));
        Assert.False(SearchText.NeedsPageText(headline + " o SMS", "sms"));
        Assert.Equal("…SMS brána — čeká na vstup · vstup · od: IT KBN…", SearchText.Snippet(page, "sms brana"));
        Assert.Equal("…Strukturované požadavky — neřešeno · vstup…", SearchText.Snippet(page, "strukturovane"));
        // The summary is no candidate even where the page says the word only around it.
        Assert.DoesNotContain("Stav nasazení", SearchText.Snippet(summary + "\n\nText o SMS.", "sms"));
    }

    [Fact]
    public void Empty_and_short_queries_are_not_sent_to_database()
    {
        Assert.Equal("", SearchText.Normalize(" ### "));
        Assert.False(SearchText.IsSearchable("a"));
        Assert.True(SearchText.IsSearchable("db"));
    }

    [Fact]
    public void Passage_is_plain_text_of_at_most_320_characters_cut_at_a_word()
    {
        Assert.Equal("Krok jedna: spust klienta a over se telefonem.",
            SearchText.Passage("## Krok jedna:\n- spust **klienta** a `over` se :secret{vault=\"v\" secret=\"s\"} telefonem."));
        Assert.Equal("snake_case zustava", SearchText.Passage("> snake_case zustava"));
        var passage = SearchText.Passage(string.Join(' ', Enumerable.Repeat("slovo", 100)));
        Assert.True(passage.Length <= SearchText.PassageLength);
        Assert.EndsWith("slovo…", passage);
        Assert.Equal("", SearchText.Passage("  \n "));
    }

    /// <summary>An excerpt (a board's page card) is the page's readable text: no table pipes or separator rows, list markers,
    /// emphasis, link or image syntax, escapes or password references; cut at a word with an ellipsis.</summary>
    [Fact]
    public void An_excerpt_is_readable_plain_text_cut_at_a_word()
    {
        const string markdown = """
            # Tým

            Kontaktní osoby **projektu** a _jejich_ role, viz [příručka](https://example.com/guide "Příručka") a ![logo](/api/attachments/1/content).

            | Role | Jméno | Kontakt |
            | --- | :---: | ---: |
            | Vedoucí | Jana \*Nová\* | `jana@example.com` |

            - první bod
            * druhý bod
            1. třetí bod

            > Citace ::secret{secretId="00000000-0000-0000-0000-000000000001" field="password"} konec
            """;
        Assert.Equal("Tým Kontaktní osoby projektu a jejich role, viz příručka a logo. Role Jméno Kontakt Vedoucí Jana *Nová* jana@example.com "
            + "první bod druhý bod třetí bod Citace konec", SearchText.Excerpt(markdown, 1000));
        var cut = SearchText.Excerpt(markdown, 40);
        Assert.Equal("Tým Kontaktní osoby projektu a jejich…", cut);
        Assert.Equal("", SearchText.Excerpt("| --- | --- |", 300));
        // A hard line break (a backslash at the end of a line) is not text; an escaped backslash is.
        Assert.Equal("Pro vývojáře: agent a \\ b", SearchText.Excerpt("Pro vývojáře:\\\nagent a \\\\ b", 300));
    }

    /// <summary>UX SE3-6: a hit's snippet is readable text like a card's excerpt (NV3-3). On the real wiki „nasazení“ showed
    /// „…e5def-…/content) Postupy…“: the headline fragment opened with an image whose target the snippet window cut into.</summary>
    [Fact]
    public void A_snippet_shows_no_link_image_or_markdown_syntax()
    {
        var fragment = "![Modrá ilustrace prostředí a nasazování](/api/attachments/271e5def-8229-499f-8e7c-bd63a88348c4/content)\n\n"
            + "Postupy pro nové **nasazení** a aktualizaci, viz [návod](https://example.com/a/b \"Návod\"). Řádek\\\ndalší \\*hvězda\\*";
        var snippet = SearchText.Snippet(fragment, "nasazeni");
        Assert.Equal("Modrá ilustrace prostředí a nasazování Postupy pro nové nasazení a aktualizaci, viz návod. Řádek další *hvězda*", snippet);
        // A fragment cut inside a link keeps the link's text, never the rest of its target.
        Assert.Equal("Postupy pro nasazení", SearchText.Snippet("8e7c-bd63a88348c4/content) Postupy pro nasazení", "nasazeni"));
        Assert.Equal("Postupy pro nasazení, viz návod", SearchText.Snippet("Postupy pro nasazení, viz [návod](https://example.com/a", "nasazeni"));
        // A hit far into the text is centred without dragging syntax in.
        Assert.DoesNotContain("](", SearchText.Snippet(new string('x', 300) + " [odkaz](/api/pages/1) nasazení", "nasazeni"));
    }

    /// <summary>
    /// UX ED-12: a long page full of comparisons („a &lt; b“, code, math) never closes its „&lt;“; the tag pattern used to scan from every
    /// „&lt;“ to the end (quadratic: 300 KB took ~120 ms). Every text pattern stays linear on text that leaves its constructs open (tags,
    /// links, emphasis, directives, task markers). A ratio, not a time limit, so a busy machine cannot fail it: 4× the text must cost
    /// under 8× the time (linear ≈ 4×, the old quadratic tag pattern ≈ 16×). Each size keeps its fastest of interleaved rounds, which
    /// a load spike does not inflate.
    /// </summary>
    [Fact]
    public void Every_text_pattern_costs_linear_time()
    {
        var comparisons = string.Concat(Enumerable.Repeat("a < b ", 1_000));
        Assert.StartsWith("a < b", SearchText.Passage(comparisons));
        Assert.StartsWith("a < b", SearchText.Snippet(comparisons, "b"));
        Assert.StartsWith("a < b", SearchText.Excerpt(comparisons, 300));
        Assert.Equal("a b c", SearchText.Passage("a <b>b</b> c"));

        const string open = "a < b _x [y]( ::secret{ c++ c# \\* | x\n- [ ] **y z\n   \n";
        string Text(int units) => string.Concat(Enumerable.Repeat(open, units));
        static void Read(string text) { SearchText.Passage(text); SearchText.Snippet(text, "z"); SearchText.Excerpt(text, 300); }
        var (small, large) = (Text(6_000), Text(24_000));
        Read(small); Read(large);
        static void Fastest(string text, ref long best)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            Read(text);
            best = Math.Min(best, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
        var (bestSmall, bestLarge) = (long.MaxValue, long.MaxValue);
        for (var round = 0; round < 7; round++) { Fastest(small, ref bestSmall); Fastest(large, ref bestLarge); }
        var ratio = (double)bestLarge / bestSmall;
        Assert.True(ratio < 8, $"4× the text took {ratio:0.0}× the time ({bestSmall * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms → {bestLarge * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms)");
    }
}
