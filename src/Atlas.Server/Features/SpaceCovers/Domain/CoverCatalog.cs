using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SpaceSettings.Domain;

namespace Atlas.Server.Features.SpaceCovers.Domain;

/// <summary>One built-in drawing as the server needs it: its key, its Czech name, and what a space must say to get it
/// (tooling/space-covers/matching.py). Tags and the preview hue of catalog.json are the web's.</summary>
public sealed record CoverCatalogEntry(string Id, string Name, string Group, string[] Stems, string[] Icons);

/// <summary>The built-in drawings of space images: the catalog.json the web build ships, embedded here, so both sides know the
/// same keys. Shipped data, not wiki data. <see cref="Match"/> is the single place a second matcher (by meaning, when AI is on)
/// would join.</summary>
public static class CoverCatalog
{
    private const string Resource = "Atlas.Server.Features.SpaceCovers.cover-catalog.json";

    /// <summary>A stem this short matches a whole word only (sql, hr, ai); a longer one is a word prefix.</summary>
    public const int WholeWordLength = 3;

    /// <summary>The memo holds this many picks and is cleared when full: any account may ask for a match.</summary>
    public const int MemoLimit = 1024;

    // A name hit outweighs the icon, the icon outweighs any number of description hits.
    private const int NameWeight = 1000;
    private const int IconWeight = 100;
    private const int MaximumDescriptionHits = 99;

    private static readonly CoverCatalogEntry[] s_entries = Load();
    private static readonly FrozenSet<string> s_keys = s_entries.Select(x => x.Id).ToFrozenSet(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Name, string Description, string Icon), string?> s_picks = new();

    /// <summary>In catalog order, which is also the order a tie is decided in.</summary>
    public static IReadOnlyList<CoverCatalogEntry> Entries => s_entries;

    public static bool Knows(string? key) => key is not null && s_keys.Contains(key);

    /// <summary>The drawing for a space, or null when nothing fits. Name and description are normalised like search text and
    /// split into words; each entry scores its stems found in the name, its icon, and its stems found in the description, in
    /// that order of weight. The default icon implies nothing. The highest score wins, a tie stays with the earlier entry.</summary>
    public static string? Match(string name, string? description, string icon)
    {
        var key = (name, description ?? "", icon);
        if (s_picks.TryGetValue(key, out var known))
        {
            return known;
        }
        var pick = Pick(Words(name), Words(description), icon);
        if (s_picks.Count >= MemoLimit)
        {
            s_picks.Clear();
        }
        s_picks[key] = pick;
        return pick;
    }

    private static string? Pick(string[] name, string[] description, string icon)
    {
        var implies = icon != SpaceAppearance.DefaultIcon;
        string? pick = null;
        var best = 0;
        foreach (var entry in s_entries)
        {
            var score = NameWeight * Hits(name, entry.Stems)
                + (implies && Array.IndexOf(entry.Icons, icon) >= 0 ? IconWeight : 0)
                + Math.Min(Hits(description, entry.Stems), MaximumDescriptionHits);
            // Strictly greater: an equal score stays with the earlier entry.
            if (score > best)
            {
                best = score;
                pick = entry.Id;
            }
        }
        return pick;
    }

    /// <summary>How many of the words match one of the stems. Words, not stems, are counted, so two stems that both fit one
    /// word count once.</summary>
    private static int Hits(string[] words, string[] stems)
    {
        var hits = 0;
        foreach (var word in words)
        {
            foreach (var stem in stems)
            {
                if (stem.Length <= WholeWordLength ? word.Equals(stem, StringComparison.Ordinal) : word.StartsWith(stem, StringComparison.Ordinal))
                {
                    hits++;
                    break;
                }
            }
        }
        return hits;
    }

    private static string[] Words(string? text)
    {
        return SearchText.NormalizeContent(text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static CoverCatalogEntry[] Load()
    {
        using var stream = typeof(CoverCatalog).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The embedded cover catalog {Resource} is missing.");
        return JsonSerializer.Deserialize<CoverCatalogEntry[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The embedded cover catalog is empty.");
    }
}
