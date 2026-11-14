using Markdig.Syntax.Inlines;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary><c>[[Target|Alias]]</c> or the embed form <c>![[file.png]]</c> as written by Obsidian, GitHub and Wiki.js wikis.</summary>
public sealed class WikiLinkInline : LeafInline
{
    public required string Target { get; init; }

    public string? Alias { get; init; }

    public bool IsEmbed { get; init; }

    public required string Source { get; init; }
}
