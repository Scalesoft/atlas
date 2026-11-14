using System.Text;
using System.Globalization;

namespace Atlas.Server.Features.PageTags.Domain;

public static class TagNames
{
    public static (string Name, string NormalizedName)[] Validate(SetPageTagsRequest request)
    {
        if (request.Names is null || request.ExpectedTagIds is null || request.ExpectedTagIds.Length > 20
            || request.ExpectedTagIds.Any(id => id == Guid.Empty) || request.Names.Length > 100)
            throw Invalid();
        var names = Prepare(request.Names);
        if (names.Length > 20) throw Invalid();
        return names;
    }

    /// <summary>
    /// Normalizes tag names (throws for an invalid one) and returns each tag once, sorted by normalized
    /// name: the order in which callers must resolve tags through <see cref="PageTagStore.ResolveAsync"/>.
    /// </summary>
    public static (string Name, string NormalizedName)[] Prepare(IEnumerable<string> names) => names
        .Select(name => Normalize(name, 48, false))
        .Select(name => (Name: name, NormalizedName: name.ToLowerInvariant()))
        .DistinctBy(name => name.NormalizedName)
        .OrderBy(name => name.NormalizedName, StringComparer.Ordinal).ToArray();

    public static string Normalize(string? input, int maximumLength, bool allowEmpty)
    {
        if (input is null && !allowEmpty || input is { Length: > 2000 }
            || input?.EnumerateRunes().Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format) is true)
            throw Invalid();
        string name;
        try
        {
            name = string.Join(' ', (input ?? "").Normalize(NormalizationForm.FormKC)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (ArgumentException) { throw Invalid(); }
        if (name.Length > maximumLength || !allowEmpty && name.Length == 0) throw Invalid();
        return name;
    }

    /// <summary>The add/remove change: bounded lists and the same expected ids rule as the full set.</summary>
    public static void ValidateChange(ChangePageTagsRequest request)
    {
        if ((request.Add?.Length ?? 0) > 100 || (request.Remove?.Length ?? 0) > 100 || request.ExpectedTagIds is { Length: > 20 }
            || request.ExpectedTagIds?.Any(id => id == Guid.Empty) is true)
            throw Invalid();
    }

    public static ApiException Invalid() =>
        new(400, "Zadejte nejvýše 20 štítků s názvem o délce 1–48 znaků bez řídicích znaků.");
}
