using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Validation;

/// <summary>
/// The editor schema of every content plugin the Atlas web registers: the editor package's built-in plugins plus Atlas's own
/// (<c>atlas-requirements</c>, Projector's requirement blocks). The web exports it to host-markdown-schema.json and fails its test
/// (src/Atlas.Web/src/hostDocumentSchema.test.ts) when the file drifts; every write path and the collaboration guard use this one validator.
/// </summary>
public static class HostDocumentValidator
{
    public static MarkdownDocumentValidator Instance { get; } = new(MarkdownSchemaManifest.Parse(Manifest()));

    private static string Manifest()
    {
        using var stream = typeof(HostDocumentValidator).Assembly.GetManifestResourceStream("Atlas.Server.Platform.Validation.host-markdown-schema.json")
            ?? throw new InvalidOperationException("The host editor schema is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
