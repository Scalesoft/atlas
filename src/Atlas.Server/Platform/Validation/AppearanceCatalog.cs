namespace Atlas.Server.Platform.Validation;

/// <summary>
/// The icons and colours a space or a page icon may use. Icons: the web client's appearance catalog
/// (src/Atlas.Web/src/appearance.ts), the icon pack (icon-pack-names.txt, written by tooling/icon-pack) and the
/// names only stored rows still hold; appearanceCatalog.test.ts checks all three against the web client.
/// Colours are 12 hues × 5 tones ("blue-2"); values stored before the palette stay valid.
/// </summary>
public static class AppearanceCatalog
{
    private static readonly string[] CoreIcons =
    [
        "book-open", "home", "star", "lightbulb", "smile", "compass", "megaphone",
        "ticket", "palette", "pin", "box", "blocks", "plug", "highlighter", "pencil",
        "search", "file-text", "file-plus", "folder", "folder-plus", "folder-input", "layout-template", "list",
        "list-checks", "list-ordered", "scroll-text", "quote", "clipboard-paste", "paperclip", "image", "table",
        "type", "hash", "link", "external-link", "users", "workflow", "user", "user-check",
        "code-xml", "square-code", "code", "database", "server", "network", "monitor", "settings-2",
        "sliders-horizontal", "filter", "activity", "layout-dashboard", "refresh-cw", "download", "upload", "rotate-cw",
        "bar-chart-3", "line-chart", "pie-chart", "donut", "sigma", "shield", "shield-check", "shield-alert",
        "lock", "lock-open", "key-round", "fingerprint", "eye", "eye-off", "ban", "info",
        "circle-help", "circle-check", "circle-x", "check", "triangle-alert", "octagon-alert", "octagon-x", "hourglass",
        "calendar", "clock", "history", "sun", "moon", "globe", "map-pin", "building-2",
    ];

    /// <summary>Names no picker offers: sparkles is retired (it means AI now), archive is what the Wiki.js import gives its space.</summary>
    private static readonly string[] LegacyIcons = ["sparkles", "archive"];

    public static readonly IReadOnlySet<string> Icons = new HashSet<string>(CoreIcons.Concat(LoadIconPack()).Concat(LegacyIcons), StringComparer.Ordinal);

    private static readonly string[] Hues = ["red", "orange", "amber", "lime", "green", "teal", "cyan", "blue", "indigo", "violet", "pink", "slate"];

    /// <summary>Space colours (blue, green, amber, coral) and page icon colours (slate … violet) from before the palette.</summary>
    private static readonly string[] LegacyColors = ["blue", "green", "amber", "coral", "slate", "teal", "orange", "rose", "violet"];

    public static readonly IReadOnlySet<string> Colors = new HashSet<string>(
        Hues.SelectMany(hue => Enumerable.Range(1, 5).Select(tone => $"{hue}-{tone}")).Concat(LegacyColors),
        StringComparer.Ordinal);

    /// <summary>The palette a picker offers, hue by hue and tone 1 (lightest) to 5; for clients that must not guess identifiers.</summary>
    public static IReadOnlyList<(string Id, string Hue, int Tone)> Palette { get; } =
        Hues.SelectMany(hue => Enumerable.Range(1, 5).Select(tone => ($"{hue}-{tone}", hue, tone))).ToArray();

    /// <summary>Colours stored before the palette; still accepted, not offered by the picker.</summary>
    public static IReadOnlyList<string> LegacyColorIds => LegacyColors;

    /// <summary>One name per line; line endings of either kind and empty lines do not matter.</summary>
    private static IEnumerable<string> LoadIconPack()
    {
        using var stream = typeof(AppearanceCatalog).Assembly.GetManifestResourceStream("Atlas.Server.Platform.Validation.icon-pack-names.txt")
            ?? throw new InvalidOperationException("The icon pack names are not embedded.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }
}
