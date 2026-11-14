using Markdig.Syntax.Inlines;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>An Obsidian comment <c>%%hidden%%</c>, invisible in the source wiki's reading view.</summary>
public sealed class ObsidianCommentInline : LeafInline
{
    public required string Source { get; init; }
}
