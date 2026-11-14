using System.Text.Json;
using System.Text.RegularExpressions;
using static Atlas.Markdown.Sync.JsValues;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Checks one attribute value: the value and the node's (or mark's) whole attrs object, returning the reason it is invalid or
/// null. Must be pure, thread-safe and linear in the value. <c>attrs</c> is undefined where the siblings cannot be relied on
/// (a collaborative attribute write, or an element merged from concurrent edits): then check what the value alone allows.
/// </summary>
public delegate string? MarkdownValueValidator(JsonElement value, JsonElement attrs);

/// <summary>
/// The server reading of a <c>MarkdownValueConstraint</c> of the editor's schema manifest (packages/markdown/src/schema/
/// constraints.ts documents every kind). Every pattern runs on the non-backtracking engine.
/// </summary>
internal sealed partial class ValueConstraint
{
    private ValueConstraint(string kind) => Kind = kind;

    public string Kind { get; }
    public bool Nullable { get; private init; }
    public string[] Values { get; private init; } = [];
    public double Minimum { get; private init; } = double.NegativeInfinity;
    public double Maximum { get; private init; } = double.PositiveInfinity;
    public int MinLength { get; private init; }
    public int MaxLength { get; private init; } = int.MaxValue;
    public bool SingleLine { get; private init; }
    public bool NonBlank { get; private init; }
    public Regex? Pattern { get; private init; }
    public string? Policy { get; private init; }
    public ImageSources? Images { get; private init; }
    public string? Validator { get; private init; }
    /// <summary>Sibling attributes the structured validator reads (its verdict depends on them).</summary>
    public string[] Context { get; private init; } = [];

    internal sealed record ImageSources(bool AllowRelative, string[]? AllowedOrigins, bool AllowDataImages, bool AllowBlobImages);

    public static ValueConstraint Parse(JsonElement json)
    {
        var kind = json.GetProperty("kind").GetString()!;
        double Number(string name, double fallback) => json.TryGetProperty(name, out var value) ? value.GetDouble() : fallback;
        int Length(string name, int fallback) => json.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;
        bool Flag(JsonElement owner, string name, bool fallback = false) => owner.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
        string[] Strings(JsonElement owner, string name) => owner.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
        if (kind is not ("unconstrained" or "enum" or "integer" or "number" or "boolean" or "text" or "dimension" or "url" or "structured"))
            throw new InvalidDataException($"Unknown value constraint kind '{kind}'.");
        var policy = json.TryGetProperty("policy", out var p) ? p.GetString() : null;
        if (kind == "url" && policy is not ("image" or "http" or "absolute-http" or "link"))
            throw new InvalidDataException($"Unknown URL policy '{policy}'.");
        return new ValueConstraint(kind)
        {
            Nullable = Flag(json, "nullable"),
            Values = Strings(json, "values"),
            Minimum = Number("minimum", double.NegativeInfinity),
            Maximum = Number("maximum", double.PositiveInfinity),
            MinLength = Length("minLength", 0),
            MaxLength = Length("maxLength", int.MaxValue),
            SingleLine = Flag(json, "singleLine"),
            NonBlank = Flag(json, "nonBlank"),
            Pattern = json.TryGetProperty("pattern", out var pattern) ? EcmaPattern(pattern.GetString()!) : null,
            Policy = policy,
            Images = json.TryGetProperty("imageSources", out var images)
                ? new ImageSources(Flag(images, "allowRelative", true), images.TryGetProperty("allowedOrigins", out _) ? Strings(images, "allowedOrigins") : null,
                    Flag(images, "allowDataImages", true), Flag(images, "allowBlobImages", true))
                : null,
            Validator = json.TryGetProperty("validator", out var validator) ? validator.GetString() : null,
            Context = Strings(json, "context"),
        };
    }

