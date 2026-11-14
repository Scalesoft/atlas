namespace Atlas.Server.Features.Access.Contracts;

/// <summary>What revoke-all closes: direct space, page, vault and secret grants, group memberships and live personal API tokens.</summary>
public sealed record RevokeAllImpact(int Grants, int Groups, int Tokens);
