using Atlas.Server.Platform.Validation;

namespace Atlas.Server.Features.SpaceSettings.Domain;

/// <summary>Icon and color of a space from the appearance catalog. The UI offers exactly these values; anything else is rejected or normalized.</summary>
public sealed record SpaceAppearance(string Icon, string Color)
{
    public const string DefaultIcon = "book-open";
    public const string DefaultColor = "blue";
    private static IReadOnlySet<string> Icons => AppearanceCatalog.Icons;
    private static IReadOnlySet<string> Colors => AppearanceCatalog.Colors;

    public static SpaceAppearance Normalize(string? icon, string? color)
    {
        return new SpaceAppearance(icon is not null && Icons.Contains(icon) ? icon : DefaultIcon, color is not null && Colors.Contains(color) ? color : DefaultColor);
    }

    public static SpaceAppearance Validate(string? icon, string? color)
    {
        if (icon is null || color is null || !Icons.Contains(icon) || !Colors.Contains(color))
        {
            throw new ApiException(400, "Vyberte podporovanou ikonu a barvu prostoru.");
        }
        return new SpaceAppearance(icon!, color!);
    }
}
