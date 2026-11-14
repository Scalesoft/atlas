using Atlas.Server.Features.Security;
using Atlas.Server.Features.Security.Domain;
using Xunit;

namespace Atlas.Server.Tests.Security;

/// <summary>Mode inheritance instance → vault → secret, the three-mode matrix (spec 5.1) and the proof binding rules.</summary>
public sealed class ProtectionModeTests
{
    private static readonly Guid VaultId = Guid.NewGuid();
    private static readonly Guid SecretId = Guid.NewGuid();

    [Theory]
    [InlineData(ProtectionMode.Always, null, null, ProtectionMode.Always, "instance")]
    [InlineData(ProtectionMode.Always, ProtectionMode.Never, null, ProtectionMode.Never, "vault")]
    [InlineData(ProtectionMode.Never, null, ProtectionMode.Always, ProtectionMode.Always, "secret")]
    [InlineData(ProtectionMode.Never, ProtectionMode.OutsideTrustedNetworks, ProtectionMode.Never, ProtectionMode.Never, "secret")]
    [InlineData(ProtectionMode.OutsideTrustedNetworks, null, null, ProtectionMode.OutsideTrustedNetworks, "instance")]
    public void Effective_mode_is_the_nearest_explicit_level(ProtectionMode instance, ProtectionMode? vault, ProtectionMode? secret,
        ProtectionMode effective, string source)
    {
        var chain = ProtectionChain.Of(instance, VaultId, vault, SecretId, secret);
        Assert.Equal(effective, chain.Effective);
        Assert.Equal(source, chain.Source.Level);
        Assert.Equal(["instance", "vault", "secret"], chain.Levels.Select(l => l.Level));
        Assert.Equal(ProtectionLevel.SecretLevel, chain.Target.Level);
    }

    [Fact]
    public void A_missing_instance_mode_fails_closed_to_always()
    {
        Assert.Equal(ProtectionMode.Always, ProtectionChain.Of(null, VaultId).Effective);
        Assert.Equal(ProtectionMode.Always, ProtectionModes.FromStored("sometimes"));
        Assert.Null(ProtectionModes.Parse("Always"));
        Assert.Equal("outsideTrustedNetworks", ProtectionMode.OutsideTrustedNetworks.Name());
    }

    [Theory]
    [InlineData(ProtectionMode.Never, false, false)]
    [InlineData(ProtectionMode.Never, true, false)]
    [InlineData(ProtectionMode.OutsideTrustedNetworks, true, false)]
    [InlineData(ProtectionMode.OutsideTrustedNetworks, false, true)]
    [InlineData(ProtectionMode.Always, true, true)]
    [InlineData(ProtectionMode.Always, false, true)]
    public void A_trusted_network_waives_reauthentication_only_outside_always(ProtectionMode mode, bool trustedNetwork, bool required)
    {
        Assert.Equal(required, ProtectionDecision.Requires(mode, trustedNetwork));
    }

    [Fact]
    public void Security_changes_bind_their_proof_to_the_changed_scope()
    {
        var target = Guid.NewGuid();
        Assert.Equal(ReauthTarget.Vault(target), ReauthTarget.Of(new SecurityChange("vault.grant", target)));
        Assert.Equal(ReauthTarget.Secret(target), ReauthTarget.Of(new SecurityChange("secret.protection", target)));
        Assert.Equal(ReauthTarget.Instance, ReauthTarget.Of(new SecurityChange("security.network", target)));
        Assert.Equal(ReauthTarget.Instance, ReauthTarget.Of(new SecurityChange("instance.protection", Guid.Empty)));
    }

    [Fact]
    public void A_proof_is_bound_to_account_stamp_session_operation_target_and_lifetime()
    {
        var actor = new UserAccount { Id = Guid.NewGuid(), Username = "a", SecurityStamp = "stamp-1", IsActive = true };
        var issued = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        var challenge = new ReauthChallenge
        {
            UserId = actor.Id, SecurityStampHash = ReauthChallenge.HashStamp("stamp-1"), SessionId = "s1", Operation = ReauthChallenge.SecurityOperation,
            TargetKind = ReauthTarget.VaultKind, TargetId = VaultId, IssuedAt = issued, ExpiresAt = issued + ReauthChallenge.Lifetime
        };
        Assert.True(challenge.IsPending(issued.AddMinutes(4)));
        Assert.False(challenge.IsPending(issued.AddMinutes(5)));
        Assert.False(challenge.Proves(ReauthChallenge.SecurityOperation, ReauthTarget.Vault(VaultId), issued.AddSeconds(1)));
        challenge.VerifiedAt = issued.AddSeconds(30);
        var now = issued.AddSeconds(60);
        Assert.True(challenge.BelongsTo(actor, "s1") && challenge.Proves(ReauthChallenge.SecurityOperation, ReauthTarget.Vault(VaultId), now));
        Assert.False(challenge.BelongsTo(actor, "s2"));
        Assert.False(challenge.BelongsTo(actor, null));
        Assert.False(challenge.BelongsTo(new UserAccount { Id = actor.Id, SecurityStamp = "stamp-2", IsActive = true }, "s1"));
        Assert.False(challenge.BelongsTo(new UserAccount { Id = Guid.NewGuid(), SecurityStamp = "stamp-1", IsActive = true }, "s1"));
        Assert.False(challenge.Proves(ReauthChallenge.RevealOperation, ReauthTarget.Vault(VaultId), now));
        Assert.False(challenge.Proves(ReauthChallenge.SecurityOperation, ReauthTarget.Vault(Guid.NewGuid()), now));
        Assert.False(challenge.Proves(ReauthChallenge.SecurityOperation, ReauthTarget.Instance, now));
        Assert.False(challenge.Proves(ReauthChallenge.SecurityOperation, ReauthTarget.Vault(VaultId), issued.AddSeconds(150)));
        Assert.Equal("expired", challenge.State(issued.AddSeconds(150)));
        challenge.ConsumedAt = now;
        Assert.False(challenge.Proves(ReauthChallenge.SecurityOperation, ReauthTarget.Vault(VaultId), now));
        Assert.Equal("consumed", challenge.State(now));
    }
}
