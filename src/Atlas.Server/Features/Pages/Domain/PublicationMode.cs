namespace Atlas.Server.Features.Pages.Domain;

/// <summary>
/// 3.6-I, the page setting „Publikace“: Manual (Ručně), readers read the version published by hand; Automatic (Automaticky), a version
/// that closes after inactivity is published by itself (PublicationModeAutoPublish); Live (Živě), readers read the latest saved state and
/// follow it live, read-only, and closed versions are published as for Automatic so that mentions and the AI index follow. A page
/// inherits its space's default (Space.PublicationMode) unless it has its own value. Stored and sent by name.
/// </summary>
public enum PublicationMode
{
    Manual,
    Automatic,
    Live
}

public static class PublicationModes
{
    /// <summary>A mode sent by its name or its Czech name (Ručně, Automaticky, Živě), in any case and with or without diacritics; anything else is 400.</summary>
    public static PublicationMode Parse(string? value) => Fold(value) switch
    {
        "manual" or "rucne" => PublicationMode.Manual,
        "automatic" or "automaticky" => PublicationMode.Automatic,
        "live" or "zive" => PublicationMode.Live,
        _ => throw new ApiException(400, "Vyberte publikaci Manual (Ručně), Automatic (Automaticky), nebo Live (Živě); inherit ji zdědí z prostoru.", "invalid_publication_mode")
    };

    /// <summary>Like <see cref="Parse"/>, but null and "inherit" mean the page follows its space (null).</summary>
    public static PublicationMode? ParseOrInherit(string? value) => value is null || Fold(value) == "inherit" ? null : Parse(value);

    private static string Fold(string? value)
        => new string((value ?? "").Trim().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant();

    /// <summary>The mode that applies to a page: its own, else its space's, read only when the page has none.</summary>
    public static async Task<PublicationMode> EffectiveAsync(NHibernate.ISession session, Page page, CancellationToken ct)
        => page.PublicationMode ?? (await session.GetAsync<Spaces.Domain.Space>(page.SpaceId, ct))?.PublicationMode ?? PublicationMode.Manual;

    /// <summary>SQL of the effective mode of page <c>p</c> in space <c>s</c>, for statements that join both.</summary>
    public const string EffectiveSql = """coalesce(p."PublicationMode", s."PublicationMode")""";
}
