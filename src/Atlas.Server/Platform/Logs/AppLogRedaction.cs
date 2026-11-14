using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Logs;

/// <summary>Hides what must never reach the stored log, before it is written: the values of passwords, secrets, tokens, keys and
/// authorization headers in any key=value or key: value form (connection strings, query strings, JSON), bearer and basic credentials,
/// the user and password of a URL, JWTs and known token shapes (Atlas's <c>sat_</c> and common providers').</summary>
public static partial class AppLogRedaction
{
    public const string Hidden = "***";

    public static string Redact(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        // The credential first: "Authorization: Bearer x" would otherwise lose only the word Bearer.
        text = Credential().Replace(text, match => match.Groups["scheme"].Value + " " + Hidden);
        text = Pair().Replace(text, match => match.Groups["key"].Value + Hidden);
        text = UrlUser().Replace(text, Hidden + "@");
        text = Jwt().Replace(text, Hidden);
        return Token().Replace(text, Hidden);
    }

    [GeneratedRegex("""(?<key>(?:password|passwd|pwd|secret|client[_-]?secret|(?:access|refresh|id|api|bearer|auth)?[_-]?token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|authorization|cookie|set-cookie)["']?\s*[=:]\s*)(?<value>"[^"]*"|'[^']*'|[^\s;,&"'}\]]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pair();

    [GeneratedRegex(@"(?<scheme>\b(?:Bearer|Basic))\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Credential();

    [GeneratedRegex(@"(?<=://)[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUser();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b(?:sat|ghp|gho|ghs|ghu|github_pat|glpat|xox[abpr]|sk)[_-][A-Za-z0-9_\-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
