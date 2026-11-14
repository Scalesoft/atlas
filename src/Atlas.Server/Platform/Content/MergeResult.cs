namespace Atlas.Server.Platform.Content;

/// <summary>
/// A page written back from Markdown: the stored envelope (<see cref="ContentJson"/>) and the editor's Markdown of the same
/// document (<see cref="Markdown"/>, <see cref="EditorMarkdownWriter.Write"/>), both validated by <c>Rules.ValidateDocument</c>,
/// and what was converted on the way (raw HTML, images, unsafe or unavailable links, unresolved <c>[[…]]</c>).
/// </summary>
public sealed record MergeResult(string ContentJson, string Markdown, IReadOnlyList<string> Warnings);
