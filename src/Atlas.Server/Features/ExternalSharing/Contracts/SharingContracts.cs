namespace Atlas.Server.Features.ExternalSharing.Contracts;

public sealed record SharingPolicyDto(bool Enabled, string? Version);
public sealed record SaveSharingPolicyRequest(bool Enabled, string? ExpectedVersion);
public sealed record CreateShareLinkRequest(string Name, DateTimeOffset ExpiresAt);
public sealed record ShareLinkDto(Guid Id, Guid PageId, string PageTitle, string Name, int RevisionVersion,
    string CreatedByName, DateTime CreatedAt, DateTime ExpiresAt, DateTime? RevokedAt, string Status,
    DateTime? LastOpenedAt = null, bool Live = false);
public sealed record ShareLinksDto(IReadOnlyList<ShareLinkDto> Items, bool HasMore);
public sealed record IssuedShareLink(ShareLinkDto Link, string Secret);
public sealed record CreatedShareLinkDto(ShareLinkDto Link, string Url);
/// <param name="Live">The link follows a live page (3.6-I Živě): the revision is its latest save, not a publication, dated by that save.</param>
public sealed record SharedPageDto(string Title, string ContentJson, int RevisionVersion,
    DateTime PublishedAt, DateTime ExpiresAt, bool Live = false);