    /// <summary>
    /// An anchored ECMAScript pattern without flags. The manifest's patterns use character classes, quantifiers and anchors only;
    /// JavaScript's "$" (no multiline flag) is the end of input, .NET's "\z".
    /// </summary>
    private static Regex EcmaPattern(string pattern)
        => new(pattern.EndsWith('$') && !pattern.EndsWith("\\$", StringComparison.Ordinal) ? pattern[..^1] + "\\z" : pattern,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>The reason <paramref name="value"/> breaks the constraint, or null.</summary>
    public string? Check(JsonElement value, JsonElement attrs, IReadOnlyDictionary<string, MarkdownValueValidator> validators)
    {
        if (Nullable && value.ValueKind == JsonValueKind.Null && Kind != "structured") return null;
        var orNull = Nullable ? " or null" : "";
        switch (Kind)
        {
            case "unconstrained":
                return null;
            case "enum":
                return TryString(value, out var word) && Values.Contains(word, StringComparer.Ordinal) ? null : $"must be one of {string.Join(", ", Values)}{orNull}";
            case "integer":
                return IsInteger(value, out var integer) && integer >= Minimum && integer <= Maximum ? null : $"must be an integer from {Minimum} to {Maximum}{orNull}";
            case "number":
                return IsNumber(value, out var number) && number >= Minimum && number <= Maximum ? null : $"must be a number from {Minimum} to {Maximum}{orNull}";
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"must be true or false{orNull}";
            case "text":
                return TryString(value, out var text) && text.Length >= MinLength && text.Length <= MaxLength && (!SingleLine || !HasControl(text))
                    && (!NonBlank || !IsBlank(text)) && (Pattern is null || Pattern.IsMatch(text))
                    ? null
                    : $"must be {(NonBlank ? "non-blank " : "")}{(SingleLine ? "single-line " : "")}text"
                        + (MaxLength < int.MaxValue ? $" of {MinLength}–{MaxLength} characters" : "") + (Pattern is null ? "" : $" matching {Pattern}") + orNull;
            case "dimension":
                return value.ValueKind is JsonValueKind.Null || IsNumber(value, out _) || (TryString(value, out var size) && size.Length <= 100 && !HasControl(size))
                    ? null : "must be a number, a short text or null";
            case "url":
                return Url(value) is { } reason ? reason + orNull : null;
            default:
                return validators[Validator!](value, attrs);
        }
    }

    private string? Url(JsonElement value)
    {
        switch (Policy)
        {
            case "image":
                return ImageSourceError(value, Images);
            case "link":
                return TryString(value, out var href) && !ScriptLink().IsMatch(LinkWhitespace().Replace(href, ""))
                    ? null : "must be a link without a javascript:, vbscript: or data: scheme";
            case "http":
                return TryString(value, out var text) && !IsBlank(text) && text.Length <= MaxLength && !HasControl(text) && ModuleUrlError(JsTrim(text), optional: false) is null
                    ? null : "must be an http(s) or relative URL without credentials";
            default:
                return TryString(value, out var absolute) && !IsBlank(absolute) && absolute.Length <= MaxLength && !HasControl(absolute)
                    && TryParseUrl(absolute, relativeToEditorBase: false, out var url) && url.Protocol is "http:" or "https:" && !url.HasCredentials
                    ? null : "must be an absolute http(s) URL without credentials";
        }
    }

    [GeneratedRegex(@"^(?:javascript|vbscript|data):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptLink();

    /// <summary>Tiptap's ATTR_WHITESPACE, which its isAllowedUri removes before reading the scheme.</summary>
    [GeneratedRegex(@"[\x00-\x20  ᠎ -  　]", RegexOptions.CultureInvariant)]
    private static partial Regex LinkWhitespace();

    [GeneratedRegex(@"^data:image/(?:png|jpeg|gif|webp|avif);base64,[a-z0-9+/=" + JsWhitespace + "]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataImage();

    /// <summary>features/images/resourcePolicy.ts imageSource under the host's image source policy.</summary>
    public static string? ImageSourceError(JsonElement value, ImageSources? rules)
    {
        if (value.ValueKind == JsonValueKind.Null || (TryString(value, out var empty) && empty.Length == 0)) return null;
        const string invalid = "must be an http(s), relative or data:image source the image policy allows";
        if (!TryString(value, out var raw) || IsBlank(raw) || HasControl(raw) || raw.Contains('\\')) return invalid;
        var source = JsTrim(raw);
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return rules?.AllowDataImages != false && DataImage().IsMatch(source) ? null : invalid;
        if (source.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            // new URL("blob:…").origin is the origin of the inner http(s) URL, "null" otherwise.
            return rules?.AllowBlobImages != false && TryParseUrl(source[5..], relativeToEditorBase: false, out var inner) && inner.Protocol is "http:" or "https:"
                && AllowedOrigin(source[5..], rules) ? null : invalid;
        }
        var absolute = Scheme().IsMatch(source) || source.StartsWith("//", StringComparison.Ordinal);
        if (source.StartsWith("//", StringComparison.Ordinal) && rules?.AllowedOrigins is not null) return invalid;
        if (!TryParseUrl(source, relativeToEditorBase: true, out var url) || url.Protocol is not ("http:" or "https:") || url.HasCredentials) return invalid;
        if (!absolute && rules?.AllowRelative == false) return invalid;
        return !absolute || AllowedOrigin(source, rules) ? null : invalid;
    }

    [GeneratedRegex("^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Scheme();

    /// <summary>
    /// shortcut: the origin comes from System.Uri (lower-cased IDN host, default port dropped), close to but not exactly the
    /// WHATWG origin; a URL it reads differently is refused. Upgrade to a WHATWG host parser if a host relies on exotic hosts.
    /// </summary>
    private static bool AllowedOrigin(string absolute, ImageSources? rules)
    {
        if (rules?.AllowedOrigins is not { } origins) return true;
        if (!Uri.TryCreate(absolute.Trim(), UriKind.Absolute, out var uri)) return false;
        var origin = $"{uri.Scheme}://{uri.IdnHost}{(uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        return origins.Contains(origin, StringComparer.Ordinal);
    }

    /// <summary>markdownModuleUrl(value, optional) for a string value.</summary>
    public static string? ModuleUrlError(string value, bool optional)
    {
        if (optional && value.Length == 0) return null;
        return !IsBlank(value) && TryParseUrl(value, relativeToEditorBase: true, out var url) && url.Protocol is "http:" or "https:" && !url.HasCredentials
            ? null : "must be an http(s) or relative URL without credentials";
    }
}
