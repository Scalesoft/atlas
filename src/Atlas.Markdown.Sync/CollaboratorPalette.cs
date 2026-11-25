using System.Reflection;
using System.Text.Json;

namespace Atlas.Markdown.Sync;

/// <summary>
/// The one palette of collaborator colours (the editor's <c>core/collaboratorColors.json</c>, embedded here): 12 colours in the order
/// they are handed out, so neighbours differ clearly. A live document or board gives each person present one colour: the one they
/// prefer (their own choice, else <see cref="Auto"/>) when no one present has it, otherwise the next free one in palette order. Earlier
/// arrivals keep theirs; a colour is free again when its holder leaves. The stamped value is the light variant, a literal #rrggbb.
/// </summary>
public static class CollaboratorPalette
{
    public sealed record Entry(string Id, string Light, string Dark);

    public static IReadOnlyList<Entry> Entries { get; } = Load();

    public static int Count => Entries.Count;

    /// <summary>The slot of a palette id, or -1.</summary>
    public static int SlotOf(string? id) => id is null ? -1 : Entries.ToList().FindIndex(entry => entry.Id == id);

    /// <summary>The slot of a stamped colour (either variant), or -1.</summary>
    public static int SlotOfColor(string? color) => color is null ? -1 : Entries.ToList().FindIndex(entry => string.Equals(entry.Light, color, StringComparison.OrdinalIgnoreCase) || string.Equals(entry.Dark, color, StringComparison.OrdinalIgnoreCase));

    /// <summary>The slot a person who chose none starts from: a hash of the user id, the arithmetic of the editor's <c>markdownUserColorSlot</c>.</summary>
    public static int Auto(string userId)
    {
        var hash = 0;
        foreach (var character in userId)
        {
            hash = unchecked(hash * 31 + character);
        }
        return (int)(Math.Abs((long)hash) % Count);
    }

    /// <summary>The preferred slot when free, else the next free one after it in palette order; with every slot taken, the preferred one (two share).</summary>
    public static int Assign(int preferred, IReadOnlySet<int> taken)
    {
        for (var step = 0; step < Count; step++)
        {
            var slot = (preferred + step) % Count;
            if (!taken.Contains(slot))
            {
                return slot;
            }
        }
        return preferred;
    }

    /// <summary>
    /// The colour to stamp for <paramref name="userId"/>: the one the same person already has among <paramref name="present"/> (user id, colour) —
    /// two tabs of one person look alike — else <see cref="Assign"/> from <paramref name="preferred"/> (a palette colour; null: <see cref="Auto"/>).
    /// A colour outside the palette (an agent's, a host's own) is kept as it is.
    /// </summary>
    public static string Effective(string userId, string? preferred, IEnumerable<(string UserId, string Color)> present)
    {
        if (preferred is not null && SlotOfColor(preferred) < 0)
        {
            return preferred;
        }
        var others = present.ToList();
        if (others.FirstOrDefault(other => other.UserId == userId) is { Color: { } own })
        {
            return own;
        }
        var taken = others.Select(other => SlotOfColor(other.Color)).Where(slot => slot >= 0).ToHashSet();
        var start = preferred is null ? Auto(userId) : SlotOfColor(preferred);
        return Entries[Assign(start, taken)].Light;
    }

    private static List<Entry> Load()
    {
        using var stream = typeof(CollaboratorPalette).Assembly.GetManifestResourceStream("Atlas.Markdown.Sync.collaborator-colors.json")
            ?? throw new InvalidOperationException("The collaborator palette is not embedded.");
        return JsonSerializer.Deserialize<List<Entry>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
