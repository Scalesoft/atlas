namespace Atlas.Markdown.Sync;

/// <summary>A content plugin's stable identity and persisted schema version.</summary>
public sealed record MarkdownSchemaPlugin(string Id, int Version);

/// <summary>Content compatibility only. Version identifies the built-in content schema, not the rich JSON envelope or deployment.</summary>
public sealed record MarkdownDocumentSchema(int Version, string Profile, IReadOnlyList<MarkdownSchemaPlugin> Plugins);

/// <summary>A permanent negotiation failure; clients must not retry with a weaker schema.</summary>
public sealed class DocumentSchemaException(string message) : Exception("[SCHEMA_MISMATCH] " + message);

public static class DocumentSchemaCompatibility
{
    public static MarkdownDocumentSchema Normalize(MarkdownDocumentSchema schema)
    {
        // Revisions 1-7 are recognized and preserved as declared; recognizing one does not make it compatible with another.
        if (schema.Version is not (1 or 2 or 3 or 4 or 5 or 6 or 7) || schema.Profile is not ("rich" or "markdown-safe") || schema.Plugins is null || schema.Plugins.Count > 128)
            throw new DocumentSchemaException("Unsupported document schema.");
        var plugins = schema.Plugins.ToArray();
        foreach (var plugin in plugins)
            if (plugin is null || string.IsNullOrWhiteSpace(plugin.Id) || plugin.Id.Length > 200 || plugin.Version < 1)
                throw new DocumentSchemaException("Invalid content plugin identity or version.");
        Array.Sort(plugins, (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        if (plugins.Select(plugin => plugin.Id).Distinct(StringComparer.Ordinal).Count() != plugins.Length)
            throw new DocumentSchemaException("Duplicate content plugin identity.");
        return schema with { Plugins = Array.AsReadOnly(plugins) };
    }

    /// <summary>Null is legacy mode, never an inferred modern profile.</summary>
    public static void EnsureCompatible(MarkdownDocumentSchema? stored, MarkdownDocumentSchema? requested)
    {
        if (stored is null && requested is null) return;
        if (stored is null || requested is null)
            throw new DocumentSchemaException("Legacy and versioned documents require an explicit migration before changing modes.");
        stored = Normalize(stored);
        requested = Normalize(requested);
        if (stored.Version != requested.Version || stored.Profile != requested.Profile || !stored.Plugins.SequenceEqual(requested.Plugins))
            throw new DocumentSchemaException("The document requires a different built-in schema revision, profile or content plugin schema.");
    }
}
