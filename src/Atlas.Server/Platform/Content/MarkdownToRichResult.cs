using System.Text.Json.Nodes;

namespace Atlas.Server.Platform.Content;

/// <summary>The top-level blocks parsed from Markdown and what was converted on the way (raw HTML, unsafe links, images).</summary>
public sealed record MarkdownToRichResult(JsonArray Blocks, IReadOnlyList<string> Warnings);
