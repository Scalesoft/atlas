using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Validation;

namespace Atlas.Server.Features.PageDecorations;

/// <summary>One suggested icon: catalog ids only, a colour of the palette and a short reason.</summary>
public sealed record IconCandidate(string Icon, string Color, string Reason);

/// <summary>
/// Icon candidates for a page from its title and text, without AI: a small Czech/English keyword table over the ids of the
/// catalog (<see cref="AppearanceCatalog"/>), plus words of the title that are a part of an icon id. Title words weigh more than
/// words of the text. A page without a match gets a neutral default set. Embeddings are no part of this: the catalog has no
/// per-icon text to embed, and the table is the whole mapping (extend it here, the test checks every id exists).
/// </summary>
public static class IconSuggester
{
    public const int MaxCandidates = 8;

    /// <summary>Stems without diacritics (a word starts with it) → icons, in the order of preference, and the hue of their colour.</summary>
    public static readonly (string[] Stems, string[] Icons, string Hue)[] Table =
    [
        (["nasazen", "deploy", "release", "vydani", "produkc", "spusten"], ["rocket", "cloud-upload", "server"], "indigo"),
        (["nemocnic", "zdravot", "pacient", "lekar", "ambulan", "klinik", "hospital"], ["hospital", "stethoscope", "heart-pulse"], "red"),
        (["server", "infrastruktur", "hosting", "linux", "virtual", "vps", "kontejner", "docker"], ["server", "hard-drive", "cpu"], "slate"),
        (["kontakt", "telefon", "adresar"], ["contact", "phone", "mail"], "teal"),
        (["tym", "lide", "zamestnan", "osob", "kolegov", "personal"], ["users", "user", "contact"], "teal"),
        (["email", "mail", "posta"], ["mail", "contact"], "teal"),
        (["databaz", "sql", "postgres", "tabulk"], ["database", "table", "server"], "cyan"),
        (["zalohov", "backup", "obnov"], ["database-backup", "hard-drive", "history"], "slate"),
        (["bezpecn", "heslo", "hesla", "opravnen", "pristup", "autentiz", "prihlas"], ["shield-check", "lock", "key-round"], "red"),
        (["navod", "postup", "manual", "prirucka", "dokumentac", "wiki", "pruvodce"], ["book-open", "list-ordered", "file-text"], "blue"),
        (["zapis", "schuz", "porad", "poznamk", "protokol"], ["scroll-text", "notebook-pen", "calendar"], "amber"),
        (["kalendar", "termin", "harmonogram", "rozvrh", "planovan"], ["calendar-days", "calendar", "clock"], "amber"),
        (["ukol", "todo", "checklist", "kontroln"], ["list-checks", "circle-check", "list"], "green"),
        (["faktur", "uctovan", "platb", "cenik", "finance", "financ", "rozpocet", "naklad"], ["receipt", "banknote", "wallet"], "green"),
        (["smlouv", "dohod", "licenc"], ["file-text", "handshake", "scroll-text"], "slate"),
        (["zakazk", "projekt", "klient", "zakaznik"], ["briefcase", "box", "blocks"], "violet"),
        (["pozadavk", "tiket", "podpora", "helpdesk", "ticket"], ["ticket", "circle-help", "list-checks"], "orange"),
        (["chyb", "bug", "incident", "porucha", "problem"], ["bug", "triangle-alert", "octagon-alert"], "red"),
        (["api", "integrac", "rozhran", "plugin", "webhook"], ["plug", "code-xml", "workflow"], "violet"),
        (["sit", "vpn", "firewall", "dns", "router"], ["network", "globe", "shield"], "cyan"),
        (["graf", "statistik", "report", "metrik", "monitoring", "vykon"], ["bar-chart-3", "line-chart", "activity"], "lime"),
        (["obrazek", "obrazky", "foto", "galerie", "logo"], ["image", "palette"], "pink"),
        (["nastaven", "konfigurac", "parametr"], ["settings-2", "sliders-horizontal", "wrench"], "slate"),
        (["skolen", "vzdelav", "kurz", "onboarding", "zaucen"], ["graduation-cap", "book-open", "lightbulb"], "blue"),
        (["sklad", "material", "zasob", "inventar"], ["warehouse", "package", "box"], "orange"),
        (["doprav", "auto", "vozidl", "kuryr"], ["truck", "car", "map-pin"], "orange"),
        (["budov", "kancelar", "pobock", "sidlo"], ["building-2", "map-pin", "home"], "slate"),
        (["novink", "oznamen", "aktualit", "komunikac"], ["megaphone", "info", "star"], "pink"),
        (["obchod", "nabidk", "partner", "dodavatel"], ["handshake", "briefcase", "megaphone"], "green"),
        (["uvod", "zacatk", "start", "prehled", "rozcestnik"], ["home", "compass", "layout-dashboard"], "blue"),
        (["otazk", "faq", "dotaz"], ["circle-help", "info", "search"], "blue"),
        (["kod", "programov", "vyvoj", "source", "repozitar"], ["code-xml", "terminal", "square-code"], "violet"),
    ];

