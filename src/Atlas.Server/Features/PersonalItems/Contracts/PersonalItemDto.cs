namespace Atlas.Server.Features.PersonalItems.Contracts;

/// <summary>
/// One personal item in Můj prostor or Sdíleno se mnou (spec 2026-10-29 §3): a note (<c>atlas.note</c>), its plain-text
/// title and last change, and a plain-text excerpt of its text (Excerpt, at most PersonalItemsQueryHandler.ExcerptLength characters): the list never sends whole texts, GET /objects/{id} reads
/// one. In Můj prostor: CanEdit, how many shares are valid now (Shares); in its trash, when it went there (TrashedAt). In Sdíleno se
/// mnou: the owner's name, my level (read or write) and when my access ends (null: never).
/// </summary>
public sealed record PersonalItemDto(Guid Id, string TypeId, string Title, DateTime UpdatedAt, long Version, bool CanEdit, int Shares, string? OwnerName,
    string? Level, DateTime? ExpiresAt, string? Excerpt, DateTime? TrashedAt = null);
