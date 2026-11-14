using Atlas.Server.Platform.Validation;

namespace Atlas.Server.Features.PageDecorations;

/// <summary>
/// The icon and colour identifiers the decoration accepts, read from the one server catalog the validation uses (no copy of
/// identifiers; Czech labels live only in the web client's appearance.ts). The icon pack has well over a thousand names, so a
/// caller filters by a part of the id and takes a bounded window.
/// </summary>
public static class AppearanceCatalogView
{
    public const int DefaultLimit = 200;
    public const int MaximumLimit = 2000;

    public static AppearanceCatalogDto Read(string? query, int? limit, int? offset = null)
    {
        var take = limit ?? DefaultLimit;
        var skip = offset ?? 0;
        var needle = query?.Trim().ToLowerInvariant() ?? "";
        if (take is < 1 or > MaximumLimit || skip < 0 || needle.Length > 64)
            throw new ApiException(400, "Počet ikon musí být 1–{maximumLimit}, posun nezáporný a hledaný text nejvýše 64 znaků.", "invalid_argument", args: new Dictionary<string, object?> { ["maximumLimit"] = MaximumLimit });
        var matching = AppearanceCatalog.Icons.Where(id => needle.Length == 0 || id.Contains(needle, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        return new AppearanceCatalogDto(matching.Skip(skip).Take(take).ToArray(), matching.Length,
            AppearanceCatalog.Palette.Select(x => new AppearanceColorDto(x.Id, x.Hue, x.Tone)).ToArray(), AppearanceCatalog.LegacyColorIds);
    }
}
