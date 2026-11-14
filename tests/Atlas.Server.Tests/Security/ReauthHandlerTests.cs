using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Queries;
using Atlas.Server.Features.Security.Reauth;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.Release;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>Fresh re-authentication with the local password over real PostgreSQL: binding to account, stamp, session, operation and
/// target, single use, expiry, the ten-minute window of a verification, and the guard on the V3 grant changes.</summary>
public sealed class ReauthHandlerTests
{
    [PostgreSqlFact]
    public async Task Logout_invalidates_a_retained_verified_proof_not_only_its_window()
    {
        await using var f = await SecurityFixture.CreateAsync(shared: true);
        f.SignIn(f.Admin, session: Guid.NewGuid().ToString("N"));
        var proof = await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new EndReauthWindow());
        f.Present(proof);
        var denied = await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetVaultGrant(f.VaultId,
            new UpdateVaultGrant(f.SpaceAdmin.Id, null, "read", null))));
        Assert.Equal(403, denied.Status);
    }

    [PostgreSqlFact]
    public async Task Password_proof_is_verified_once_and_consumed_once()
    {
        await using var f = await SecurityFixture.CreateAsync(shared: true);
        f.SignIn(f.Admin);
        var started = await f.Invoke<ReauthStarted>(new StartReauth("security", f.VaultId, null));
        Assert.Equal("password", started.Method);
        Assert.Equal("pending", (await f.Invoke<ReauthStatusView>(new GetReauthStatus(started.Id))).State);
        Assert.Equal(new ReauthOutcome(false, "password"), await f.Invoke<ReauthOutcome>(new VerifyReauthPassword(started.Id, "wrong password")));
        var failed = await f.Invoke<ReauthStatusView>(new GetReauthStatus(started.Id));
        Assert.Equal(("pending", "password"), (failed.State, failed.Failure));
        Assert.Equal(new ReauthOutcome(true, null), await f.Invoke<ReauthOutcome>(new VerifyReauthPassword(started.Id, SecurityFixture.AdminPassword)));
        Assert.Equal(new ReauthOutcome(false, "missing"), await f.Invoke<ReauthOutcome>(new VerifyReauthPassword(started.Id, SecurityFixture.AdminPassword)));
        var verified = await f.Invoke<ReauthStatusView>(new GetReauthStatus(started.Id));
        Assert.Equal("verified", verified.State);
        Assert.NotNull(verified.ProofExpiresAt);

        var member = await f.Host.SeedUserAsync("member");
        f.Present(started.Id);
        await f.Invoke(new SetVaultGrant(f.VaultId, new UpdateVaultGrant(member.Id, null, "read", null)));
        Assert.Equal("consumed", (await f.Invoke<ReauthStatusView>(new GetReauthStatus(started.Id))).State);
        // Presenting it again consumes nothing: the next change passes in the session's re-authentication window instead.
        await f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(member.Id, null, "read", null)));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='reauth.consumed' AND \"ReauthChallengeId\"=:id", ("id", started.Id)));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='reauth.window_used' AND \"ReauthChallengeId\"=:id", ("id", started.Id)));
    }

    [PostgreSqlFact]
    public async Task Grant_changes_are_blocked_without_a_verified_re_authentication()
    {
        await using var f = await SecurityFixture.CreateAsync(shared: true);
        var member = await f.Host.SeedUserAsync("member");
        f.SignIn(f.Admin);
        var error = await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(member.Id, null, "read", null))));
        Assert.Equal((403, ReauthRefusal.RequiredCode), (error.Status, error.Code));
        f.Present(Guid.NewGuid());
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(member.Id, null, "read", null))))).Status);
        // A started challenge and a wrong password open no window.
        var started = await f.Invoke<ReauthStarted>(new StartReauth("security", null, f.SecretId));
        Assert.Equal(new ReauthOutcome(false, "password"), await f.Invoke<ReauthOutcome>(new VerifyReauthPassword(started.Id, "wrong password")));
        f.Present(started.Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(member.Id, null, "read", null))))).Status);
        Assert.Null((await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities())).ReauthWindowExpiresAt);
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM \"ResourceGrant\" WHERE \"UserId\"=:u", ("u", member.Id)));
        Assert.Equal(new ReauthOutcome(true, null), await f.Invoke<ReauthOutcome>(new VerifyReauthPassword(started.Id, SecurityFixture.AdminPassword)));
        await f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(member.Id, null, "read", null)));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"ResourceGrant\" WHERE \"UserId\"=:u", ("u", member.Id)));
    }

    /// <summary>Owner decision 28. 9. ("sudo mode"): one verified re-authentication lets every further security change of the same
    /// account, session and stamp pass for ten minutes from the verification, whatever its target, and each covered change is audited
    /// with the challenge that opened the window. Another session, a sign-out, a stamp rotation or the end of the ten minutes close it.</summary>
    [PostgreSqlFact]
    public async Task One_re_authentication_covers_security_changes_of_the_session_for_ten_minutes()
    {
        // Own host: changes the instance protection mode and counts every reauth.consumed row.
        await using var f = await SecurityFixture.CreateAsync();
        var members = new[] { await f.Host.SeedUserAsync("first-member"), await f.Host.SeedUserAsync("second-member"), await f.Host.SeedUserAsync("third-member") };
        f.SignIn(f.Admin, session: "desk");
        var proof = await f.ProveAsync(vaultId: f.VaultId);
        var status = await f.Invoke<ReauthStatusView>(new GetReauthStatus(proof));
        Assert.Equal(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(120), status.WindowExpiresAt - status.ProofExpiresAt);
        Assert.Equal(status.WindowExpiresAt, (await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities())).ReauthWindowExpiresAt);
        f.Present(null);
        foreach (var member in members)
        {
            await f.Invoke(new SetSecretGrant(f.OtherSecretId, new UpdateVaultGrant(member.Id, null, "read", null)));
        }
        var instance = await f.Invoke<ProtectionState>(new GetProtection("instance", null));
        await f.Invoke(new SetProtection("instance", null, new UpdateProtection("outsideTrustedNetworks", instance.Version)));
        Assert.Equal(4L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='reauth.window_used' AND \"ReauthChallengeId\"=:id AND \"ActorId\"=:a",
            ("id", proof), ("a", f.Admin.Id)));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='reauth.consumed'"));

        // The window never serves a value release: the effective mode still asks for a proof, and a security proof is none.
        using (var scope = f.Host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
            using var tx = session.BeginTransaction();
            var policy = scope.ServiceProvider.GetRequiredService<IReleasePolicy>();
            Assert.True((await policy.EvaluateAsync(f.Admin, f.SecretId, f.Http.HttpContext!, CancellationToken.None)).RequiresReauth);
            Assert.False(await policy.TryConsumeProofAsync(f.Admin, f.Http.HttpContext!, f.SecretId, "reveal", proof, CancellationToken.None));
            await tx.CommitAsync();
        }

        // Another session of the same account has no window.
        f.SignIn(f.Admin, session: "laptop");
        var other = await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(members[0].Id, null, "read", null))));
        Assert.Equal((403, ReauthRefusal.RequiredCode), (other.Status, other.Code));
        Assert.Null((await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities())).ReauthWindowExpiresAt);

        // Nine minutes after the verification the window is still open; ten minutes after it, it is closed.
        f.SignIn(f.Admin, session: "desk");
        await f.Host.ExecuteAsync("UPDATE \"ReauthChallenge\" SET \"IssuedAt\"=\"IssuedAt\"-interval '9 minutes', \"ExpiresAt\"=\"ExpiresAt\"-interval '9 minutes', " +
            "\"VerifiedAt\"=\"VerifiedAt\"-interval '9 minutes' WHERE \"Id\"=:id", proof);
        await f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(members[0].Id, null, "read", null)));
        await f.Host.ExecuteAsync("UPDATE \"ReauthChallenge\" SET \"IssuedAt\"=\"IssuedAt\"-interval '1 minute', \"ExpiresAt\"=\"ExpiresAt\"-interval '1 minute', " +
            "\"VerifiedAt\"=\"VerifiedAt\"-interval '1 minute' WHERE \"Id\"=:id", proof);
        var late = await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(members[1].Id, null, "read", null))));
        Assert.Equal((403, ReauthRefusal.RequiredCode), (late.Status, late.Code));

        // A sign-out ends a fresh window on the server, also for a copy of the session's cookie.
        await f.ProveAsync(secretId: f.SecretId);
        await f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(members[1].Id, null, "read", null)));
        await f.Invoke(new EndReauthWindow());
        f.Present(null);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(members[2].Id, null, "read", null))))).Status);
        Assert.Null((await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities())).ReauthWindowExpiresAt);

        // A stamp rotation (password change, reset, privilege change) ends a fresh window at once.
        await f.ProveAsync(secretId: f.SecretId);
        await f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(members[2].Id, null, "read", null)));
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id", f.Admin.Id);
        var admin = f.Admin;
        admin.SecurityStamp = "rotated";
        f.SignIn(admin, session: "desk");
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetSecretGrant(f.SecretId, new UpdateVaultGrant(f.SpaceAdmin.Id, null, "read", null))))).Status);
        Assert.Null((await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities())).ReauthWindowExpiresAt);
    }

    [PostgreSqlFact]
    public async Task A_proof_belongs_to_its_session_and_stamp_and_expires()
    {
        await using var f = await SecurityFixture.CreateAsync(shared: true);
        var member = await f.Host.SeedUserAsync("member");
        f.SignIn(f.Admin, session: "first");
        var proof = await f.ProveAsync(vaultId: f.VaultId);
        // Another session of the same account cannot use it, nor see it.
        f.SignIn(f.Admin, session: "second");
        f.Present(proof);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetVaultGrant(f.VaultId, new UpdateVaultGrant(member.Id, null, "read", null))))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStatusView>(new GetReauthStatus(proof)))).Status);
        // Another account cannot either.
        f.SignIn(f.SpaceAdmin, session: "first");
        f.Present(proof);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetVaultGrant(f.VaultId, new UpdateVaultGrant(member.Id, null, "read", null))))).Status);
        // A proof older than two minutes is dead.
        f.SignIn(f.Admin, session: "first");
        f.Present(proof);
        await f.Host.ExecuteAsync("UPDATE \"ReauthChallenge\" SET \"IssuedAt\"=\"IssuedAt\"-interval '10 minutes', \"ExpiresAt\"=\"ExpiresAt\"-interval '10 minutes', " +
            "\"VerifiedAt\"=\"VerifiedAt\"-interval '10 minutes' WHERE \"Id\"=:id", proof);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetVaultGrant(f.VaultId, new UpdateVaultGrant(member.Id, null, "read", null))))).Status);
        Assert.Equal("expired", (await f.Invoke<ReauthStatusView>(new GetReauthStatus(proof))).State);
        // A challenge not verified in five minutes cannot be verified any more.
        var late = await f.Invoke<ReauthStarted>(new StartReauth("security", null, null));
        await f.Host.ExecuteAsync("UPDATE \"ReauthChallenge\" SET \"IssuedAt\"=\"IssuedAt\"-interval '6 minutes', \"ExpiresAt\"=\"ExpiresAt\"-interval '6 minutes' WHERE \"Id\"=:id", late.Id);
        Assert.Equal(new ReauthOutcome(false, "missing"), await f.Invoke<ReauthOutcome>(new VerifyReauthPassword(late.Id, SecurityFixture.AdminPassword)));
        // A rotated security stamp (password change, reset) invalidates a verified proof.
        var rotated = await f.ProveAsync(vaultId: f.VaultId);
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:id", f.Admin.Id);
        var admin = f.Admin;
        admin.SecurityStamp = "rotated";
        f.SignIn(admin, session: "first");
        f.Present(rotated);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetVaultGrant(f.VaultId, new UpdateVaultGrant(member.Id, null, "read", null))))).Status);
    }

    [PostgreSqlFact]
    public async Task Start_needs_a_session_a_valid_target_and_a_method_that_can_prove_freshness()
    {
        await using var f = await SecurityFixture.CreateAsync(shared: true);
        f.SignIn(f.Admin);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("reveal", null, null)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("security", f.VaultId, f.SecretId)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("everything", null, null)))).Status);
        // Self-test is for provider accounts only.
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("selftest", null, null)))).Status);
        f.SignIn(f.SpaceAdmin);
        var unsupported = await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("security", null, null)));
        Assert.Equal((403, ReauthRefusal.UnsupportedCode), (unsupported.Status, unsupported.Code));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("selftest", null, null)))).Status);
        var me = await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities());
        Assert.Equal(("unsupported", null), (me.FreshAuth, me.FreshAuthMethod));
        f.SignIn(f.Admin);
        me = await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities());
        Assert.Equal(("supported", "password", true), (me.FreshAuth, me.FreshAuthMethod, me.ManagesAllVaults));
        f.Http.HttpContext!.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("security", null, null)))).Status);
    }

    [PostgreSqlFact]
    public async Task Provider_accounts_are_unknown_until_a_fresh_auth_time_was_seen()
    {
        var provider = new AtlasOidcProvider { Id = "hub", DisplayName = "Hub", Issuer = "https://hub.test/", ClientId = "atlas" };
        // Own host: its OIDC provider configuration and the provider's fresh-auth verdict are the installation's.
        await using var f = await SecurityFixture.CreateAsync(oidc:new AtlasOidcOptions { Providers = [provider] });
        await f.Host.ExecuteAsync("INSERT INTO \"ExternalIdentity\"(\"Id\",\"UserId\",\"Issuer\",\"Subject\",\"ActiveFrom\") VALUES (gen_random_uuid(),:id,'https://hub.test/','sub-1',now())",
            f.SpaceAdmin.Id);
        f.SignIn(f.SpaceAdmin, method: "oidc:hub");
        var me = await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities());
        Assert.Equal(("unknown", "oidc"), (me.FreshAuth, me.FreshAuthMethod));
        var started = await f.Invoke<ReauthStarted>(new StartReauth("security", f.VaultId, null));
        Assert.Equal(("oidc", "hub"), (started.Method, started.ProviderId));

        // A callback without auth_time proves nothing and marks the provider unsupported, a stale one likewise.
        Assert.Equal(new ReauthOutcome(false, "auth_time_missing"),
            await f.Invoke<ReauthOutcome>(new CompleteOidcReauth(started.Id, "hub", "https://hub.test/", "sub-1", null)));
        Assert.Equal("failed", (await f.Invoke<ReauthStatusView>(new GetReauthStatus(started.Id))).State);
        me = await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities());
        Assert.Equal("unsupported", me.FreshAuth);
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ReauthStarted>(new StartReauth("security", f.VaultId, null)));
        Assert.Equal(ReauthRefusal.UnsupportedCode, refused.Code);

        // The WikiAdmin self-test (a linked admin) records supported after a fresh auth_time.
        await f.Host.ExecuteAsync("INSERT INTO \"ExternalIdentity\"(\"Id\",\"UserId\",\"Issuer\",\"Subject\",\"ActiveFrom\") VALUES (gen_random_uuid(),:id,'https://hub.test/','admin-sub',now())",
            f.Admin.Id);
        f.SignIn(f.Admin, method: "oidc:hub");
        var test = await f.Invoke<ReauthStarted>(new StartReauth("selftest", null, null, "hub"));
        Assert.Equal(new ReauthOutcome(false, "identity"),
            await f.Invoke<ReauthOutcome>(new CompleteOidcReauth(test.Id, "hub", "https://hub.test/", "sub-1", DateTime.UtcNow)));
        test = await f.Invoke<ReauthStarted>(new StartReauth("selftest", null, null, "hub"));
        Assert.Equal(new ReauthOutcome(false, "auth_time_stale"),
            await f.Invoke<ReauthOutcome>(new CompleteOidcReauth(test.Id, "hub", "https://hub.test/", "admin-sub", DateTime.UtcNow.AddMinutes(-10))));
        var providers = await f.Invoke<IReadOnlyList<FreshAuthProviderView>>(new GetFreshAuthProviders());
        Assert.Equal(("unsupported", true, false, true), (providers[0].Verdict, providers[0].AuthTimePresent, providers[0].AuthTimeFresh, providers[0].SelfTest));
        test = await f.Invoke<ReauthStarted>(new StartReauth("selftest", null, null, "hub"));
        Assert.Equal(new ReauthOutcome(true, null),
            await f.Invoke<ReauthOutcome>(new CompleteOidcReauth(test.Id, "hub", "https://hub.test/", "admin-sub", DateTime.UtcNow)));
        providers = await f.Invoke<IReadOnlyList<FreshAuthProviderView>>(new GetFreshAuthProviders());
        Assert.Equal(("supported", true, true), (providers[0].Verdict, providers[0].AuthTimePresent, providers[0].AuthTimeFresh));
        // A self-test proof authorizes nothing.
        f.Present(test.Id);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetVaultGrant(f.VaultId, new UpdateVaultGrant(f.SpaceAdmin.Id, null, "read", null))))).Status);
        f.SignIn(f.SpaceAdmin, method: "oidc:hub");
        Assert.Equal("supported", (await f.Invoke<SecurityCapabilitiesView>(new GetSecurityCapabilities())).FreshAuth);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<IReadOnlyList<FreshAuthProviderView>>(new GetFreshAuthProviders()))).Status);
    }
}
