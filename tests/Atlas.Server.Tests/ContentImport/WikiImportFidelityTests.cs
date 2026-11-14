using System.Text.Json.Nodes;
using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.ContentImport.Domain;
using Xunit;

namespace Atlas.Server.Tests.ContentImport;

/// <summary>
/// Foreign-wiki Markdown must arrive as editor nodes the Atlas editor accepts, with every degraded
/// construct reported. The fixture pages under tests/fixtures/wiki-import are realistic GitHub,
/// Obsidian and Wiki.js exports; <see cref="WikiImportGoldenTests"/> pins their complete output.
/// </summary>
public sealed class WikiImportFidelityTests
{
    private static ImportedDocument Convert(string markdown, params (string Path, byte[] Bytes)[] files)
    {
        return ImportPages.Convert(markdown, files);
    }

    private static JsonNode Root(ImportedDocument result)
    {
        return JsonNode.Parse(result.ContentJson)!;
    }

    private static List<JsonNode> Nodes(JsonNode? node, string type)
    {
        var found = new List<JsonNode>();
        if (node is null)
        {
            return found;
        }
        if (node["type"]?.GetValue<string>() == type)
        {
            found.Add(node);
        }
        // The envelope holds the document; nodes nest through their content.
        if (node["document"] is JsonNode document)
        {
            found.AddRange(Nodes(document, type));
        }
        foreach (var child in node["content"] as JsonArray ?? [])
        {
            found.AddRange(Nodes(child, type));
        }
        return found;
    }

    private static string Text(JsonNode node)
    {
        return string.Concat(Nodes(node, "text").Select(x => x["text"]!.GetValue<string>()));
    }

    private static List<string> Marks(JsonNode text)
    {
        return (text["marks"] as JsonArray ?? []).Select(x => x!["type"]!.GetValue<string>()).ToList();
    }

    private static string Href(JsonNode root, string text)
    {
        return Nodes(root, "text").First(x => x["text"]!.GetValue<string>() == text)["marks"]!.AsArray()
            .Single(x => x!["type"]!.GetValue<string>() == "link")!["attrs"]!["href"]!.GetValue<string>();
    }

    private static string HeadingId(JsonNode root, string text)
    {
        return Nodes(root, "heading").Single(x => Text(x) == text)["attrs"]!["headingId"]!.GetValue<string>();
    }

    private static WikiImportFinding Finding(ImportedDocument result, string construct)
    {
        return Assert.Single(result.Findings, x => x.Construct == construct);
    }

