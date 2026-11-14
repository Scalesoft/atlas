using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Features.Projector.Domain;

/// <summary>The attributes of the <c>requirement</c> node (spec §2.1), checked where the server itself writes them: the editor's
/// manifest validates them on every save as well (R1), this gives the block commands their own readable refusals.</summary>
public static partial class RequirementAttributes
{
    public const string NodeType = "requirement";
    public const int MaxText = 200;
    public const int MaxRequires = 1000;

    public static readonly IReadOnlyList<string> Directions = ["internal", "inbound", "outbound"];
    public static readonly IReadOnlyList<string> States = ["planned", "waiting_for_input", "in_progress", "done", "blocked", "not_applicable"];

    /// <summary>Done or not applicable: nothing waits on it and it is not missing.</summary>
    public static bool IsFinished(string state) => state is "done" or "not_applicable";

    public static IReadOnlyList<string> ParseRequires(string? requires)
    {
        return (requires ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <param name="otherKeys">The keys of the page's other blocks: a key is unique within its page.</param>
    /// <param name="heading">A requirement heading's view: its key is its headingId (letters, digits, dots, underscores and hyphens, at most
    /// 128) and its requires are headingIds or <c>pageId#headingId</c>, a line of links with no length limit.</param>
    public static void Validate(JsonObject attrs, IReadOnlySet<string> otherKeys, bool heading = false)
    {
        var key = Text(attrs, "key");
        if (heading ? !Platform.Content.RequirementHeadings.IsValidId(key) : key.Length > 0 && !Key().IsMatch(key))
        {
            throw Invalid("key", heading
                ? "ID požadavku (headingId) smí mít jen písmena bez diakritiky, číslice, tečky, podtržítka a pomlčky (nejvýše 128 znaků) a nesmí být prázdné."
                : "Klíč požadavku smí mít jen malá písmena bez diakritiky, číslice, tečky a pomlčky (nejvýše 96 znaků).");
        }
        if (key.Length > 0 && otherKeys.Contains(key))
        {
            throw new ApiException(400, $"Klíč „{key}“ už na stránce má jiný požadavek; zvolte jiný, nebo nechte klíč prázdný a vznikne z názvu.",
                "requirement_key_taken", Field("key"));
        }
        if (!Directions.Contains(Text(attrs, "direction")))
        {
            throw Invalid("direction", "Směr požadavku musí být internal, inbound nebo outbound.");
        }
        if (!States.Contains(Text(attrs, "state")))
        {
            throw Invalid("state", $"Neznámý stav požadavku; použijte {string.Join(", ", States)}.");
        }
        var date = Text(attrs, "date");
        if (date.Length > 0 && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw Invalid("date", "Datum požadavku musí být ve tvaru RRRR-MM-DD (např. 2026-10-29), nebo prázdné.");
        }
        var contact = Text(attrs, "partyContactId");
        if (contact.Length > 0 && !Guid.TryParseExact(contact, "D", out _))
        {
            throw Invalid("partyContactId", "partyContactId je id kontaktu z adresáře (GUID), nebo prázdné pro stranu zapsanou textem.");
        }
        foreach (var field in new[] { "party", "where", "stateLabel", "system" })
        {
            if (Text(attrs, field).Length > MaxText)
            {
                throw Invalid(field, $"Strana, místo uložení, vlastní stav a služba smějí mít nejvýše {MaxText} znaků.");
            }
        }
        var requires = Text(attrs, "requires");
        var required = ParseRequires(requires);
        if (heading ? required.Any(x => !Platform.Content.RequirementHeadings.IsValidId(x) && Platform.Content.RequirementHeadings.CrossPage(x) is null)
            : requires.Length > MaxRequires || required.Any(x => !Reference().IsMatch(x)))
        {
            throw Invalid("requires", $"Závislosti jsou klíče oddělené čárkou (klíč požadavku jiné stránky jako klíč@pageId), nejvýše {MaxRequires} znaků.");
        }
        if (key.Length > 0 && required.Contains(key))
        {
            throw Invalid("requires", "Požadavek nemůže záviset sám na sobě; odeberte jeho vlastní klíč ze závislostí.");
        }
    }

    /// <summary>A dependency on another page's block, <c>key@pageId</c> (plan 3.1 §2.3 point 5), or on another page's requirement heading,
    /// <c>pageId#headingId</c> (its key); null for a key of the same page.</summary>
    public static (string Key, Guid PageId)? CrossPage(string reference)
    {
        if (Platform.Content.RequirementHeadings.CrossPage(reference) is { } heading)
        {
            return (heading.HeadingId, heading.Page);
        }
        var at = reference.IndexOf('@');
        return at > 0 && Guid.TryParseExact(reference[(at + 1)..], "D", out var page) ? (reference[..at], page) : null;
    }

    public static string Reference(string key, Guid pageId) => $"{key}@{pageId:D}";

    /// <summary>A string attribute; one the stored node leaves out reads as the schema's default (spec §2.1).</summary>
    public static string Text(JsonNode? attrs, string name) => Platform.Content.RequirementText.Attr(attrs, name);

    /// <summary>A request's list of keys: given, at most <paramref name="max"/>, each a key, at least one of those it asks for. The keys
    /// a page already holds (<paramref name="asStored"/>: present, not applicable) are only compared, so any text of a key's length
    /// passes and none is fine: a page saved before a rule must not lock its catalog panel.</summary>
    public static IReadOnlyList<string> RequireKeys(IReadOnlyList<string>? keys, string name, int max, bool asStored = false)
    {
        if (keys is null || keys.Count > max || (!asStored && keys.Count == 0)
            || keys.Any(key => key is null || (asStored ? key.Length > 128
                : !Key().IsMatch(key.Trim()) && !Platform.Content.RequirementHeadings.IsValidId(key.Trim()))))
        {
            throw InvalidArgument(name, $"{name}: nejvýše {max} klíčů požadavků (nebo ID požadavků-nadpisů), každý z písmen bez diakritiky, číslic, teček, podtržítek a pomlček.");
        }
        return keys;
    }

    /// <summary>"SSL certifikát" → "ssl-certifikat", as the web's slugKey: the key's alphabet, at most 96 characters; empty when nothing fits.</summary>
    public static string Slug(string title)
    {
        var plain = string.Concat(title.Normalize(NormalizationForm.FormD).Where(c => c is < '̀' or > 'ͯ')).ToLowerInvariant();
        var slug = NotKey().Replace(plain, "-").TrimStart('-', '.');
        return slug[..Math.Min(slug.Length, 96)].TrimEnd('-', '.');
    }

    /// <summary><paramref name="key"/>, or the first free <c>key-2</c>, <c>key-3</c> … within 96 characters (the web's nextRequirementKey).</summary>
    public static string Unique(string key, IReadOnlySet<string> taken)
    {
        for (var number = 2; key.Length > 0 && taken.Contains(key); number++)
        {
            var suffix = $"-{number}";
            var candidate = key[..Math.Min(key.Length, 96 - suffix.Length)] + suffix;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
        return key;
    }

    /// <summary>A refused request argument: the message names <paramref name="field"/> and how to fix it, <c>details.field</c> names it for a client.</summary>
    public static ApiException InvalidArgument(string field, string message) => new(400, message, "invalid_argument", Field(field));

    private static ApiException Invalid(string field, string message) => new(400, message, "invalid_requirement", Field(field));

    private static Dictionary<string, object?> Field(string field) => new() { ["field"] = field };

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,95}$")]
    private static partial Regex Key();

    /// <summary>A key, or a key of another page's block (<c>key@pageId</c>, the id in its lowercase D form as the web writes it).</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,95}(?:@[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?$")]
    private static partial Regex Reference();

    [GeneratedRegex("[^a-z0-9.]+")]
    private static partial Regex NotKey();
}
