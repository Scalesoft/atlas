using Xunit;
using Atlas.Server.Features.ContentImport.Domain;
namespace Atlas.Server.Tests;

public class WikiHtmlToMarkdownTests
{
    [Fact]
    public void Converts_headings_prose_lists_and_code_language()
    {
        var result = WikiHtmlToMarkdown.Convert(
            "<h1>Titulek</h1><p>Text s <strong>tučným</strong> slovem.</p><ul><li>První</li><li>Druhý</li></ul>"
            + "<pre><code class=\"language-csharp\">var a = 1;</code></pre>");
        Assert.Equal("# Titulek\n\nText s **tučným** slovem.\n\n- První\n- Druhý\n\n```csharp\nvar a = 1;\n```", result.Markdown);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Indents_nested_lists_under_their_item()
    {
        var result = WikiHtmlToMarkdown.Convert("<ul><li>Nadřazená<ul><li>Vnořená</li></ul></li><li>Druhá</li></ul>");
        Assert.Equal("- Nadřazená\n  - Vnořená\n- Druhá", result.Markdown);
    }

    [Fact]
    public void Converts_table_with_header_cells_to_a_pipe_table()
    {
        var result = WikiHtmlToMarkdown.Convert(
            "<table><thead><tr><th>Jméno</th><th>Role</th></tr></thead><tbody><tr><td>Martin</td><td>Vývoj</td></tr></tbody></table>");
        Assert.Equal("| Jméno | Role |\n| --- | --- |\n| Martin | Vývoj |", result.Markdown);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Reports_a_table_without_header_cells_and_uses_its_first_row()
    {
        var result = WikiHtmlToMarkdown.Convert("<table><tr><td>Jméno</td></tr><tr><td>Martin</td></tr></table>");
        Assert.Equal("| Jméno |\n| --- |\n| Martin |", result.Markdown);
        Assert.Contains(result.Findings, x => x.Construct == "html-table-header");
    }

    [Fact]
    public void Reports_merged_table_cells_instead_of_dropping_them()
    {
        var result = WikiHtmlToMarkdown.Convert("<table><tr><th colspan=\"2\">Souhrn</th></tr><tr><td>a</td><td>b</td></tr></table>");
        Assert.Contains("Souhrn", result.Markdown, StringComparison.Ordinal);
        Assert.Contains(result.Findings, x => x.Construct == "html-table-merged");
    }

    [Fact]
    public void Drops_interactive_markup_and_names_it_in_a_warning()
    {
        var result = WikiHtmlToMarkdown.Convert(
            "<p>Zůstává</p><script>alert('x')</script><form><label>Jméno</label><input value=\"tajné\"><select><option>Volba</option></select></form>");
        Assert.Equal("Zůstává", result.Markdown);
        Assert.DoesNotContain("alert", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Volba", result.Markdown, StringComparison.Ordinal);
        Assert.Contains(result.Findings, x => x.Construct == "html-dropped:script" && x.Message.Contains("skript", StringComparison.Ordinal));
        Assert.Contains(result.Findings, x => x.Construct == "html-dropped:form");
    }

    [Fact]
    public void Keeps_links_and_images_but_strips_script_urls()
    {
        var result = WikiHtmlToMarkdown.Convert(
            "<p><a href=\"other.html\">Odkaz</a> <a href=\"javascript:alert(1)\">Zlý</a> <img src=\"assets/a.png\" alt=\"Popis\"></p>");
        Assert.Contains("[Odkaz](other.html)", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("Zlý", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("![Popis](assets/a.png)", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Converts_blockquote_and_horizontal_rule()
    {
        var result = WikiHtmlToMarkdown.Convert("<blockquote><p>Citace</p></blockquote><hr><p>Za linkou</p>");
        Assert.Equal("> Citace\n\n---\n\nZa linkou", result.Markdown);
    }

    [Fact]
    public void Unwraps_layout_elements_and_normalises_non_breaking_space()
    {
        var result = WikiHtmlToMarkdown.Convert("<div class=\"row\"><span>Text&nbsp;s mezerou</span></div>");
        Assert.Equal("Text s mezerou", result.Markdown);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Yields_empty_markdown_when_only_unconvertible_markup_remains()
    {
        var result = WikiHtmlToMarkdown.Convert("<form><input value=\"a\"><button>Poslat</button></form>");
        Assert.Equal("", result.Markdown);
        Assert.Contains(result.Findings, x => x.Construct == "html-dropped:form");
    }

    [Fact]
    public void Maps_inline_semantics_to_editor_marks_and_reports_what_has_none()
    {
        var result = WikiHtmlToMarkdown.Convert("<p><mark>Pozor</mark> <u>podtržení</u> <kbd>Ctrl</kbd> H<sub>2</sub>O <span style=\"color:red\">červeně</span></p>");
        Assert.Equal("==Pozor== ++podtržení++ `Ctrl` H2O červeně", result.Markdown);
        Assert.Equal(["kbd", "subscript", "html-style"], result.Findings.Select(x => x.Construct));
    }

    [Fact]
    public void Keeps_top_level_inline_formatting()
    {
        Assert.Equal("**Keep me**", WikiHtmlToMarkdown.Convert("<b>Keep me</b>").Markdown);
    }

    [Fact]
    public void Emits_details_callout_classes_and_column_alignment_in_the_form_the_converter_reads()
    {
        var result = WikiHtmlToMarkdown.Convert("<details><summary>Více &amp; méně</summary><p>Skryté</p></details>"
            + "<blockquote class=\"is-warning\"><p>Pozor</p></blockquote>"
            + "<table><tr><th style=\"text-align: right\">Číslo</th><th align=\"center\">Stav</th></tr><tr><td>1</td><td>OK</td></tr></table>");
        Assert.Equal("<details>\n<summary>Více &amp; méně</summary>\n\nSkryté\n\n</details>\n\n> [!WARNING]\n> Pozor\n\n| Číslo | Stav |\n| ---: | :---: |\n| 1 | OK |", result.Markdown);
        Assert.DoesNotContain(result.Findings, x => x.Construct != "html-style");
    }

    [Fact]
    public void Reports_markup_nested_deeper_than_32_levels_instead_of_dropping_it_silently()
    {
        var html = string.Concat(Enumerable.Repeat("<blockquote>", 40)) + "<p>Hluboko</p>" + string.Concat(Enumerable.Repeat("</blockquote>", 40)) + "<p>Mělce</p>";
        var result = WikiHtmlToMarkdown.Convert(html);
        Assert.Contains("Mělce", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Hluboko", result.Markdown, StringComparison.Ordinal);
        Assert.Equal("lost", Assert.Single(result.Findings, x => x.Construct == "html-depth").Impact);
    }
}
