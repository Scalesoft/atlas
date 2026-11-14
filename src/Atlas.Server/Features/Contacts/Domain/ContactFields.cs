using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Platform.Objects;

namespace Atlas.Server.Features.Contacts.Domain;

/// <summary>
/// The fields of a contact (objects spec §1a, §2.2) and their rules, those of the editor's contacts plugin (<c>normalizeContact</c>)
/// with the owner's Markdown rule on top: name, function, organization and department are one line of inline Markdown, the address
/// is multi-line Markdown, e-mails, phones, website and photo are identifiers with their own validation (the editor's patterns; a
/// URL is absolute http(s) without credentials, a photo also the contact's own uploaded picture's address, ContactPhotos.Url). Stored canonically: the fields in <see cref="Order"/>, trimmed, without empty
/// ones, e-mails and phones distinct. A change names the fields it sets (null or empty clears one) and the last writer wins per
/// field. Refusals name the field and never repeat the input.
/// </summary>
public static partial class ContactFields
{
    public const int MaxLine = 400;
    public const int MaxAddress = 4096;
    public const int MaxEntries = 20;
    public const int MaxUrl = 2048;
    private const int MaxDirectoryId = 256;
    public const int MaxAlias = 60;
    private const int MaxPicks = 1000;

    /// <summary>The learned pick counts of the aliases, by folded alias; the server keeps them (<see cref="Learn"/>), no change sets them.</summary>
    public const string AliasPicks = "aliasPicks";

    private static readonly string[] Order = ["name", "title", "organization", "department", "emails", "phones", "website", "address", "aliases", "photo", "directoryId"];

