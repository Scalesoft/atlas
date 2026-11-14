namespace Atlas.Markdown.Sync;

/// <summary>A validated logical document identifier. It is never a filesystem path.</summary>
public sealed record DocumentId
{
    private DocumentId(string value) => Value = value;

    public string Value { get; }

    public static DocumentId Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 200)
            throw new FormatException("Document id must contain 1 to 200 characters.");
        if (value.Contains("..", StringComparison.Ordinal))
            throw new FormatException("Document id cannot contain '..'.");

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '-') continue;
            throw new FormatException("Document id contains an unsupported character.");
        }

        return new DocumentId(value);
    }

    public static bool TryParse(string? value, out DocumentId? documentId)
    {
        try
        {
            documentId = Parse(value ?? string.Empty);
            return true;
        }
        catch (FormatException)
        {
            documentId = null;
            return false;
        }
    }

    public override string ToString() => Value;
}