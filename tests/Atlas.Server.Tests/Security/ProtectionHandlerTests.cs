using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Security;
using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.Security.Contracts;
using Atlas.Server.Features.Security.Domain;
using Atlas.Server.Features.Security.Queries;
using Atlas.Server.Features.Security.Reauth;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Release;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>Protection modes over real PostgreSQL: the owner default, inheritance and explicit levels, who may change what, the
/// guard, CAS and no-op, history and audit, configuration versions, and the release decision service. Each test has a host of its
/// own: protection changes bump the installation's SecurityConfiguration version (row Id 1), which the tests assert.</summary>
public sealed class ProtectionHandlerTests
{
    [PostgreSqlFact]
    public async Task Fresh_configuration_is_always_with_no_trusted_network_and_is_inherited()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin);
        var instance = await f.Invoke<ProtectionState>(new GetProtection("instance", null));
        Assert.Equal(("always", "always", "instance", "1"), (instance.Explicit, instance.Effective, instance.Source, instance.ConfigurationVersion));
        Assert.Equal(new ProtectionImpactView(1, 2), instance.Impact);
        Assert.True(instance.Preview.ReauthRequired);
        Assert.Equal("supported", instance.Preview.FreshAuth);
        var secret = await f.Invoke<ProtectionState>(new GetProtection("secret", f.SecretId));
        Assert.Equal((null, "always", "instance"), (secret.Explicit, secret.Effective, secret.Source));
        Assert.Equal(["instance", "vault", "secret"], secret.Chain.Select(c => c.Level));
        Assert.Equal(["Provozní hesla", "Router"], secret.Chain.Skip(1).Select(c => c.Name));
        Assert.Equal(0L, await f.CountAsync("SELECT count(*) FROM \"TrustedAddress\""));
    }

    [PostgreSqlFact]
    public async Task Levels_inherit_and_override_with_history_audit_and_new_versions()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin);
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("outsideTrustedNetworks", null)));
        var vault = await f.Invoke<ProtectionState>(new GetProtection("vault", f.VaultId));
        Assert.Equal(("outsideTrustedNetworks", "outsideTrustedNetworks", "vault", "2"), (vault.Explicit, vault.Effective, vault.Source, vault.ConfigurationVersion));
        Assert.Equal(new ProtectionImpactView(0, 2), vault.Impact);
        await f.ProveAsync(secretId: f.SecretId);
        await f.Invoke(new SetProtection("secret", f.SecretId, new UpdateProtection("never", null)));
        var secret = await f.Invoke<ProtectionState>(new GetProtection("secret", f.SecretId));
        Assert.Equal(("never", "secret"), (secret.Effective, secret.Source));
        Assert.False(secret.Preview.ReauthRequired);
        var other = await f.Invoke<ProtectionState>(new GetProtection("secret", f.OtherSecretId));
        Assert.Equal(("outsideTrustedNetworks", "vault"), (other.Effective, other.Source));
        Assert.True(other.Preview.ReauthRequired);

        // Back to inheriting: the vault row closes, the secrets fall back to the instance default.
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection(null, vault.Version)));
        other = await f.Invoke<ProtectionState>(new GetProtection("secret", f.OtherSecretId));
        Assert.Equal(("always", "instance", "4"), (other.Effective, other.Source, other.ConfigurationVersion));
        var instance = await f.Invoke<ProtectionState>(new GetProtection("instance", null));
        Assert.Equal(new ProtectionImpactView(1, 1), instance.Impact);

        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM \"ProtectionSetting\" WHERE \"VaultId\" IS NOT NULL"));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"ProtectionSetting\" WHERE \"VaultId\"=:v AND \"SecretId\" IS NULL AND \"ActiveTo\" IS NOT NULL", ("v", f.VaultId)));
        Assert.Equal(2L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='protection.set' AND \"ActorId\"=:a", ("a", f.Admin.Id)));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='protection.cleared'"));
        Assert.Equal(3L, await f.CountAsync("SELECT count(*) FROM \"AccessAuditEvent\" WHERE \"Action\"='reauth.consumed' AND \"ActorId\"=:a", ("a", f.Admin.Id)));
        Assert.Equal(4L, await f.CountAsync("SELECT max(\"Version\") FROM \"SecurityConfigurationRevision\""));
        Assert.Equal(1L, await f.CountAsync("SELECT count(*) FROM \"SecurityConfigurationRevision\" WHERE \"ActiveTo\" IS NULL AND \"ChangedBy\"=:a", ("a", f.Admin.Id)));
        var audited = await f.CountAsync("""
            SELECT count(*) FROM "AccessAuditView" WHERE "Action"='protection.set' AND "VaultId"=:v AND "SpaceId"=:s AND "ProtectionMode" IS NOT NULL
            """, ("v", f.VaultId), ("s", f.SpaceId));
        Assert.Equal(2L, audited);
        // History rows are immutable (IntegrityRules).
        await Platform.IntegrityRefusal.AssertAsync(f.Host, IntegrityRules.HistoryImmutable, async s =>
            (await s.QueryOver<ProtectionSetting>().Where(x => x.VaultId == f.VaultId && x.SecretId == null).Take(1).SingleOrDefaultAsync())!.Mode = "never");
    }

    [PostgreSqlFact]
    public async Task Changes_need_manage_a_fresh_re_authentication_and_a_current_version()
    {
        await using var f = await SecurityFixture.CreateAsync();
        var reader = await f.Host.SeedUserAsync("reader");
        var outsider = await f.Host.SeedUserAsync("outsider");
        f.SignIn(f.Admin);
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetVaultGrant(f.VaultId, new UpdateVaultGrant(reader.Id, null, "read", null)));

        f.SignIn(reader);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("never", null))))).Status);
        Assert.Equal("always", (await f.Invoke<ProtectionState>(new GetProtection("secret", f.SecretId))).Effective);
        Assert.Null((await f.Invoke<ProtectionState>(new GetProtection("secret", f.SecretId))).Impact);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ProtectionState>(new GetProtection("instance", null)))).Status);
        f.SignIn(outsider);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<ProtectionState>(new GetProtection("vault", f.VaultId)))).Status);

        // The space Admin manages its vault but cannot prove a fresh sign-in; the wiki Admin needs one in its own session.
        f.SignIn(f.SpaceAdmin);
        var missing = await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("never", null))));
        Assert.Equal((403, ReauthRefusal.UnsupportedCode), (missing.Status, missing.Code));
        f.SignIn(f.Admin, session: "second");
        var required = await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetProtection("secret", f.SecretId, new UpdateProtection("never", null))));
        Assert.Equal((403, ReauthRefusal.RequiredCode), (required.Status, required.Code));
        var proof = await f.ProveAsync(vaultId: f.VaultId);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("never", "stale"))))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("sometimes", null))))).Status);
        // The refused attempts rolled back; the first change that succeeds consumes the proof, the next ones pass in its window.
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("never", null)));
        Assert.Equal("consumed", (await f.Invoke<ReauthStatusView>(new GetReauthStatus(proof))).State);
        var instance = await f.Invoke<ProtectionState>(new GetProtection("instance", null));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke(new SetProtection("instance", null, new UpdateProtection(null, instance.Version))))).Status);
        await f.Invoke(new SetProtection("instance", null, new UpdateProtection("outsideTrustedNetworks", instance.Version)));
        Assert.Equal("outsideTrustedNetworks", (await f.Invoke<ProtectionState>(new GetProtection("instance", null))).Effective);
    }

    [PostgreSqlFact]
    public async Task Overrides_list_explicit_vault_and_secret_modes_and_the_audit_names_them()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin);
        Assert.Empty(await f.Invoke<IReadOnlyList<ProtectionOverrideView>>(new ListProtectionOverrides()));
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("outsideTrustedNetworks", null)));
        await f.ProveAsync(secretId: f.SecretId);
        await f.Invoke(new SetProtection("secret", f.SecretId, new UpdateProtection("never", null)));
        var overrides = await f.Invoke<IReadOnlyList<ProtectionOverrideView>>(new ListProtectionOverrides());
        Assert.Equal([(f.VaultId, (Guid?)null, "outsideTrustedNetworks"), (f.VaultId, f.SecretId, "never")],
            overrides.Select(o => (o.VaultId, o.SecretId, o.Mode)));
        Assert.Equal(("Provozní hesla", "Router"), (overrides[1].VaultName, overrides[1].SecretTitle));
        var audit = await f.Invoke<Atlas.Server.Features.AccessAudit.Contracts.AccessAuditList>(
            new Atlas.Server.Features.AccessAudit.Queries.GetAccessAudit(null, 10));
        var set = audit.Items.First(i => i.Action == "protection.set");
        Assert.Equal(("never", "3", "Provozní hesla", "Router"), (set.ProtectionMode, set.ConfigurationVersion, set.VaultName, set.SecretTitle));

        f.SignIn(f.SpaceAdmin);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.Invoke<IReadOnlyList<ProtectionOverrideView>>(new ListProtectionOverrides()))).Status);
    }

    [PostgreSqlFact]
    public async Task No_op_changes_keep_the_version()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin);
        var instance = await f.Invoke<ProtectionState>(new GetProtection("instance", null));
        await f.ProveAsync();
        await f.Invoke(new SetProtection("instance", null, new UpdateProtection("always", instance.Version)));
        await f.ProveAsync(secretId: f.SecretId);
        await f.Invoke(new SetProtection("secret", f.SecretId, new UpdateProtection(null, null)));
        Assert.Equal("1", (await f.Invoke<ProtectionState>(new GetProtection("instance", null))).ConfigurationVersion);
    }

    [PostgreSqlFact]
    public async Task Release_decision_reports_mode_chain_client_rule_and_support()
    {
        await using var f = await SecurityFixture.CreateAsync();
        f.SignIn(f.Admin, peer: "192.168.10.5");
        await f.ProveAsync();
        var version = (await f.Invoke<TrustedNetworkList>(new ListTrustedNetworks())).Version;
        var office = await f.Invoke<TrustedAddressSaved>(new SaveTrustedAddress(null, TrustedAddress.NetworkPurpose,
            new SaveTrustedAddressRequest("Kancelář", "lan", "192.168.10.0/24", true, version)));
        await f.ProveAsync(vaultId: f.VaultId);
        await f.Invoke(new SetProtection("vault", f.VaultId, new UpdateProtection("outsideTrustedNetworks", null)));

        async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = f.Host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
            using var tx = db.BeginTransaction();
            var result = await work(scope.ServiceProvider);
            await tx.CommitAsync();
            return result;
        }
        Task<ProtectionDecision> Decide(UserAccount actor) => InScope(services => services.GetRequiredService<ReleasePolicy>()
            .DecideAsync(actor, f.VaultId, f.SecretId, ClientContext.From(f.Http.HttpContext), CancellationToken.None));
        Task<ReleaseDecision> Evaluate(UserAccount actor, Guid secretId) => InScope(services => services.GetRequiredService<IReleasePolicy>()
            .EvaluateAsync(actor, secretId, f.Http.HttpContext!, CancellationToken.None));

        var inside = await Decide(f.Admin);
        Assert.Equal((ProtectionMode.OutsideTrustedNetworks, "vault"), (inside.EffectiveMode, inside.Chain.Source.Level));
        Assert.Equal(("192.168.10.5", "direct"), (inside.Client.Address?.ToString(), inside.Client.Reason));
        Assert.Equal(office.Id, inside.Network?.Id);
        Assert.True(inside.NetworkException);
        Assert.False(inside.ReauthRequired || inside.Blocked);
        Assert.Equal((FreshAuthSupport.Supported, "password"), (inside.Support, inside.Method?.Method));
        Assert.Equal(3, inside.ConfigurationVersion);
        Assert.Equal(new ReleaseDecision("outsideTrustedNetworks", "vault", false, true, "Kancelář"), await Evaluate(f.Admin, f.SecretId));

        // A spoofed forwarded address from outside is no internal client.
        f.SignIn(f.Admin, peer: "203.0.113.7", headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "192.168.10.5" });
        var spoofed = await Decide(f.Admin);
        Assert.Equal(("unclear", null), (spoofed.Client.Source.ToString().ToLowerInvariant(), spoofed.Network));
        Assert.True(spoofed.ReauthRequired);
        Assert.False(spoofed.Blocked);
        Assert.Equal(new ReleaseDecision("outsideTrustedNetworks", "vault", true, true, null), await Evaluate(f.Admin, f.SecretId));

        // A reveal proof is consumed once, for its secret and operation only; a security proof is no reveal proof.
        var reveal = await f.ProveAsync("reveal", secretId: f.SecretId);
        var security = await f.ProveAsync(secretId: f.SecretId);
        Task<bool> Consume(Guid secretId, string operation, Guid proof) => InScope(services => services.GetRequiredService<IReleasePolicy>()
            .TryConsumeProofAsync(f.Admin, f.Http.HttpContext!, secretId, operation, proof, CancellationToken.None));
        Assert.False(await Consume(f.SecretId, "reveal", security));
        Assert.False(await Consume(f.OtherSecretId, "reveal", reveal));
        Assert.False(await Consume(f.SecretId, "copy", reveal));
        Assert.False(await Consume(f.SecretId, "security", security));
        Assert.True(await Consume(f.SecretId, "reveal", reveal));
        Assert.False(await Consume(f.SecretId, "reveal", reveal));
        var copy = await f.ProveAsync("copy", secretId: f.SecretId);
        f.SignIn(f.Admin, session: "other");
        Assert.False(await Consume(f.SecretId, "copy", copy));

        // Always is never waived by a network; without any way to re-authenticate the release is blocked (nepodporováno).
        f.SignIn(f.SpaceAdmin, peer: "192.168.10.5");
        await f.Host.ExecuteAsync("UPDATE \"SecurityConfiguration\" SET \"Version\"=\"Version\"+1 WHERE \"Id\"=1; " +
            "UPDATE \"ProtectionSetting\" SET \"ActiveTo\"=clock_timestamp() WHERE \"VaultId\"=:id AND \"ActiveTo\" IS NULL", f.VaultId);
        var blocked = await Decide(f.SpaceAdmin);
        Assert.Equal((ProtectionMode.Always, true, true, false), (blocked.EffectiveMode, blocked.ReauthRequired, blocked.Blocked, blocked.NetworkException));
        Assert.Equal(office.Id, blocked.Network?.Id);
        Assert.Equal(4, blocked.ConfigurationVersion);
        Assert.Equal(new ReleaseDecision("always", "instance", true, false, null), await Evaluate(f.SpaceAdmin, f.SecretId));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Evaluate(f.Admin, Guid.NewGuid()))).Status);
    }
}
