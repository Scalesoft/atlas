using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Collaboration.Contracts;

/// <summary>Envelope: InvokeAsync&lt;T&gt; cannot carry a null result, and the bound schema is legitimately null.</summary>
public sealed record BoundSchema(MarkdownDocumentSchema? Schema);
