using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// Converts CommonMark plus the GitHub, Obsidian and Wiki.js extensions into the editor's rich
/// document. Every construct maps to a node the Atlas editor registers (rich profile with the
/// diagrams, math and table-of-contents plugins) or degrades to readable text; each degradation is
/// reported per page with its first line. HTML never becomes executable markup. The stored Markdown
/// projection is written from the produced document by <see cref="Platform.Content.EditorMarkdownWriter"/>.
/// </summary>
public static partial class WikiMarkdownConverter
{
    public const string EndnotesTitle = "Poznámky pod čarou";

    private const int DiagramSourceLimit = 20_000;

    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough | EmphasisExtraOptions.Subscript | EmphasisExtraOptions.Superscript
            | EmphasisExtraOptions.Inserted | EmphasisExtraOptions.Marked)
        .UseTaskLists()
        .UseAutoLinks()
        .UseFootnotes()
        .UseMathematics()
        .UseDefinitionLists()
        .UseEmojiAndSmiley(false)
        .UseWikiSyntax()
        .Build();

    /// <summary>
    /// The editor heading identity of a page heading. <paramref name="key"/> stays stable when unrelated
    /// parts of the page change: a heading's slug with its occurrence, or a fixed name for a section the
    /// converter adds.
    /// </summary>
    internal static string HeadingId(string path, string key)
    {
        return "import-" + Hash(path + "#" + key)[..24];
    }

    public static ImportedDocument Convert(WikiSourcePage page, WikiPageLinks links)
    {
        return new WikiPageConversion(page, links, false).Run()!;
    }

    /// <summary>
    /// The headings the page will have, with their editor identities, for resolving links into it. Reads
    /// the structure only: no links, no Markdown projection and no document validation.
    /// </summary>
    public static IReadOnlyList<(string Text, string Id)> Outline(WikiSourcePage page)
    {
        var conversion = new WikiPageConversion(page, WikiPageLinks.Offline, true);
        conversion.Run();
        return conversion.Headings;
    }

    /// <summary>The plain text of a level-1 heading that opens the document, if there is one.</summary>
    public static string? LeadingHeading(string markdown)
    {
        var parsed = Markdig.Markdown.Parse(markdown, Pipeline);
        return parsed.FirstOrDefault() is HeadingBlock { Level: 1 } heading ? StripAttributes(PlainText(heading.Inline)).Trim() : null;
    }

    /// <summary>Mirrors the diagrams plugin: source is data, never HTML, init directives or front matter.</summary>
    public static bool IsValidDiagram(string source)
    {
        return source.Trim().Length > 0 && source.Length <= DiagramSourceLimit && !DiagramForbidden().IsMatch(source);
    }

    /// <summary>Removes a trailing Wiki.js attribute list such as <c>{.tabset}</c>.</summary>
    internal static string StripAttributes(string text)
    {
        return WikiJsTrailingAttribute().Replace(text, "");
    }

    internal static string PlainText(ContainerInline? container)
    {
        if (container is null)
        {
            return "";
        }
        var builder = new StringBuilder();
        foreach (var inline in container)
        {
            builder.Append(inline switch
            {
                LiteralInline literal => literal.Content.ToString(),
                CodeInline code => code.Content,
                LineBreakInline => " ",
                MathInline math => math.Content.ToString(),
                WikiLinkInline wiki => wiki.Alias ?? wiki.Target,
                HtmlEntityInline entity => entity.Transcoded.ToString(),
                ContainerInline child => PlainText(child),
                _ => "",
            });
        }
        return builder.ToString();
    }

    private static string Hash(string value)
    {
        return System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    [GeneratedRegex(@"%%\s*\{|^\s*---|</?[a-z!]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DiagramForbidden();

    // Non-backtracking: the leading whitespace run is unanchored and would otherwise be retried at every position.
    [GeneratedRegex(@"\s*\{\s*[.#][^{}]*\}\s*$", RegexOptions.NonBacktracking)]
    private static partial Regex WikiJsTrailingAttribute();
}
