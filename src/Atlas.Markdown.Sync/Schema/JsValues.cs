using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.Markdown.Sync;

/// <summary>
/// The JavaScript and WHATWG URL semantics the editor's attribute validators rely on (String.prototype.trim, Number.isInteger,
/// <c>new URL(value, base)</c>), so <see cref="MarkdownDocumentValidator"/> judges a value as the editor would. The URL reading covers
/// what the validators ask (the scheme, embedded credentials, an empty or malformed host, the origin); where it cannot tell, it refuses.
/// </summary>
internal static partial class JsValues
{
    /// <summary>JavaScript's WhiteSpace and LineTerminator: what trim() removes and \s matches.</summary>
    public const string JsWhitespace = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";

    private static readonly char[] JsWhitespaceChars =
        [.. new[] { 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a,
            0x2028, 0x2029, 0x202f, 0x205f, 0x3000, 0xfeff }.Select(code => (char)code)];

    public static string JsTrim(string value) => value.Trim(JsWhitespaceChars);

    public static bool IsBlank(string value) => JsTrim(value).Length == 0;

    /// <summary>A C0 control character or DEL (/[\u0000-\u001f\u007f]/).</summary>
    public static bool HasControl(string value) => value.Any(c => c < ' ' || c == '\u007f');

    /// <summary>A control character other than tab and line breaks (/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/).</summary>
    public static bool HasControlExceptBreaks(string value) => value.Any(c => (c < ' ' && c is not ('\t' or '\n' or '\r')) || c == '\u007f');

    public static bool HasLineBreak(string value) => value.Contains('\r') || value.Contains('\n');

    public static bool IsNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number);
    }

    /// <summary>Number.isInteger: 3 and 3.0 alike.</summary>
    public static bool IsInteger(JsonElement value, out double number) => IsNumber(value, out number) && Math.Floor(number) == number;

    /// <summary>Number.isSafeInteger.</summary>
    public static bool IsSafeInteger(JsonElement value, out double number) => IsInteger(value, out number) && Math.Abs(number) <= 9_007_199_254_740_991d;

    public static bool IsNumberEqual(JsonElement value, double expected) => IsNumber(value, out var number) && number == expected;

    public static bool TryString(JsonElement value, out string text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        return value.ValueKind == JsonValueKind.String;
    }

    /// <summary>A parsed URL as far as the editor's checks need it.</summary>
    public readonly record struct UrlParts(string Protocol, bool HasCredentials);

    private static readonly char[] ControlsAndSpace = Enumerable.Range(0, 0x21).Select(x => (char)x).ToArray();

    private static readonly string[] SpecialSchemes = ["http", "https", "ws", "wss", "ftp", "file"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant)]
    private static partial Regex Scheme();

    /// <summary>
    /// <c>new URL(value, base)</c> with the editor's base https://editor.invalid/ (or none): the protocol ("https:") and whether
    /// a username or password is embedded; false where the constructor would throw (or this reading cannot tell).
    /// </summary>
    public static bool TryParseUrl(string value, bool relativeToEditorBase, out UrlParts parts)
    {
        parts = default;
        // The URL parser strips leading and trailing C0 controls and spaces, and removes tabs and newlines anywhere.
        var input = value.Trim(ControlsAndSpace).Replace("\t", "").Replace("\n", "").Replace("\r", "");
        var scheme = Scheme().Match(input);
        if (!scheme.Success)
        {
            if (!relativeToEditorBase)
            {
                return false;
            }
            // Relative to an https base: "//host/…" (or with backslashes) names a host, anything else is a path.
            if (input.Length >= 2 && input[0] is ('/' or '\\') && input[1] is ('/' or '\\'))
            {
                return Authority(input.TrimStart('/', '\\'), "https:", out parts);
            }
            parts = new UrlParts("https:", false);
            return true;
        }
        var name = scheme.Value[..^1].ToLowerInvariant();
        var rest = input[scheme.Length..];
        var protocol = name + ":";
        if (!SpecialSchemes.Contains(name))
        {
            // Opaque or non-special URLs ("mailto:…"): credentials only behind an explicit authority.
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                var authority = rest[2..];
                var end = authority.IndexOfAny(['/', '?', '#']);
                parts = new UrlParts(protocol, Credentials(end < 0 ? authority : authority[..end]));
                return true;
            }
            parts = new UrlParts(protocol, false);
            return true;
        }
        if (name == "file")
        {
            parts = new UrlParts(protocol, false);
            return true;
        }
        // "https:foo" against the https base is a relative path; any other special scheme reads a host after optional slashes.
        if (relativeToEditorBase && name == "https" && rest.Length > 0 && rest[0] is not ('/' or '\\'))
        {
            parts = new UrlParts(protocol, false);
            return true;
        }
        return Authority(rest.TrimStart('/', '\\'), protocol, out parts);
    }

    private static bool Authority(string rest, string protocol, out UrlParts parts)
    {
        parts = default;
        var end = rest.IndexOfAny(['/', '\\', '?', '#']);
        var authority = end < 0 ? rest : rest[..end];
        var at = authority.LastIndexOf('@');
        var host = at < 0 ? authority : authority[(at + 1)..];
        var credentials = at >= 0 && Credentials(authority[..at]);
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            if (close < 0 || !Port(host[(close + 1)..]))
            {
                return false;
            }
            parts = new UrlParts(protocol, credentials);
            return true;
        }
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!Port(host[colon..]))
            {
                return false;
            }
            host = host[..colon];
        }
        // An empty host or a forbidden domain code point makes the constructor throw.
        if (host.Length == 0 || host.Any(c => c <= ' ' || c == '\u007f' || "#%/:<>?@[\\]^|".Contains(c)))
        {
            return false;
        }
        parts = new UrlParts(protocol, credentials);
        return true;
    }

    private static bool Credentials(string userinfo) => userinfo.Length > 0 && userinfo != ":";

    /// <summary>Empty, or ":" and a port number up to 65535 (leading zeros allowed, as the URL parser allows them).</summary>
    private static bool Port(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }
        var digits = value[1..];
        var significant = digits.TrimStart('0');
        return value[0] == ':' && digits.All(char.IsAsciiDigit)
            && (significant.Length < 5 || (significant.Length == 5 && int.Parse(significant, System.Globalization.CultureInfo.InvariantCulture) <= 65535));
    }
}