    [Fact]
    public void Headings_keep_every_level_one_to_six_without_a_finding()
    {
        var result = Convert("# One\n\n#### Four\n\n##### Five\n\n###### Six");
        Assert.Equal([1, 4, 5, 6], Nodes(Root(result), "heading").Select(x => x["attrs"]!["level"]!.GetValue<int>()));
        Assert.Empty(result.Findings);
        Assert.StartsWith("# One\n\n#### Four\n\n##### Five\n\n###### Six", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Heading_identity_follows_the_heading_anchor_not_its_position()
    {
        var before = Root(Convert("## Setup\n\n## Usage\n\n## Usage"));
        var after = Root(Convert("## Intro\n\n## Setup\n\n## Usage\n\n## Usage"));
        Assert.Equal(HeadingId(before, "Setup"), HeadingId(after, "Setup"));
        var usages = Nodes(after, "heading").Where(x => Text(x) == "Usage").Select(x => x["attrs"]!["headingId"]!.GetValue<string>()).ToList();
        Assert.Equal(Nodes(before, "heading").Where(x => Text(x) == "Usage").Select(x => x["attrs"]!["headingId"]!.GetValue<string>()), usages);
        Assert.Equal(2, usages.Distinct().Count());
    }

    [Fact]
    public void Task_lists_keep_their_state_and_a_mixed_list_becomes_consecutive_lists()
    {
        var result = Convert("- [x] Done\n- [ ] Open\n  - [ ] Nested\n- Plain");
        var document = Root(result)["document"]!;
        Assert.Equal(["taskList", "bulletList"], (document["content"] as JsonArray)!.Take(2).Select(x => x!["type"]!.GetValue<string>()));
        var items = Nodes(document, "taskItem");
        Assert.Equal([true, false, false], items.Select(x => x["attrs"]!["checked"]!.GetValue<bool>()));
        Assert.Equal("Done", Text(items[0]));
        // The editor writes task lists loose (it has no tight flag for them).
        Assert.StartsWith("- [x] Done\n\n- [ ] Open\n\n  - [ ] Nested\n\n- Plain", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Emphasis_extras_use_editor_marks_and_keep_sub_and_superscript_notation()
    {
        var result = Convert("~~gone~~ ==marked== ++under++ H~2~O x^2^");
        var texts = Nodes(Root(result), "text");
        Assert.Equal(["strike"], Marks(texts.Single(x => x["text"]!.GetValue<string>() == "gone")));
        Assert.Equal(["highlight"], Marks(texts.Single(x => x["text"]!.GetValue<string>() == "marked")));
        Assert.Equal(["underline"], Marks(texts.Single(x => x["text"]!.GetValue<string>() == "under")));
        Assert.Contains("H~2~O x^2^", Text(Root(result)["document"]!), StringComparison.Ordinal);
        Assert.Equal(WikiImportFinding.Degraded, Finding(result, "subscript").Impact);
        Assert.Equal(WikiImportFinding.Degraded, Finding(result, "superscript").Impact);
        // The editor dialect writes highlight as ==text==; underline has no Markdown form and keeps its text.
        Assert.StartsWith("~~gone~~ ==marked== under H\\~2\\~O", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_are_stored_in_the_editor_schema_order()
    {
        var result = Convert("***[both](https://example.com)*** and <u>**under bold**</u>");
        var texts = Nodes(Root(result), "text");
        Assert.Equal(["link", "bold", "italic"], Marks(texts.Single(x => x["text"]!.GetValue<string>() == "both")));
        Assert.Equal(["bold", "underline"], Marks(texts.Single(x => x["text"]!.GetValue<string>() == "under bold")));
    }

    [Theory]
    [InlineData("==***a***b==", "***==a==***==b==")]
    [InlineData("==*<http://g>*.==", "[*==http://g==*](http://g)==.==")]
    public void A_mark_that_outlives_two_marks_opened_before_it_is_written_as_the_editor_writes_it(string markdown, string expected)
    {
        // The first text carries three marks and the next one only the last of them: the editor closes all three and opens
        // that one again (tests/fixtures/content/mark-order.json). The writer used to throw here, which failed the whole import.
        Assert.Equal(expected, Convert(markdown).Markdown);
    }

    [Fact]
    public void Math_keeps_the_latex_source_untouched()
    {
        var result = Convert("Inline $E = mc^2$.\n\n$$\n\\int_0^1 x^2 \\, dx\n$$\n\n$$\\frac{a}{b}$$");
        var root = Root(result);
        Assert.Equal("E = mc^2", Assert.Single(Nodes(root, "mathInline"))["attrs"]!["data"]!["expression"]!.GetValue<string>());
        Assert.Equal(["\\int_0^1 x^2 \\, dx", "\\frac{a}{b}"], Nodes(root, "mathBlock").Select(x => x["attrs"]!["data"]!["expression"]!.GetValue<string>()));
        Assert.Contains(root["plugins"]!.AsArray(), x => x!["id"]!.GetValue<string>() == "math" && x["version"]!.GetValue<int>() == 1);
        // The editor dialect writes math as $$ lines and $…$ with the untouched source.
        Assert.Contains("$$\n\\int_0^1 x^2 \\, dx\n$$\n\n$$\n\\frac{a}{b}\n$$", result.Markdown, StringComparison.Ordinal);
        Assert.StartsWith("Inline $E = mc^2$.", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Math_longer_than_the_plugin_limit_stays_text_and_is_reported()
    {
        var expression = new string('x', 10_001);
        var result = Convert("Before $" + expression + "$ after.\n\n$$\n" + expression + "\n$$");
        var root = Root(result);
        Assert.Empty(Nodes(root, "mathInline"));
        Assert.Empty(Nodes(root, "mathBlock"));
        Assert.Contains("$" + expression + "$", Text(root["document"]!), StringComparison.Ordinal);
        Assert.Equal("latex", Assert.Single(Nodes(root, "codeBlock"))["attrs"]!["language"]!.GetValue<string>());
        var finding = Finding(result, "math");
        Assert.Equal((WikiImportFinding.Degraded, 2, 1), (finding.Impact, finding.Count, finding.Line));
    }

    [Fact]
    public void Mermaid_becomes_a_diagram_unless_the_diagram_plugin_would_reject_it()
    {
        var result = Convert("```mermaid\nflowchart LR\n  A --> B\n```\n\n```mermaid\n%%{init: {'theme': 'dark'}}%%\nflowchart LR\n```\n\n```plantuml\n@startuml\n@enduml\n```");
        var root = Root(result);
        Assert.Equal("flowchart LR\n  A --> B", Assert.Single(Nodes(root, "diagram"))["attrs"]!["data"]!["source"]!.GetValue<string>());
        Assert.Contains(root["plugins"]!.AsArray(), x => x!["id"]!.GetValue<string>() == "diagrams" && x["version"]!.GetValue<int>() == 2);
        Assert.Equal(["mermaid", "plantuml"], Nodes(root, "codeBlock").Select(x => x["attrs"]!["language"]!.GetValue<string>()));
        Assert.Equal(6, Finding(result, "diagram-invalid").Line);
        Assert.Equal(WikiImportFinding.Degraded, Finding(result, "diagram-unsupported").Impact);
        // A diagram without a caption is a ```mermaid fence, like the rejected source kept as a code block.
        Assert.StartsWith("```mermaid\nflowchart LR\n  A --> B\n```\n\n```mermaid\n%%{init", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHub_and_Obsidian_alerts_and_WikiJs_classes_become_callouts()
    {
        var result = Convert("> [!NOTE]\n> Frozen on Fridays.\n\n> [!warning]- Budget risk\n> Over by **10 %**.\n\n> Pager duty.\n{.is-danger}\n\n> [!custom]\n> Sample.\n\n> Plain quote.");
        var callouts = Nodes(Root(result), "callout");
        Assert.Equal(["note", "warning", "caution", "note"], callouts.Select(x => x["attrs"]!["variant"]!.GetValue<string>()));
        Assert.Equal("Frozen on Fridays.", Text(callouts[0]));
        Assert.Equal(["bold"], Marks(Nodes(callouts[1], "text")[0]));
        Assert.Equal("Budget riskOver by 10 %.", Text(callouts[1]));
        Assert.Equal("Pager duty.", Text(callouts[2]));
        Assert.Single(Nodes(Root(result), "blockquote"));
        Assert.Equal(4, Finding(result, "callout-fold").Line);
        Assert.Equal("[!custom]", Finding(result, "callout-kind").Excerpt);
        // The editor writes a callout as a GitHub alert; a title stays its bold first paragraph (its projection
        // follows the editor exactly, see the golden files).
        Assert.StartsWith("> [!NOTE]\n> Frozen on Fridays.\n\n> [!WARNING]\n", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("\n\n> [!CAUTION]\n> Pager duty.\n\n", result.Markdown, StringComparison.Ordinal);
        Assert.EndsWith("> Plain quote.", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Footnotes_become_an_endnotes_section_linked_both_ways()
    {
        var page = new WikiSourcePage("page.md", "Page", "## Intro\n\nClaim[^a].\n\n[^a]: Source.", "hash", []);
        var id = Guid.NewGuid();
        var links = new WikiPageLinks((_, _) => LinkResolution.None, (_, _, _) => LinkResolution.None, heading => ImportLinkResolver.PageHref(id, heading));
        var result = WikiMarkdownConverter.Convert(page, links);
        var root = Root(result);
        var headings = Nodes(root, "heading");
        Assert.Equal(WikiMarkdownConverter.EndnotesTitle, Text(headings[^1]));
        var endnotes = headings[^1]["attrs"]!["headingId"]!.GetValue<string>();
        Assert.Equal(ImportLinkResolver.PageHref(id, endnotes), Href(root, "[1]"));
        Assert.Equal(ImportLinkResolver.PageHref(id, headings[0]["attrs"]!["headingId"]!.GetValue<string>()), Href(root, "↩"));
        Assert.Equal("Source. ↩", Text(Nodes(root, "orderedList").Single()));
        Assert.Equal(WikiImportFinding.Degraded, Finding(result, "footnote").Impact);
        Assert.Contains(WikiMarkdownConverter.Outline(page), x => x.Id == endnotes);
    }

    [Fact]
    public void Tables_keep_column_alignment_and_pad_short_rows()
    {
        var result = Convert("| A | B | C |\n|:--|--:|:-:|\n| 1 | 2 |\n| x | y | z |");
        var rows = Nodes(Root(result), "tableRow");
        Assert.All(rows, row => Assert.Equal(3, (row["content"] as JsonArray)!.Count));
        Assert.Equal(["tableHeader", "tableHeader", "tableHeader"], rows[0]["content"]!.AsArray().Select(x => x!["type"]!.GetValue<string>()));
        Assert.Equal(["left", "right", "center"], rows[1]["content"]!.AsArray().Select(x => x!["attrs"]!["textAlign"]!.GetValue<string>()));
        Assert.StartsWith("| A | B | C |\n| :--- | ---: | :---: |\n| 1 | 2 |  |\n| x | y | z |", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_over_the_editor_cell_limit_stays_readable_source_and_is_reported()
    {
        var header = "|" + string.Concat(Enumerable.Range(0, 10).Select(i => " c" + i + " |"));
        var rows = string.Concat(Enumerable.Range(0, 100).Select(_ => "\n|" + string.Concat(Enumerable.Repeat(" v |", 10))));
        var result = Convert(header + "\n|" + string.Concat(Enumerable.Repeat("---|", 10)) + rows);
        var root = Root(result);
        Assert.Empty(Nodes(root, "table"));
        Assert.StartsWith(header, Text(Assert.Single(Nodes(root, "codeBlock"))), StringComparison.Ordinal);
        var finding = Finding(result, "table-too-large");
        Assert.Equal((WikiImportFinding.Degraded, 1), (finding.Impact, finding.Line));
    }

    [Fact]
    public void Html_inside_markdown_becomes_breaks_marks_headings_and_resolved_images()
    {
        var result = Convert("One<br>two <kbd>Ctrl</kbd> <mark>hi</mark> H<sub>2</sub>O <span style=\"color:red\">red</span> <img src=\"pic.png\" alt=\"Pic\">\n\n"
            + "<details>\n<summary>More</summary>\n\nHidden **text**.\n\n</details>\n\n<!-- note -->", ("pic.png", ImportPages.Png));
        var root = Root(result);
        Assert.Single(Nodes(root, "hardBreak"));
        var texts = Nodes(root, "text");
        Assert.Equal(["code"], Marks(texts.Single(x => x["text"]!.GetValue<string>() == "Ctrl")));
        Assert.Equal(["highlight"], Marks(texts.Single(x => x["text"]!.GetValue<string>() == "hi")));
        var src = Assert.Single(Nodes(root, "image"))["attrs"]!["src"]!.GetValue<string>();
        Assert.Matches("^/api/attachments/[0-9a-f-]{36}/content$", src);
        // A <details> is a heading (Atlas has no disclosure: every heading folds), 3 with none before it, then its blocks.
        var heading = Assert.Single(Nodes(root, "heading"));
        Assert.Equal(("More", 3), (Text(heading), heading["attrs"]!["level"]!.GetValue<int>()));
        Assert.Empty(Nodes(root, "disclosure"));
        Assert.DoesNotContain("<", Text(root["document"]!), StringComparison.Ordinal);
        Assert.Equal(["kbd", "subscript", "html:span", "comment"], result.Findings.Select(x => x.Construct));
        Assert.Equal(WikiImportFinding.Lost, Finding(result, "comment").Impact);
        Assert.Contains("### More\n\nHidden **text**.", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_details_go_below_their_parent_and_consecutive_ones_stay_siblings()
    {
        var result = Convert("## Sekce\n\n<details>\n<summary>Vnější</summary>\n\nA\n\n<details>\n<summary>Vnitřní</summary>\n\nB\n\n</details>\n\n</details>\n\n"
            + "<details>\n<summary>Další</summary>\n\nC\n\n</details>");
        var headings = Nodes(Root(result), "heading").Select(x => (Text(x), x["attrs"]!["level"]!.GetValue<int>())).ToList();
        Assert.Equal([("Sekce", 2), ("Vnější", 3), ("Vnitřní", 4), ("Další", 3)], headings);
        Assert.Contains("### Vnější\n\nA\n\n#### Vnitřní\n\nB\n\n### Další\n\nC", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_script_and_style_are_dropped_with_their_content()
    {
        var result = Convert("Keep <script>alert(1)</script>this <style>p { color: red }</style>text <b>bold</b>.\n\nNext <input value=\"x\"> paragraph.");
        var paragraphs = Nodes(Root(result), "paragraph").Select(Text).Where(x => x.Length > 0).ToList();
        Assert.Equal(["Keep this text bold.", "Next  paragraph."], paragraphs);
        Assert.Equal(["html-dropped:script", "html-dropped:style", "html-dropped:input"], result.Findings.Select(x => x.Construct));
        Assert.All(result.Findings, x => Assert.Equal(WikiImportFinding.Lost, x.Impact));
    }

    [Fact]
    public void Obsidian_comments_spanning_blank_lines_are_dropped_as_a_whole()
    {
        var result = Convert("Before %%inline%% text.\n\n%%\nHidden plan.\n\n- hidden item\n\nStill hidden.\n%%\n\nAfter.\n\n%% never closed\n\nVisible.");
        var paragraphs = Nodes(Root(result), "paragraph").Select(Text).Where(x => x.Length > 0).ToList();
        Assert.Equal(["Before  text.", "After.", "%% never closed", "Visible."], paragraphs);
        Assert.Empty(Nodes(Root(result), "bulletList"));
        var finding = Finding(result, "comment");
        Assert.Equal((WikiImportFinding.Lost, 2, 1), (finding.Impact, finding.Count, finding.Line));
    }

    [Fact]
    public void Nesting_deeper_than_the_editor_supports_makes_the_page_unsupported_not_the_import()
    {
        var deep = ImportPages.Page("deep.md", string.Concat(Enumerable.Repeat("> ", 40)) + "too deep");
        var linking = ImportPages.Page("linking.md", "See [[deep]].");
        var (pages, unsupported) = ImportPages.ConvertAll([deep, linking]);
        Assert.Equal("linking.md", Assert.Single(pages).Source.Path);
        Assert.Contains("hluboce", Assert.Single(unsupported, x => x.Key == "deep.md").Value, StringComparison.Ordinal);
        // The left-out page is not linked: the link stays text and is reported.
        Assert.Equal(WikiImportFinding.Unresolved, Finding(pages[0].Document, "wiki-link").Impact);
    }

    [Fact]
    public void A_page_over_the_document_size_limit_is_left_out_and_the_rest_is_imported()
    {
        var huge = ImportPages.Page("huge.md", string.Concat(Enumerable.Repeat("a\n\n", 45_000)));
        var small = ImportPages.Page("small.md", "Link to [huge](huge.md).");
        var (pages, unsupported) = ImportPages.ConvertAll([huge, small]);
        Assert.Equal("small.md", Assert.Single(pages).Source.Path);
        Assert.Contains("Stránka je příliš velká: uložený obsah má", unsupported["huge.md"], StringComparison.Ordinal);
        Assert.DoesNotContain("/#/pages/", pages[0].Document.ContentJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_to_missing_headings_and_block_references_fall_back_to_the_page_and_are_reported()
    {
        var target = ImportPages.Page("target.md", "## Present");
        var page = ImportPages.Page("page.md", "[missing](target.md#absent) [present](target.md#present) [[target#^block-1|block]]");
        var result = ImportPages.Convert(page, [target]);
        var root = Root(result);
        var anchor = Finding(result, "anchor-unresolved");
        Assert.Equal((WikiImportFinding.Degraded, "target.md#absent"), (anchor.Impact, anchor.Excerpt));
        Assert.Equal(ImportPages.Href("target.md"), Href(root, "missing"));
        Assert.StartsWith(ImportPages.Href("target.md") + "?heading=import-", Href(root, "present"), StringComparison.Ordinal);
        var block = Finding(result, "block-reference");
        Assert.Equal((WikiImportFinding.Degraded, "[[target#^block-1|block]]"), (block.Impact, block.Excerpt));
        Assert.Equal(ImportPages.Href("target.md"), Href(root, "block"));
    }

    [Fact]
    public void Equally_good_page_matches_in_other_folders_are_reported_as_ambiguous()
    {
        var page = ImportPages.Page("notes/page.md", "[[Setup]] and [[Local]]");
        var result = ImportPages.Convert(page, [ImportPages.Page("a/Setup.md", "A"), ImportPages.Page("b/Setup.md", "B"), ImportPages.Page("notes/Local.md", "L"), ImportPages.Page("x/Local.md", "X")]);
        var root = Root(result);
        Assert.Equal(ImportPages.Href("a/Setup.md"), Href(root, "Setup"));
        // A same-folder page settles the choice.
        Assert.Equal(ImportPages.Href("notes/Local.md"), Href(root, "Local"));
        var ambiguous = Finding(result, "wiki-link-ambiguous");
        Assert.Equal((WikiImportFinding.Degraded, 1, "[[Setup]]"), (ambiguous.Impact, ambiguous.Count, ambiguous.Excerpt));
    }

    [Fact]
    public void GitHub_order_keeps_the_heading_and_both_readings_naming_different_pages_are_reported()
    {
        var runbook = ImportPages.Page("Runbook.md", "## Rollback");
        var home = ImportPages.Page("Home.md", "Home");
        var page = ImportPages.Page("page.md", "[[Undo steps|Runbook#Rollback]] and [[Home|Runbook]]");
        var result = ImportPages.Convert(page, [runbook, home]);
        var root = Root(result);
        var rollback = HeadingId(Root(ImportPages.ConvertAll([runbook]).Pages.Single().Document), "Rollback");
        Assert.Equal(ImportPages.Href("Runbook.md", rollback), Href(root, "Undo steps"));
        // [[Home|Runbook]] reads as a link to Home (Obsidian) or to Runbook (GitHub); both pages exist.
        Assert.Equal(ImportPages.Href("Home.md"), Href(root, "Runbook"));
        Assert.Equal("[[Home|Runbook]]", Finding(result, "wiki-link-ambiguous").Excerpt);
    }

    [Fact]
    public void Declared_plugins_are_registered_by_the_Atlas_editor_at_the_same_schema_version()
    {
        var host = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "host-document-schema.json")))!["plugins"]!.AsArray()
            .ToDictionary(x => x!["id"]!.GetValue<string>(), x => x!["version"]!.GetValue<int>());
        var result = Convert("[[_TOC_]]\n\n# A\n\n$x$\n\n```mermaid\nflowchart LR\n```");
        var declared = Root(result)["plugins"]!.AsArray();
        Assert.Equal(["diagrams", "heading-anchors", "math", "toc"], declared.Select(x => x!["id"]!.GetValue<string>()));
        Assert.All(declared, plugin => Assert.Equal(host[plugin!["id"]!.GetValue<string>()], plugin["version"]!.GetValue<int>()));
        Assert.StartsWith("Obsah dokumentu (automaticky podle nadpisů 1–3)\n\n# A", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_fixture_resolves_wiki_links_titles_and_heading_anchors()
    {
        var pages = await ImportPages.FixtureAsync("github");
        var guide = pages.Single(x => x.Source.Path == "Deployment-Guide.md");
        var runbook = pages.Single(x => x.Source.Path == "Runbook.md");
        Assert.Equal(("Deployment Guide", true), (guide.Source.Title, guide.Source.DropLeadingHeading));
        var root = Root(guide.Document);
        Assert.DoesNotContain(Nodes(root, "heading"), x => Text(x) == "Deployment Guide");
        Assert.Equal(ImportPages.Href("Home.md"), Href(root, "Home"));
        Assert.Equal(ImportPages.Href("Release-Process.md"), Href(root, "Release process"));
        Assert.Equal(ImportPages.Href("Runbook.md", HeadingId(Root(runbook.Document), "Rollback")), Href(root, "Runbook section"));
        Assert.Equal(ImportPages.Href("Deployment-Guide.md", HeadingId(root, "Rollback")), Href(root, "jump"));
        Assert.Equal(["images/architecture.png"], guide.Assets.Values.Select(x => x.Path));
        Assert.DoesNotContain(guide.Document.Findings, x => x.Impact == WikiImportFinding.Unresolved);
        // Its `#### Rollback` is a level 4 heading now, so only the footnote is degraded.
        Assert.Equal(["footnote"], guide.Document.Findings.Select(x => x.Construct));
    }

    [Fact]
    public async Task Obsidian_fixture_maps_front_matter_embeds_and_reports_unresolved_links()
    {
        var pages = await ImportPages.FixtureAsync("obsidian");
        var notes = pages.Single(x => x.Source.Path == "Meeting Notes.md");
        Assert.Equal(("Meeting Notes", true, 9), (notes.Source.Title, notes.Source.DropLeadingHeading, notes.Source.LineOffset));
        Assert.Equal(["meetings", "team/backend"], notes.Source.Tags);
        Assert.Equal(["Weekly sync"], notes.Source.Aliases);
        // The embed takes the file next to the page, not the one in images/.
        Assert.Equal(["whiteboard.png", "images/whiteboard.png"], notes.Assets.Values.Select(x => x.Path));
        var root = Root(notes.Document);
        Assert.Equal(2, Nodes(root, "image").Count);
        var risks = HeadingId(Root(pages.Single(x => x.Source.Path == "Project Alpha.md").Document), "Risks");
        Assert.Equal(ImportPages.Href("Project Alpha.md", risks), Href(root, "Project Alpha > Risks"));
        Assert.Contains("a missing Nonexistent Page.", Text(root["document"]!), StringComparison.Ordinal);
        var unresolved = Finding(notes.Document, "wiki-link");
        Assert.Equal((WikiImportFinding.Unresolved, 12, "[[Nonexistent Page]]"), (unresolved.Impact, unresolved.Line, unresolved.Excerpt));
        Assert.Equal(16, Finding(notes.Document, "embed-page").Line);
        Assert.Equal(WikiImportFinding.Lost, Finding(notes.Document, "comment").Impact);
        Assert.Equal("\\int_0^1 x^2 \\, dx", Assert.Single(Nodes(root, "mathBlock"))["attrs"]!["data"]!["expression"]!.GetValue<string>());
        Assert.Equal(["warning", "tip"], Nodes(root, "callout").Select(x => x["attrs"]!["variant"]!.GetValue<string>()));
    }

    [Fact]
    public async Task WikiJs_fixture_reports_each_degraded_construct_with_its_source_line()
    {
        var pages = await ImportPages.FixtureAsync("wikijs", "https://wiki.example.com/en");
        var policy = pages.Single(x => x.Source.Path == "ops/incident-policy.md");
        Assert.Equal(["incidents", "ops"], policy.Source.Tags);
        var root = Root(policy.Document);
        Assert.Single(Nodes(root, "tableOfContents"));
        Assert.Equal("warning", Assert.Single(Nodes(root, "callout"))["attrs"]!["variant"]!.GetValue<string>());
        Assert.Equal(3, policy.Assets.Count);
        Assert.Equal(2, Nodes(root, "image").Count);
        var matrix = HeadingId(Root(pages.Single(x => x.Source.Path == "ops/escalation.md").Document), "Matrix");
        Assert.Contains(Nodes(root, "text"), x => x["marks"]?[0]?["attrs"]?["href"]?.GetValue<string>() == ImportPages.Href("ops/escalation.md", matrix));
        Assert.DoesNotContain(policy.Document.Findings, x => x.Construct == "heading-level");
        Assert.Equal(41, Finding(policy.Document, "comment").Line);
        Assert.Equal(2, Finding(policy.Document, "kbd").Count);
        Assert.Equal(46, Finding(policy.Document, "diagram-unsupported").Line);
        Assert.Equal("https://wiki.example.com/en/home", Finding(policy.Document, "source-link-unresolved").Excerpt);
        Assert.Equal(30, Finding(policy.Document, "html-table-merged").Line);
    }

    [Fact]
    public async Task A_leading_h1_titles_an_untitled_page_and_is_dropped_when_it_repeats_the_title()
    {
        using var zip = ContentImportTests.Zip(("untitled.md", "# From heading\n\nBody"), ("titled.md", "---\ntitle: Same\n---\n\n# same\n\nBody"),
            ("different.md", "---\ntitle: Title\n---\n\n# Other heading\n\nBody"));
        var archive = await WikiJsArchive.ReadAsync(zip, WikiJsArchive.Validate(new WikiImportOptions("source", "Import")));
        var pages = archive.Pages.ToDictionary(x => x.Path);
        Assert.Equal(("From heading", true), (pages["untitled.md"].Title, pages["untitled.md"].DropLeadingHeading));
        Assert.Equal(("Same", true, 4), (pages["titled.md"].Title, pages["titled.md"].DropLeadingHeading, pages["titled.md"].LineOffset));
        Assert.Equal(("Title", false), (pages["different.md"].Title, pages["different.md"].DropLeadingHeading));
        var converted = ImportPages.ConvertAll([pages["untitled.md"]]).Pages.Single().Document;
        Assert.Empty(Nodes(Root(converted), "heading"));
        Assert.Equal("Body", converted.Markdown);
    }
}