    private static readonly string[] DefaultIcons = ["file-text", "book-open", "lightbulb", "folder", "list-checks", "star"];

    public static IReadOnlyList<IconCandidate> Suggest(string? title, string? text, int count = MaxCandidates)
    {
        var scores = new Dictionary<string, (double Score, string Hue, string Reason)>(StringComparer.Ordinal);
        void Add(string icon, double score, string hue, string reason)
        {
            if (!AppearanceCatalog.Icons.Contains(icon))
            {
                return;
            }
            scores[icon] = scores.TryGetValue(icon, out var old) ? (old.Score + score, old.Hue, old.Reason) : (score, hue, reason);
        }

        var titleWords = Words(title).Distinct().ToArray();
        var bodyWords = Words(text).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        foreach (var (stems, icons, hue) in Table)
        {
            // The reason quotes the stem of the table, never the page's word (which could carry anything after the stem).
            var inTitle = titleWords.Select(word => stems.FirstOrDefault(stem => Matches(word, stem))).FirstOrDefault(stem => stem is not null);
            var inBody = bodyWords.Select(x => (Stem: stems.FirstOrDefault(stem => Matches(x.Key, stem)), Count: x.Value)).Where(x => x.Stem is not null)
                .OrderByDescending(x => x.Count).FirstOrDefault();
            var weight = (inTitle is null ? 0 : 6) + (inBody.Stem is null ? 0 : Math.Min(inBody.Count, 3));
            if (weight == 0)
            {
                continue;
            }
            var reason = inTitle is not null ? $"název obsahuje „{inTitle}…“" : $"text obsahuje „{inBody.Stem}…“";
            for (var rank = 0; rank < icons.Length; rank++)
            {
                Add(icons[rank], weight - rank * 0.5, hue, reason);
            }
        }
        // Words of the title (English or not) that are a whole part of an icon id: "server", "calendar", "database".
        foreach (var word in titleWords.Where(x => x.Length >= 4))
        {
            foreach (var id in AppearanceCatalog.Icons.Where(id => id.Split('-').Contains(word, StringComparer.Ordinal)))
            {
                Add(id, 4 - Math.Min(id.Length, 30) / 30.0, "blue", $"název obsahuje „{word}“, část id ikony");
            }
        }

        var found = scores.OrderByDescending(x => x.Value.Score).ThenBy(x => x.Key, StringComparer.Ordinal).Take(count)
            .Select(x => new IconCandidate(x.Key, x.Value.Hue + "-3", x.Value.Reason)).ToList();
        foreach (var icon in DefaultIcons.Where(x => found.Count < Math.Min(count, 5) && found.All(f => f.Icon != x)))
        {
            found.Add(new IconCandidate(icon, "blue-3", "výchozí nabídka, nic z názvu a textu nevyplývá"));
        }
        return found;
    }

    /// <summary>All the text values of a ProseMirror document, bounded (the first 4 000 characters are enough to tell a topic).</summary>
    public static string TextOf(string? contentJson)
    {
        var text = new StringBuilder();
        void Walk(JsonNode? node)
        {
            if (text.Length > 4000)
            {
                return;
            }
            switch (node)
            {
                case JsonObject o:
                    if (o["text"] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        text.Append(s).Append(' ');
                    }
                    Walk(o["content"]);
                    break;
                case JsonArray a:
                    foreach (var child in a)
                    {
                        Walk(child);
                    }
                    break;
            }
        }
        try
        {
            Walk(string.IsNullOrEmpty(contentJson) ? null : JsonNode.Parse(contentJson));
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return text.ToString();
    }

    /// <summary>A stem of up to three letters ("sit", "api", "sql") must be the whole word, so "situace" is not a network.</summary>
    private static bool Matches(string word, string stem) => stem.Length <= 3 ? word == stem : word.StartsWith(stem, StringComparison.Ordinal);

    private static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var plain = new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant();
        return plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\'', '„', '“', '/', '*', '#', '-')).Where(x => x.Length >= 2);
    }
}
