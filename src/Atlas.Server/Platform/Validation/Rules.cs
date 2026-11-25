using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Platform.Validation;

/// <summary>Pure validators shared by every feature. No state, no database, no DI.</summary>
public static class Rules
{
    /// <summary>
    /// A required name or title: trimmed, at most <paramref name="max"/> characters, one line without control characters or
    /// bidirectional controls (a line break or tab breaks the tree, search results, Git backup paths and audit rows that show it;
    /// a bidi control makes the text read other than it is). <paramref name="unchanged"/> is the stored value: sent back as it is
    /// (a web save of a page whose legacy title predates the rule), it still saves; only a new or changed value is refused.
    /// </summary>
    public static string Required(string? value, string field, int max = 200, string? unchanged = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max)
        {
            throw new ApiException(400, "Pole {field} je povinné a smí mít nejvýše {max} znaků.", args: new Dictionary<string, object?> { ["field"] = field, ["max"] = max });
        }
        var text = value.Trim();
        if (text != unchanged && !IsLine(text))
        {
            throw new ApiException(400, "Pole {field} smí mít jen jeden řádek bez řídicích znaků.", args: new Dictionary<string, object?> { ["field"] = field });
        }
        return text;
    }

    /// <summary>
    /// An optional text of several lines (a description, a note): trimmed, blank means null, at most <paramref name="max"/>
    /// characters; line breaks are kept, every other control character and bidirectional control is refused. As in
    /// <see cref="Required"/>, an <paramref name="unchanged"/> stored value still saves.
    /// </summary>
    public static string? OptionalText(string? value, string field, int max, string? unchanged = null)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.Length > max)
        {
            throw new ApiException(400, "{field} smí mít nejvýše {max} znaků.", args: new Dictionary<string, object?> { ["field"] = field, ["max"] = max });
        }
        if (text != unchanged && !IsMultiLineText(text))
        {
            throw new ApiException(400, "{field} nesmí obsahovat řídicí znaky.", args: new Dictionary<string, object?> { ["field"] = field });
        }
        return text;
    }

    /// <summary>The character rule of <see cref="Required"/> and <see cref="OptionalLine"/>: no control character (so no line
    /// break, tab or NUL) and no bidirectional control.</summary>
    public static bool IsLine(string text) => !text.Any(c => char.IsControl(c) || IsBidiControl(c));

    /// <summary>The character rule of <see cref="OptionalText"/>: line breaks (\n, \r) are kept, every other control character
    /// (NUL included, which PostgreSQL text refuses) and every bidirectional control is refused.</summary>
    public static bool IsMultiLineText(string text) => !text.Any(c => (char.IsControl(c) && c is not ('\n' or '\r')) || IsBidiControl(c));

    /// <summary>Text synced from another system (an identity provider, a password file): control characters and bidirectional
    /// controls are removed instead of refusing the sync, and whitespace runs collapse to one space.</summary>
    public static string CleanLine(string text) =>
        string.Join(' ', new string(text.Select(c => char.IsControl(c) ? ' ' : c).Where(c => !IsBidiControl(c)).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The Unicode bidirectional controls: ALM, LRM, RLM, LRE…RLO and LRI…PDI.</summary>
    public static bool IsBidiControl(char c) => c is '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    /// <summary>An optional single-line text: trimmed, blank means null; control characters (so line breaks) and anything over
    /// <paramref name="max"/> characters are refused. The change summary of a publication; the database check mirrors it.</summary>
    public static string? OptionalLine(string? value, string field, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        if (text.Length > max || !IsLine(text))
        {
            throw new ApiException(400, "{field} smí mít jeden řádek a nejvýše {max} znaků.", args: new Dictionary<string, object?> { ["field"] = field, ["max"] = max });
        }
        return text;
    }

    /// <summary>The web editor's wording; <see cref="ForApiClient"/> words it for a client without an editor (REST, MCP).</summary>
    public const string VersionConflictMessage = "Stránku mezitím změnil jiný uživatel. Vaše změny zůstávají v editoru; načtěte aktuální verzi před dalším uložením.";

    public static string ForApiClient(string message) => message == VersionConflictMessage
        ? "Stránku mezitím změnil jiný uživatel. Načtěte aktuální verzi stránky znovu a změnu zopakujte s jejím novým expectedVersion."
        : message;

    public static void ExpectVersion(int current, int expected)
    {
        if (!IsExpectedVersion(current, expected))
        {
            throw new ApiException(409, VersionConflictMessage, "version_conflict");
        }
    }

    /// <summary>The expected version is the current one, or the one the agent of a live write read (<see cref="LiveWriteBase"/>).</summary>
    public static bool IsExpectedVersion(int current, int expected) => current == expected || LiveWriteBase.Accepts(current, expected);

    /// <summary>Accent colours of the interface (palettes.css, profile.ts themeAccents); calm tones only.</summary>
    private static readonly string[] AppearanceAccents = ["blue", "green", "red", "ocean", "sunset", "indigo", "violet", "amber"];

    public static void ValidateAppearance(Appearance value)
    {
        if (value.Layout is not ("connected" or "islands") || !AppearanceAccents.Contains(value.Accent, StringComparer.Ordinal) || value.Mode is not ("system" or "light" or "dark")
            || value.Motion is not ("system" or "reduce") || value.Language is not ("auto" or "cs" or "en")
            || value.CollaboratorColor != "auto" && Atlas.Markdown.Sync.CollaboratorPalette.SlotOf(value.CollaboratorColor) < 0)
        {
            throw new ApiException(400, "Nepodporované nastavení vzhledu.");
        }
    }

    /// <summary>A grant being written must end in the future. Callers apply it to a new grant and to changed bounds only: an
    /// unchanged stored end that has already passed is kept (the no-op rule, or a role change that resends the stored bounds).</summary>
    public static void RequireFutureEnd(DateTime? expiresAt, DateTime utcNow)
    {
        if (expiresAt is { } end && end <= utcNow)
        {
            throw new ApiException(400, "Konec platnosti už uplynul. Zvolte datum a čas v budoucnosti.");
        }
    }

    /// <summary>Stored page content limits in characters (string.Length, as JavaScript counts them). The editor JSON of large
    /// tables is many times its Markdown, so the JSON limit is usually met first. src/Atlas.Web/src/savePage.ts mirrors both.</summary>
    public const int MaxDocumentJsonLength = 2_000_000;
    public const int MaxDocumentMarkdownLength = 1_000_000;

    /// <summary>The rich envelope rules, including the editor schema (Atlas.Markdown.Sync's <see cref="MarkdownDocumentValidator"/>); returns the secret ids
    /// its password references name (WP4 V6). A reference that breaks the
    /// plugin contract (for example an inline value), in the JSON or as a Markdown directive, is refused, and so is a directive the
    /// JSON does not hold. Whether the author may use the ids is the caller's check
    /// (SecretReferenceAccess), since only it knows the account.</summary>
    public static IReadOnlySet<Guid> ValidateDocument(string? json, string? markdown)
    {
        if (json is null || markdown is null)
        {
            throw new ApiException(400, "Dokument neobsahuje obsah editoru nebo Markdown.");
        }
        RequireMarkdownSize(markdown);
        if (json.Length > MaxDocumentJsonLength)
        {
            throw new ApiException(400, "Stránka je příliš velká: uložený obsah má {size}, limit je {limit}. Rozdělte velké tabulky do více stránek.", "document_too_large",
                args: new Dictionary<string, object?> { ["size"] = Megabytes(json.Length), ["limit"] = Megabytes(MaxDocumentJsonLength) });
        }
        try
        {
            using var document = JsonDocument.Parse(json, MarkdownDocumentValidator.JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || !MarkdownDocumentValidator.IsFormat(format.GetString())
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("document", out var content) || content.ValueKind != JsonValueKind.Object
                || !content.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "doc")
            {
                throw new JsonException();
            }
            var referenced = SecretReferenceContent.Collect(root);
            // The Markdown projection (editor 0.6.0 directives) may name only what the JSON holds, so the JSON checks cover it.
            SecretReferenceMarkdown.RequireWithin(markdown, referenced);
            // Every node, mark and attribute as the pinned editor accepts them (400 invalid_document with the path): an API,
            // MCP or import client must never store a document the editor then refuses to open.
            if (HostDocumentValidator.Instance.Validate(root) is { } violation)
            {
                throw EditorDocumentErrors.Error(violation);
            }
            return referenced;
        }
        catch (InvalidDataException)
        {
            throw new ApiException(400, "Odkaz na heslo v dokumentu není platný.", "invalid_secret_reference");
        }
        catch (JsonException)
        {
            throw new ApiException(400, "Obsah editoru není platný JSON dokument, nebo je zanořen hlouběji než {maxDepth} úrovní uzlů.", args: new Dictionary<string, object?> { ["maxDepth"] = MarkdownDocumentValidator.MaxDepth });
        }
    }

    /// <summary>
    /// <see cref="ValidateDocument(string?, string?)"/> for content a client or an archive writes: the server owns the Markdown
    /// projection. Search, export, backups, the AI index and the API read it, so it is written here from the validated JSON with
    /// <see cref="EditorMarkdownWriter"/> (the editor's serializer, golden-tested against it) and never taken from the client.
    /// </summary>
    public static (IReadOnlySet<Guid> Referenced, string Markdown) ValidateDocument(string? json)
    {
        // "" names no directive and fits the limit, so only the JSON is checked, and the writer only sees a document it accepts.
        var referenced = ValidateDocument(json, "");
        var markdown = EditorMarkdownWriter.Write(RichDocument.Parse(json!).Document);
        RequireMarkdownSize(markdown);
        return (referenced, markdown);
    }

    /// <summary>Which limit, how far over it and what helps: the save error is the only place the author learns it.</summary>
    public static void RequireMarkdownSize(string markdown)
    {
        if (markdown.Length > MaxDocumentMarkdownLength)
        {
            throw new ApiException(400, "Stránka je příliš velká: text stránky má {size}, limit je {limit}. Rozdělte obsah do více stránek.", "document_too_large",
                args: new Dictionary<string, object?> { ["size"] = Megabytes(markdown.Length), ["limit"] = Megabytes(MaxDocumentMarkdownLength) });
        }
    }

    /// <summary>"2,3 MB" (millions of characters, Czech decimal comma), rounded up so that a size over a limit never reads as the limit.</summary>
    private static string Megabytes(int characters)
    {
        var tenths = (characters + 99_999) / 100_000;
        return tenths % 10 == 0 ? $"{tenths / 10} MB" : $"{tenths / 10},{tenths % 10} MB";
    }
}
