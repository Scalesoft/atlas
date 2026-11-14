namespace Atlas.Server.Features.PersonalApi.Contracts;

/// <summary>One personal token of the wiki for its WikiAdmins (GET /api/admin/api-tokens). CreatedAt is its ActiveFrom, RevokedAt its
/// ActiveTo. LastUsedAt is always null: authentication records no use of a token.</summary>
public sealed record AdminApiToken(Guid Id, Guid UserId, string UserName, string Name, string[] Scopes, DateTime CreatedAt,
    DateTime ExpiresAt, DateTime? LastUsedAt, DateTime? RevokedAt)
{
    /// <summary>rest and/or mcp.</summary>
    public string[] Channels { get; init; } = [ApiChannels.Rest];
}
