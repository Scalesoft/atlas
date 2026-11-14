namespace Atlas.Server.Features.PageDecorations;

public sealed record PageDecorationDto(Guid RevisionId, string? Icon, string? Color);
public sealed record PageDecorationState(PageDecorationDto? Decoration);
public sealed record SetPageDecorationRequest(string? Icon, string? Color, Guid? ExpectedRevisionId);
public sealed record AppearanceColorDto(string Id, string Hue, int Tone);
/// <summary>Icon ids (filtered and bounded), the total number of matching icons, the palette and the legacy colours that are still accepted.</summary>
public sealed record AppearanceCatalogDto(IReadOnlyList<string> Icons, int IconTotal, IReadOnlyList<AppearanceColorDto> Colors, IReadOnlyList<string> LegacyColors);
public sealed record GetPageDecoration(Guid PageId);
public sealed record SetPageDecoration(Guid PageId, SetPageDecorationRequest Request);