    internal static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["name"] = "Jméno", ["title"] = "Funkce", ["organization"] = "Organizace", ["department"] = "Oddělení", ["emails"] = "E-mail",
        ["phones"] = "Telefon", ["website"] = "Web", ["address"] = "Adresa", ["aliases"] = "Zkratky a přezdívky", ["photo"] = "Fotografie",
        ["directoryId"] = "Identifikátor v adresáři"
    };

    /// <summary>The data of a new contact, canonical; <see cref="ObjectChangeRejected"/> for an invalid one.</summary>
    public static JsonObject Create(JsonObject data)
    {
        return Apply(new JsonObject(), data).Data;
    }

    /// <summary>The data with <paramref name="fields"/> set over <paramref name="current"/>, and the change as its viewers hear it:
    /// each named field as stored now, a cleared one as null. A photo may name only <paramref name="contactId"/>'s own uploaded
    /// picture (a contact not created yet has none).</summary>
    public static (JsonObject Data, JsonObject Change) Apply(JsonObject current, JsonObject fields, Guid? contactId = null)
    {
        if (fields.Count == 0)
        {
            throw new ObjectChangeRejected("Změna kontaktu nenastavuje žádné pole.");
        }
        var values = Order.Where(current.ContainsKey).ToDictionary(key => key, key => current[key]!.DeepClone());
        var change = new JsonObject();
        foreach (var (key, value) in fields)
        {
            if (!Labels.TryGetValue(key, out var label))
            {
                throw new ObjectChangeRejected("Neznámé pole kontaktu.");
            }
            var normalized = Normalize(key, value, contactId) ?? throw Rejected(label);
            if (IsEmpty(normalized))
            {
                values.Remove(key);
                change[key] = null;
            }
            else
            {
                values[key] = normalized;
                change[key] = normalized.DeepClone();
            }
        }
        if (!values.ContainsKey("name"))
        {
            throw new ObjectChangeRejected("Jméno kontaktu je povinné.");
        }
        var data = new JsonObject(Order.Where(values.ContainsKey).Select(key => KeyValuePair.Create(key, (JsonNode?)values[key])));
        // The counts of the aliases still there stay.
        var aliases = values.TryGetValue("aliases", out var kept) ? kept!.AsArray().Select(alias => Fold(alias!.GetValue<string>())).ToHashSet() : [];
        if (current[AliasPicks] is JsonObject picks && picks.Where(pick => aliases.Contains(pick.Key)).ToArray() is { Length: > 0 } still)
        {
            data[AliasPicks] = new JsonObject(still.Select(pick => KeyValuePair.Create(pick.Key, pick.Value?.DeepClone())));
        }
        return (data, change);
    }

    /// <summary>
    /// A person picked after searching for <paramref name="query"/> („MMu“, „Mára“): the query becomes an alias of the contact (if it
    /// is new) and its pick count grows, so the next search for it ranks this contact first, the more picked of two first. A query the
    /// contact matches anyway teaches nothing and is refused: every word of it begins a word of the name, function, organization,
    /// department or an e-mail („Mare“, „jan nov“, „vývoj“, „marek.m“), as the web's people search matches (contactSearch.ts); so is
    /// a 21st alias. Pickers that know only the name (the requirement's party) leave the rest to this.
    /// </summary>
    public static (JsonObject Data, JsonObject Change) Learn(JsonObject current, string query)
    {
        var alias = Collapse(query);
        var key = Fold(alias);
        if (key.Length < 2 || alias.Length > MaxAlias || !Rules.IsLine(alias))
        {
            throw Rejected(Labels["aliases"]);
        }
        var words = new[] { Title(current), InlinePlainText(Text(current, "title")), InlinePlainText(Text(current, "organization")), InlinePlainText(Text(current, "department")) }
            .Concat(EmailKeys(current)).SelectMany(Words).ToArray();
        if (Words(alias) is { Length: > 0 } typed && typed.All(part => words.Any(word => word.StartsWith(part, StringComparison.Ordinal))))
        {
            throw new ObjectChangeRejected("Začátek jména, funkce, organizace, oddělení nebo e-mailu není zkratka.");
        }
        var aliases = current["aliases"] as JsonArray ?? [];
        var list = aliases.Select(item => item!.GetValue<string>()).ToList();
        var change = new JsonObject();
        if (!list.Any(item => Fold(item) == key))
        {
            list.Add(alias);
            change["aliases"] = new JsonArray(list.Select(item => (JsonNode)JsonValue.Create(item)).ToArray());
        }
        var (data, _) = Apply(current, change.Count > 0 ? change : new JsonObject { ["aliases"] = aliases.DeepClone() });
        var picks = data[AliasPicks] as JsonObject ?? [];
        picks[key] = Math.Min(MaxPicks, (picks[key]?.GetValue<int>() ?? 0) + 1);
        data[AliasPicks] = picks;
        return (data, change);
    }

    /// <summary>A text as lookups compare it: lower case, no diacritics, single spaces (<c>foldText</c> of the web).</summary>
    public static string Fold(string text) => string.Join(' ', Search.Domain.SearchText.LookupWords(text.Length > 200 ? text[..200] : text));

    /// <summary>The folded words of a text, split at anything but a letter or digit (an e-mail at its dots and @): the web's people search words.</summary>
    private static string[] Words(string text) => NotAWord().Split(Fold(text)).Where(word => word.Length > 0).ToArray();

    /// <summary>The name as plain text: the library's title, sorting and search.</summary>
    public static string Title(JsonObject data)
    {
        return InlinePlainText(Text(data, "name"));
    }

    /// <summary>The whole contact as one line of plain text (search, AI, export), its parts in the stored order.</summary>
    public static string PlainText(JsonObject data)
    {
        var parts = Order.Where(key => key is not ("photo" or "directoryId") && data.ContainsKey(key)).SelectMany(key => key switch
        {
            "emails" or "phones" or "aliases" => data[key]!.AsArray().Select(item => item!.GetValue<string>()),
            "address" => [Collapse(Markdig.Markdown.ToPlainText(Text(data, key)))],
            "website" => [Text(data, key)],
            _ => [InlinePlainText(Text(data, key))]
        });
        return string.Join(" · ", parts.Where(part => part.Length > 0));
    }

    /// <summary>The e-mail addresses in lower case: the same person is found by any spelling of the address.</summary>
    public static IReadOnlyList<string> EmailKeys(JsonObject data)
    {
        return data["emails"] is JsonArray emails ? emails.Select(item => item!.GetValue<string>().ToLowerInvariant()).ToArray() : [];
    }

    /// <summary>The text of every contact mention in <paramref name="text"/> in place of the mention (its label, unescaped).</summary>
    public static string WithoutContactMentions(string text)
    {
        return ContactMention().Replace(text, match => Regex.Replace(match.Groups[1].Value, @"\\([\\\[\]])", "$1"));
    }

    [GeneratedRegex(@"\[((?:\\.|[^\]\\])*)\]\(a-contact:[^)]*\)")]
    private static partial Regex ContactMention();

    /// <summary>One line of inline Markdown as plain text: a start that would open a block stays text, as the web renders it
    /// (escapeInlineMarkdown in boardMarkdownText.ts). A note's title reads the same (NoteObjectType).</summary>
    public static string InlinePlainText(string line)
    {
        var escaped = ThematicBreak().IsMatch(line) ? "\\" + line : OrderedStart().Replace(BlockStart().Replace(line, "$1\\$2"), "$1\\$2");
        return Collapse(Markdig.Markdown.ToPlainText(escaped));
    }

    /// <summary>The value as stored (an empty string or list clears the field), or null when it is not the field's.</summary>
    private static JsonNode? Normalize(string key, JsonNode? value, Guid? contactId)
    {
        if (value is null)
        {
            return JsonValue.Create("");
        }
        // A contact never holds a mention of another contact (`[Jana](a-contact:?…)`): its own fields keep the mention's text.
        if (key is "name" or "title" or "organization" or "department" or "address" && String(value) is { } written && written.Contains("](a-contact:", StringComparison.Ordinal))
        {
            value = JsonValue.Create(WithoutContactMentions(written));
        }
        return key switch
        {
            "emails" => List(value, email => Email().IsMatch(email)),
            "phones" => List(value, phone => Phone().IsMatch(phone)),
            "aliases" => Aliases(value),
            "website" or "photo" => Line(value, MaxUrl) is { } url && (url.Length == 0 || IsWebUrl(url) || key == "photo" && contactId is { } own && ContactPhotos.IsUrl(url, own)) ? JsonValue.Create(url) : null,
            "address" => String(value) is { } text && text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() is var address
                         && address.Length <= MaxAddress && Rules.IsMultiLineText(address)
                ? JsonValue.Create(address)
                : null,
            "directoryId" => Line(value, MaxDirectoryId) is { } id ? JsonValue.Create(id) : null,
            _ => Line(value, MaxLine) is { } line ? JsonValue.Create(line) : null
        };
    }

    private static JsonArray? List(JsonNode value, Func<string, bool> valid)
    {
        if (value is not JsonArray items || items.Count > MaxEntries)
        {
            return null;
        }
        var entries = new List<string>();
        foreach (var item in items)
        {
            if (item is null || Line(item, MaxLine) is not { Length: > 0 } entry || !valid(entry))
            {
                return null;
            }
            if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(entry);
            }
        }
        return new JsonArray(entries.Select(entry => (JsonNode)JsonValue.Create(entry)).ToArray());
    }

    /// <summary>At most 20 one-line aliases of 60 characters, single-spaced, each once whatever its case and diacritics.</summary>
    private static JsonArray? Aliases(JsonNode value)
    {
        if (value is not JsonArray items || items.Count > MaxEntries)
        {
            return null;
        }
        var entries = new List<string>();
        foreach (var item in items)
        {
            if (item is null || Line(item, MaxAlias) is not { Length: > 0 } entry)
            {
                return null;
            }
            if (!entries.Any(kept => Fold(kept) == Fold(entry)))
            {
                entries.Add(Collapse(entry));
            }
        }
        return new JsonArray(entries.Select(entry => (JsonNode)JsonValue.Create(entry)).ToArray());
    }

    private static string? Line(JsonNode value, int max)
    {
        return String(value)?.Trim() is { } line && line.Length <= max && Rules.IsLine(line) ? line : null;
    }

    private static string? String(JsonNode value)
    {
        return value is JsonValue text && text.GetValueKind() == JsonValueKind.String ? text.GetValue<string>() : null;
    }

    private static bool IsWebUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0;
    }

    private static bool IsEmpty(JsonNode value)
    {
        return value is JsonArray { Count: 0 } || value is JsonValue && value.GetValue<string>().Length == 0;
    }

    private static string Text(JsonObject data, string key)
    {
        return data[key]?.GetValue<string>() ?? "";
    }

    private static string Collapse(string text)
    {
        return Whitespace().Replace(text, " ").Trim();
    }

    private static ObjectChangeRejected Rejected(string label) => new($"Neplatné pole kontaktu: {label}.");

    [GeneratedRegex(@"^[^\s@<>?,;:]+@[^\s@<>?,;:]+\.[^\s@<>?,;:]+$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^\+?[\d\s().\-#*]{1,80}$")]
    private static partial Regex Phone();

    [GeneratedRegex(@"^(\s*)(#{1,6}(?=\s|$)|>|[-+*](?=\s)|```|~~~)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^(\s*\d+)([.)])(?=\s)")]
    private static partial Regex OrderedStart();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex ThematicBreak();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotAWord();
}
