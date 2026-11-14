namespace Atlas.Server.Features.PageOrganization.Contracts;

/// <param name="TitleOrderCancelled">A hand move within a level pinned to titles ended the pin (Domain.TitleOrder); „Zpět“ pins it again.</param>
public sealed record OrganizationChange(long Version, IReadOnlyList<Guid> AffectedIds, bool HasSiblingTitleCollision = false, bool TitleOrderCancelled = false);
