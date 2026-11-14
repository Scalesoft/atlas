using Microsoft.Net.Http.Headers;

namespace Atlas.Server.Platform.Localization;

/// <summary>
/// The language of the tool for one request (Atlas 4.1): the account's choice, which the web mirrors in the cookie
/// <c>atlas-language</c> (src/Atlas.Web/src/i18n), else the first language of Accept-Language (Czech for cs, English for any
/// other), else Czech. Resolved once per request. Only texts the server shows to people follow it (<see cref="ErrorTexts"/>); MCP
/// and AI texts, logs, exports, backups and the tree order (TitleOrder, cs-CZ for everyone) do not.
/// </summary>
public static class RequestLanguage
{
    public const string Czech = "cs";
    public const string English = "en";
    public const string Cookie = "atlas-language";

    private static readonly object Key = new();

    public static string Of(HttpContext context)
    {
        if (context.Items.TryGetValue(Key, out var known) && known is string language)
        {
            return language;
        }
        language = Resolve(context.Request);
        context.Items[Key] = language;
        return language;
    }

    private static string Resolve(HttpRequest request)
    {
        if (request.Cookies[Cookie] is Czech or English)
        {
            return request.Cookies[Cookie]!;
        }
        // Never throws: a malformed header is no header. A stable sort keeps the browser's order among equal weights.
        if (!StringWithQualityHeaderValue.TryParseList(request.Headers.AcceptLanguage, out var languages)
            || languages.Where(x => x.Value != "*").OrderByDescending(x => x.Quality ?? 1).FirstOrDefault() is not { } first)
        {
            return Czech;
        }
        var tag = first.Value.Value ?? "";
        return tag.Equals(Czech, StringComparison.OrdinalIgnoreCase) || tag.StartsWith("cs-", StringComparison.OrdinalIgnoreCase) ? Czech : English;
    }
}
