namespace Atlas.Server.Features.PersonalApi.Commands;

/// <summary>A WikiAdmin revokes someone's personal token (DELETE /api/admin/api-tokens/{id}).</summary>
public sealed record RevokeUserApiToken(Guid TokenId);
