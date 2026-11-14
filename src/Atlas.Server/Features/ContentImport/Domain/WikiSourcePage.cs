using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// A page as the export describes it. <paramref name="Path"/> is the extension-independent page
/// identity (always the Markdown path), so a page keeps its identity when the source wiki switches
/// editors. <paramref name="SourceFilePath"/> names the archive entry the page was read from and is
/// empty for organisational pages synthesised for export folders without a page of their own.
/// </summary>
public sealed record WikiSourcePage(
    string Path,
    string Title,
    string Markdown,
    string Sha256,
    IReadOnlyList<string> Warnings,
    string SourceFilePath = "")
{
    /// <summary>What reading the page (metadata, HTML-to-Markdown) already could not carry over.</summary>
    public IReadOnlyList<WikiImportFinding> Findings { get; init; } = [];

    /// <summary>Normalised tag names from the page metadata, applied as Atlas page tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Alternative names (Obsidian <c>aliases</c>) wiki links may use for this page.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The body starts with an H1 that became the page title; the converter omits it.</summary>
    public bool DropLeadingHeading { get; init; }

    /// <summary>Lines of the source file before <see cref="Markdown"/> (the metadata header), for report line numbers.</summary>
    public int LineOffset { get; init; }

    /// <summary><see cref="Markdown"/> was generated from an HTML page, so its line numbers do not point into the source file.</summary>
    public bool SourceIsHtml { get; init; }
}
