namespace Atlas.Server.Platform.Content;

/// <summary>
/// A page as Markdown for a client that edits text (MCP, phase 4 E5): text blocks in the editor's dialect, every other
/// node a numbered placeholder <c>⟦c:N⟧</c> described in <see cref="Components"/>, and the heading sections with their
/// output lines. <see cref="MarkdownEditable"/>: the whole text fits the output limit, so one read gives what a replacement of the
/// whole page (placeholders included) needs. Built only by <see cref="RichMarkdownProjection.Project"/>.
/// </summary>
public sealed record ContentProjection(string Markdown, IReadOnlyList<ContentSection> Sections,
    IReadOnlyList<ComponentSlot> Components, bool MarkdownEditable, IReadOnlyList<string> Warnings)
{
    /// <summary>The original node behind each placeholder, for the merge back; never part of any output.</summary>
    internal IReadOnlyList<SlotBinding> Bindings { get; init; } = [];

    /// <summary>The projected top-level blocks in document order, for the merge back; never part of any output.</summary>
    internal IReadOnlyList<ProjectedBlock> Blocks { get; init; } = [];
}
