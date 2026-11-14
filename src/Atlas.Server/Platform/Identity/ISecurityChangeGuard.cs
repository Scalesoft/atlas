namespace Atlas.Server.Platform.Identity;

/// <summary>
/// The one extra condition of a security change (vault and secret grants, including the grant an approved access request writes;
/// granting manage on a space or a page, turning WikiAdmin on or off, revoke-all (owner decision 3a, 29. 9.); protection modes,
/// trusted networks and proxies, password resets and local group membership, the OIDC providers, a backup key, a personal API token), checked after authorization and validation and before the write. Rejecting an access request is
/// no security change: it cannot widen access. The server registers ReauthSecurityChangeGuard (Features/Security, WP4 V4): a fresh
/// re-authentication proof bound to the change's scope, consumed in the change's transaction, or the ten-minute re-authentication
/// window of the same session. There is no permissive default; a host without the security module cannot run a security change.
/// </summary>
public interface ISecurityChangeGuard
{
    /// <summary>Throws an ApiException when the change may not proceed now.</summary>
    Task RequireAsync(UserAccount actor, SecurityChange change, CancellationToken ct);
}

/// <summary>What is being changed: Kind names the operation (vault.grant, secret.grant, space.grant, page.grant, account.wiki_admin,
/// account.revoke_all, account.password_reset, group.membership, vault.protection, secret.protection, instance.protection, security.network, security.proxy,
/// security.proxy_hops, backup.destination, oidc.configuration, api_token.issue), TargetId its target (Guid.Empty for global ones).</summary>
public sealed record SecurityChange(string Kind, Guid TargetId);
