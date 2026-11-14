using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Vaults;

/// <summary>Vault and secret access levels (WP4 V3) over real PostgreSQL: the owner decisions of 27. 9. as a matrix, the access
/// explanation, grant management with its history and audit, the grant candidates, security/me and the post-commit refresh.</summary>
public sealed class VaultAccessTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";
    private static readonly VaultAccessLevel[] Levels = [VaultAccessLevel.Discover, VaultAccessLevel.Read, VaultAccessLevel.Manage];

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Levels_follow_the_space_admin_role_and_explicit_grants_only()
    {
        await using var f = await VaultFixture.CreateAsync();
        var (vault, router, mail) = await AttachAsync(f);
        var expiredAdmin = await f.Host.SeedUserAsync("expired-admin");
        var editor = await f.Host.SeedUserAsync("page-editor");
        var otherAdmin = await f.Host.SeedUserAsync("other-admin");
        var direct = await f.Host.SeedUserAsync("direct");
        var member = await f.Host.SeedUserAsync("member");
        var secretOnly = await f.Host.SeedUserAsync("secret-only");
        var expired = await f.Host.SeedUserAsync("expired");
        var future = await f.Host.SeedUserAsync("future");
        var group = await f.Host.SeedGroupAsync("Provoz", member);
        await f.Host.SeedAsync(async session =>
        {
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = expiredAdmin.Id, Role = (int)SpaceRole.Admin,
                StartAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(-1) });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = editor.Id, Role = (int)SpaceRole.Writer });
        });
        await f.Host.SeedSpaceAsync("Jiný", (otherAdmin, SpaceRole.Admin));
        await Grant(f, f.SpaceAdmin, vault, direct, "read");
        await f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(null, group, "discover", null)));
        await f.As(f.SpaceAdmin, new SetSecretGrant(router, new UpdateVaultGrant(member.Id, null, "read", null)));
        await f.As(f.SpaceAdmin, new SetSecretGrant(router, new UpdateVaultGrant(secretOnly.Id, null, "manage", null)));
        // A lapsed grant cannot be written any more (Rules.RequireFutureEnd); one from before the rule still exists.
        await f.Host.SeedAsync(async session => await session.CreateSQLQuery("""
            INSERT INTO "ResourceGrant"("Id","VaultId","VaultSpaceId","UserId","Role","StartAt","ExpiresAt","ActiveFrom")
            VALUES (gen_random_uuid(), :vault, :space, :user, 20, now() - interval '2 days', now() - interval '1 day', clock_timestamp())
            """).SetParameter("vault", vault).SetParameter("space", f.SpaceId).SetParameter("user", expired.Id).ExecuteUpdateAsync());
        await Grant(f, f.SpaceAdmin, vault, future, "read", DateTimeOffset.UtcNow.AddDays(1));

        await AssertVault(f, f.SpaceAdmin, vault, VaultAccessLevel.Manage);
        await AssertSecret(f, f.SpaceAdmin, router, VaultAccessLevel.Manage);
        // A WikiAdmin manages every vault and secret like a space Admin; page roles, other spaces and lapsed grants give nothing.
        await AssertVault(f, f.Admin, vault, VaultAccessLevel.Manage);
        await AssertSecret(f, f.Admin, router, VaultAccessLevel.Manage);
        Assert.Equal(4, (await f.As<IReadOnlyList<VaultSecretView>>(f.Admin, new ListVaultSecrets(vault))).Count);
        using (var scope = f.Host.Services.CreateScope())
        {
            var decision = await scope.ServiceProvider.GetRequiredService<VaultGrantSnapshots>().AuthorizeVaultAsync(f.Admin, vault, VaultAccessLevel.Manage, CancellationToken.None);
            Assert.True(decision.WikiAdmin && !decision.SpaceAdmin && decision.Until is null);
        }
        foreach (var nobody in new[] { expiredAdmin, editor, otherAdmin, expired, future })
        {
            await AssertVault(f, nobody, vault, VaultAccessLevel.None);
            await AssertSecret(f, nobody, router, VaultAccessLevel.None);
            await Denied(404, () => f.As<IReadOnlyList<VaultSecretView>>(nobody, new ListVaultSecrets(vault)));
        }
        await AssertVault(f, direct, vault, VaultAccessLevel.Read);
        await AssertSecret(f, direct, mail, VaultAccessLevel.Read);
        // The highest applicable grant wins: the group's vault discover everywhere, the direct secret read on that secret.
        await AssertVault(f, member, vault, VaultAccessLevel.Discover);
        await AssertSecret(f, member, mail, VaultAccessLevel.Discover);
        await AssertSecret(f, member, router, VaultAccessLevel.Read);
        // A secret grant reaches only its secret; the vault is discoverable so the secret can be found, nothing more.
        await AssertSecret(f, secretOnly, router, VaultAccessLevel.Manage);
        await AssertSecret(f, secretOnly, mail, VaultAccessLevel.None);
        await AssertVault(f, secretOnly, vault, VaultAccessLevel.Discover);

        // Listings: the space Admin and vault grantees see every entry, a secret grantee only its own; the level per entry.
        Assert.Equal(4, (await f.As<IReadOnlyList<VaultSecretView>>(direct, new ListVaultSecrets(vault))).Count);
        var own = Assert.Single(await f.As<IReadOnlyList<VaultSecretView>>(secretOnly, new ListVaultSecrets(vault)));
        Assert.Equal((router, "manage"), (own.SecretId, own.Level));
        Assert.Equal("read", (await f.As<IReadOnlyList<VaultSecretView>>(member, new ListVaultSecrets(vault))).Single(s => s.SecretId == router).Level);
        // The registry list stays for registry managers, with their own level.
        Assert.Equal("manage", Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.Admin, new ListVaults()), v => v.SpaceId == f.SpaceId).Level);
        Assert.Equal("manage", Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults())).Level);
        Assert.Empty(await f.As<IReadOnlyList<VaultView>>(direct, new ListVaults()));

        // A removed secret is missing for everyone, and its grants no longer reveal the vault.
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = now() WHERE \"Id\" = :id", router);
        await AssertSecret(f, f.SpaceAdmin, router, VaultAccessLevel.None);
        await AssertSecret(f, secretOnly, router, VaultAccessLevel.None);
        await AssertVault(f, secretOnly, vault, VaultAccessLevel.None);
        var registry = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault));
        Assert.NotNull(Assert.Single(registry, s => s.SecretId == router).RemovedAt);
        Assert.DoesNotContain(await f.As<IReadOnlyList<VaultSecretView>>(direct, new ListVaultSecrets(vault)), s => s.SecretId == router);
        // Restored under the same id (KeePass entry back), its grants apply again.
        await f.Host.ExecuteAsync("UPDATE \"VaultSecret\" SET \"RemovedAt\" = NULL WHERE \"Id\" = :id", router);
        await AssertSecret(f, secretOnly, router, VaultAccessLevel.Manage);

        // An archived vault is missing for everyone; its grants and their history stay.
        await f.Operate(f.SpaceAdmin, o => o.ArchiveAsync(vault, CancellationToken.None));
        foreach (var actor in new[] { f.Admin, f.SpaceAdmin, direct, member, secretOnly })
        {
            await AssertVault(f, actor, vault, VaultAccessLevel.None);
            await AssertSecret(f, actor, mail, VaultAccessLevel.None);
        }
        await Denied(404, () => f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault)));
        using var session = f.Host.OpenSession();
        Assert.Equal(6L, await session.CreateSQLQuery("SELECT count(*) FROM \"ResourceGrant\" WHERE \"VaultId\" = :vault AND \"ActiveTo\" IS NULL")
            .SetParameter("vault", vault).UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Access_explains_every_source_with_validity_and_shows_management_detail_only_to_managers()
    {
        await using var f = await VaultFixture.CreateAsync();
        var (vault, router, _) = await AttachAsync(f);
        var temporary = await f.Host.SeedUserAsync("temporary-admin");
        var member = await f.Host.SeedUserAsync("member");
        var reader = await f.Host.SeedUserAsync("reader");
        var group = await f.Host.SeedGroupAsync("Provoz", member);
        var adminUntil = DateTime.UtcNow.AddDays(10);
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = temporary.Id,
            Role = (int)SpaceRole.Admin, ExpiresAt = adminUntil }));
        var readerUntil = DateTimeOffset.UtcNow.AddDays(5);
        await Grant(f, f.SpaceAdmin, vault, reader, "read", null, readerUntil);
        await f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(null, group, "discover", null)));
        await f.As(temporary, new SetSecretGrant(router, new UpdateVaultGrant(member.Id, null, "read", null, null, readerUntil)));

        var state = await f.As<VaultAccessState>(f.SpaceAdmin, new GetSecretAccess(router));
        Assert.True(state.CanManage);
        Assert.Equal(vault, state.VaultId);
        Assert.Equal(router, state.SecretId);
        Assert.Equal("Router", state.SecretTitle);
        Assert.Equal("Provoz", state.SpaceName);
        var admin = Assert.Single(state.People, p => p.UserId == temporary.Id);
        var adminSource = Assert.Single(admin.Sources);
        Assert.Equal(("spaceAdmin", "manage"), (adminSource.Kind, adminSource.Level));
        Assert.Equal(adminUntil, adminSource.ExpiresAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Null(adminSource.Version);
        var person = Assert.Single(state.People, p => p.UserId == member.Id);
        Assert.Equal("read", person.Level);
        Assert.Equal(["vault", "secret"], person.Sources.Select(s => s.Kind));
        Assert.Equal(["discover", "read"], person.Sources.Select(s => s.Level));
        Assert.Equal(("Provoz", group), (person.Sources[0].Name, person.Sources[0].GroupId!.Value));
        Assert.Equal("", person.Sources[1].Name);
        Assert.NotNull(person.Sources[1].Version);
        var readerRow = Assert.Single(state.People, p => p.UserId == reader.Id);
        Assert.Equal(readerUntil.UtcDateTime, readerRow.Sources.Single().ExpiresAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(["wikiAdmin"], Assert.Single(state.People, p => p.UserId == f.Admin.Id).Sources.Select(s => s.Kind));
        Assert.Equal("discover", Assert.Single(state.Groups).Level);
        Assert.Equal("resource_grant.added", state.LastChange!.Action);
        Assert.Equal(temporary.Id, state.LastChange.ActorId);
        Assert.Equal("temporary-admin", state.LastChange.ActorName);
        // The vault itself shows the space Admin role and the vault grants, not the secret grant.
        var vaultState = await f.As<VaultAccessState>(f.SpaceAdmin, new GetVaultAccess(vault));
        Assert.Equal(["vault"], Assert.Single(vaultState.People, p => p.UserId == member.Id).Sources.Select(s => s.Kind));
        Assert.Equal(f.SpaceAdmin.Id, vaultState.LastChange!.ActorId);

        // Without manage: the caller's own row only.
        var own = await f.As<VaultAccessState>(member, new GetSecretAccess(router));
        Assert.False(own.CanManage);
        Assert.Empty(own.People);
        Assert.Empty(own.Groups);
        Assert.Null(own.LastChange);
        Assert.Equal((member.Id, "read", 2), (own.MyAccess.UserId, own.MyAccess.Level, own.MyAccess.Sources.Count));
        // Without Discover: missing; a lapsed grant is listed but not effective. WikiAdmin manages the access of every vault.
        Assert.True((await f.As<VaultAccessState>(f.Admin, new GetVaultAccess(vault))).CanManage);
        Assert.Equal("manage", (await f.As<VaultAccessState>(f.Admin, new GetSecretAccess(router))).MyAccess.Level);
        await Denied(404, () => f.As<VaultAccessState>(reader, new GetSecretAccess(Guid.NewGuid())));
        await f.Host.ExecuteAsync("UPDATE \"ResourceGrant\" SET \"ActiveTo\" = clock_timestamp() WHERE \"UserId\" = :id AND \"ActiveTo\" IS NULL", reader.Id);
        await f.Host.SeedAsync(async session => await session.CreateSQLQuery("""
            INSERT INTO "ResourceGrant"("Id","VaultId","VaultSpaceId","UserId","Role","StartAt","ExpiresAt","ActiveFrom")
            VALUES (gen_random_uuid(), :vault, :space, :user, 20, now() - interval '2 days', now() - interval '1 day', clock_timestamp())
            """).SetParameter("vault", vault).SetParameter("space", f.SpaceId).SetParameter("user", reader.Id).ExecuteUpdateAsync());
        var lapsed = Assert.Single((await f.As<VaultAccessState>(f.SpaceAdmin, new GetVaultAccess(vault))).People, p => p.UserId == reader.Id);
        Assert.False(Assert.Single(lapsed.Sources).Effective);
        Assert.Null(lapsed.Level);
        await Denied(404, () => f.As<VaultAccessState>(reader, new GetVaultAccess(vault)));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Grants_need_manage_are_bounded_validated_and_versioned_with_audited_history()
    {
        await using var f = await VaultFixture.CreateAsync();
        var (vault, router, mail) = await AttachAsync(f);
        var user = await f.Host.SeedUserAsync("user");
        var valueReader = await f.Host.SeedUserAsync("value-reader");
        var secretManager = await f.Host.SeedUserAsync("secret-manager");
        var temporary = await f.Host.SeedUserAsync("temporary-admin");
        // Candidates come 20 at a time from every group and account of the installation: the test searches its own by name.
        var groupName = TestLogin.For("Provoz");
        var group = await f.Host.SeedGroupAsync(groupName);
        var adminUntil = DateTime.UtcNow.AddDays(3);
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = temporary.Id,
            Role = (int)SpaceRole.Admin, ExpiresAt = adminUntil }));
        await Grant(f, f.SpaceAdmin, vault, valueReader, "read");
        await f.As(f.SpaceAdmin, new SetSecretGrant(router, new UpdateVaultGrant(secretManager.Id, null, "manage", null)));

        // Who may grant: manage on the target (a WikiAdmin everywhere). Read is not manage.
        await f.As(f.Admin, new SetVaultGrant(vault, new UpdateVaultGrant(null, group, "discover", null)));
        await Denied(403, () => Grant(f, valueReader, vault, user, "discover"));
        await Denied(404, () => f.As(user, new SetSecretGrant(router, new UpdateVaultGrant(user.Id, null, "discover", null))));
        await Denied(403, () => Grant(f, secretManager, vault, user, "discover"));
        await Denied(404, () => f.As(secretManager, new SetSecretGrant(mail, new UpdateVaultGrant(user.Id, null, "discover", null))));
        await f.As(secretManager, new SetSecretGrant(router, new UpdateVaultGrant(user.Id, null, "read", null)));
        // A temporary manager cannot grant beyond its own authority.
        await Denied(403, () => Grant(f, temporary, vault, user, "discover"));
        await Denied(403, () => Grant(f, temporary, vault, user, "discover", null, new DateTimeOffset(adminUntil.AddDays(1))));
        await Grant(f, temporary, vault, user, "discover", null, new DateTimeOffset(adminUntil.AddHours(-1)));

        // Grant candidates: managers only, users and groups.
        var candidates = await f.As<IReadOnlyList<AccessCandidate>>(f.SpaceAdmin, new GetVaultGrantCandidates(vault, groupName[..12].ToLowerInvariant()));
        Assert.Contains(candidates, c => c.Kind == "group" && c.Id == group);
        Assert.Contains(await f.As<IReadOnlyList<AccessCandidate>>(secretManager, new GetSecretGrantCandidates(router, user.Username)),
            c => c.Kind == "user" && c.Id == user.Id);
        await Denied(403, () => f.As<IReadOnlyList<AccessCandidate>>(valueReader, new GetVaultGrantCandidates(vault, null)));
        Assert.Contains(await f.As<IReadOnlyList<AccessCandidate>>(f.Admin, new GetVaultGrantCandidates(vault, groupName)), c => c.Id == group);

        // Validation before anything else; writing values (a future level) is not grantable.
        await Denied(400, () => Grant(f, f.SpaceAdmin, vault, user, null));
        await Denied(400, () => Grant(f, f.SpaceAdmin, vault, user, "write"));
        await Denied(400, () => Grant(f, f.SpaceAdmin, vault, user, "readValue"));
        await Denied(400, () => f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(user.Id, Guid.NewGuid(), "discover", null))));
        await Denied(400, () => Grant(f, f.SpaceAdmin, vault, user, "discover", DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow.AddDays(1)));
        await Denied(404, () => f.As(f.SpaceAdmin, new SetVaultGrant(vault, new UpdateVaultGrant(Guid.NewGuid(), null, "discover", null))));
        await Denied(404, () => f.As(f.SpaceAdmin, new SetVaultGrant(Guid.NewGuid(), new UpdateVaultGrant(user.Id, null, "discover", null))));

        // CAS on the current assignment, the no-op rule, change and revoke.
        var version = await VersionAsync(f, vault, user);
        await Denied(409, () => Grant(f, f.SpaceAdmin, vault, user, "read"));
        await Grant(f, f.SpaceAdmin, vault, user, "discover", null, new DateTimeOffset(adminUntil.AddHours(-1)), version);
        Assert.Equal(version, await VersionAsync(f, vault, user));
        await Denied(400, () => Grant(f, f.SpaceAdmin, vault, user, "discover", null, DateTimeOffset.UtcNow.AddMinutes(-1), version));
        await Grant(f, f.SpaceAdmin, vault, user, "manage", null, null, version);
        var changed = await VersionAsync(f, vault, user);
        Assert.NotEqual(version, changed);
        await AssertVault(f, user, vault, VaultAccessLevel.Manage);
        await Denied(409, () => f.As(f.SpaceAdmin, new RevokeVaultGrant(vault, user.Id, null, version)));
        await f.As(f.SpaceAdmin, new RevokeVaultGrant(vault, user.Id, null, changed));
        await f.As(f.SpaceAdmin, new RevokeVaultGrant(vault, user.Id, null, null));
        await Denied(400, () => f.As(f.SpaceAdmin, new RevokeVaultGrant(vault, null, null, null)));
        await AssertVault(f, user, vault, VaultAccessLevel.Discover);
        await AssertSecret(f, user, router, VaultAccessLevel.Read);
        await f.As(secretManager, new RevokeSecretGrant(router, user.Id, null, await VersionAsync(f, vault, user, router)));
        await AssertVault(f, user, vault, VaultAccessLevel.None);

        // History: every interval closed, never deleted; the audit names the actor of each change.
        using var session = f.Host.OpenSession();
        var rows = await session.CreateSQLQuery("""
            SELECT g."Role", g."ActiveTo" IS NULL AS "Current", g."SecretId" IS NULL AS "VaultLevel",
                (SELECT string_agg(e."Action" || ':' || e."ActorId", ',' ORDER BY e."Id") FROM "AccessAuditEvent" e WHERE e."ResourceGrantId" = g."Id") AS "Audit"
            FROM "ResourceGrant" g WHERE g."UserId" = :user ORDER BY g."ActiveFrom"
            """).SetParameter("user", user.Id).ListAsync<object[]>();
        // The secret grant, the bounded vault grant (the no-op kept it) and its replacement; the refused attempts left nothing.
        Assert.Equal(3, rows.Count);
        Assert.Equal(new object[] { 20, false, false, $"resource_grant.added:{secretManager.Id},resource_grant.removed:{secretManager.Id}" }, rows[0]);
        Assert.Equal(new object[] { 10, false, true, $"resource_grant.added:{temporary.Id},resource_grant.removed:{f.SpaceAdmin.Id}" }, rows[1]);
        Assert.Equal(new object[] { 40, false, true, $"resource_grant.added:{f.SpaceAdmin.Id},resource_grant.removed:{f.SpaceAdmin.Id}" }, rows[2]);
    }

    [PostgreSqlFact]
    public async Task Security_me_reports_global_security_managed_spaces_and_vault_levels()
    {
        await using var f = await VaultFixture.CreateAsync();
        var (vault, router, _) = await AttachAsync(f);
        var direct = await f.Host.SeedUserAsync("direct");
        var secretOnly = await f.Host.SeedUserAsync("secret-only");
        var nobody = await f.Host.SeedUserAsync("nobody");
        await Grant(f, f.SpaceAdmin, vault, direct, "read");
        await f.As(f.SpaceAdmin, new SetSecretGrant(router, new UpdateVaultGrant(secretOnly.Id, null, "read", null)));

        var wiki = await f.As<SecurityCapabilitiesView>(f.Admin, new GetSecurityCapabilities());
        Assert.True(wiki.GlobalSecurityAdmin);
        // owner decision 27. 9.: a flag instead of every space
        Assert.True(wiki.ManagesAllVaults);
        Assert.Empty(wiki.VaultManagerSpaces);
        Assert.Equal(new VaultAccessSummary(vault, f.SpaceId, "manage", false), Assert.Single(wiki.Vaults, v => v.SpaceId == f.SpaceId));
        var manager = await f.As<SecurityCapabilitiesView>(f.SpaceAdmin, new GetSecurityCapabilities());
        Assert.False(manager.GlobalSecurityAdmin || manager.ManagesAllVaults);
        // No builtin password and no linked identity in this host: nothing can prove a fresh sign-in.
        Assert.Equal(("unsupported", null), (manager.FreshAuth, manager.FreshAuthMethod));
        Assert.Equal([f.SpaceId], manager.VaultManagerSpaces);
        Assert.Equal(new VaultAccessSummary(vault, f.SpaceId, "manage", false), Assert.Single(manager.Vaults));
        Assert.Equal(new VaultAccessSummary(vault, f.SpaceId, "read", false),
            Assert.Single((await f.As<SecurityCapabilitiesView>(direct, new GetSecurityCapabilities())).Vaults));
        Assert.Equal(new VaultAccessSummary(vault, f.SpaceId, "discover", true),
            Assert.Single((await f.As<SecurityCapabilitiesView>(secretOnly, new GetSecurityCapabilities())).Vaults));
        var none = await f.As<SecurityCapabilitiesView>(nobody, new GetSecurityCapabilities());
        Assert.Equal((false, 0, 0), (none.GlobalSecurityAdmin, none.VaultManagerSpaces.Count, none.Vaults.Count));
    }

    /// <summary>A grant change asks for the live collaboration refresh after its commit, as page grants do. The coordinator here
    /// fails on purpose, so AccessChanges logs its warning exactly when it ran; a refused change must not run it.</summary>
    [PostgreSqlFact]
    public async Task Granting_and_revoking_refresh_live_access_after_the_commit_only()
    {
        var logs = new CaptureLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        // A coordinator without a document registry throws from RefreshAllAsync; AccessChanges catches it and logs.
        var failing = new DocumentAccessCoordinator(null!, null!, null!, new MarkdownSyncOptions(), TimeProvider.System,
            NullLogger<DocumentAccessCoordinator>.Instance);
        // A private host (configure): the failing coordinator is the host's, and the test counts every refresh warning of its log.
        await using var f = await VaultFixture.CreateAsync(configure: services =>
            services.Replace(ServiceDescriptor.Scoped(_ => new AccessChanges(factory.CreateLogger<AccessChanges>(), failing))));
        var (vault, _, _) = await AttachAsync(f);
        var user = await f.Host.SeedUserAsync("user");
        int Refreshes() => logs.Messages.Count(m => m.Contains("access refresh after an access change failed", StringComparison.Ordinal));

        await Denied(400, () => Grant(f, f.SpaceAdmin, vault, user, "write"));
        Assert.Equal(0, Refreshes());
        await Grant(f, f.SpaceAdmin, vault, user, "read");
        Assert.Equal(1, Refreshes());
        // A stale version is refused after the locks and reads: rolled back, no refresh.
        await Denied(409, () => Grant(f, f.SpaceAdmin, vault, user, "manage"));
        Assert.Equal(1, Refreshes());
        await Grant(f, f.SpaceAdmin, vault, user, "read", null, null, await VersionAsync(f, vault, user));
        Assert.Equal(1, Refreshes());
        await f.As(f.SpaceAdmin, new RevokeVaultGrant(vault, user.Id, null, await VersionAsync(f, vault, user)));
        Assert.Equal(2, Refreshes());
        await Denied(404, () => f.As(user, new RevokeVaultGrant(vault, user.Id, null, null)));
        Assert.Equal(2, Refreshes());
    }

    private static async Task<(Guid Vault, Guid Router, Guid Mail)> AttachAsync(VaultFixture f)
    {
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var secrets = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        return (vault.Id, secrets.Single(s => s.Title == "Router").SecretId, secrets.Single(s => s.Title == "Mail").SecretId);
    }

    private static Task Grant(VaultFixture f, UserAccount actor, Guid vault, UserAccount subject, string? level,
        DateTimeOffset? startAt = null, DateTimeOffset? expiresAt = null, string? expectedVersion = null)
    {
        return f.As(actor, new SetVaultGrant(vault, new UpdateVaultGrant(subject.Id, null, level, expectedVersion, startAt, expiresAt)));
    }

    /// <summary>The CAS token of the current direct grant of a user on the vault (or on one of its secrets), as the access tab shows it.</summary>
    private static async Task<string?> VersionAsync(VaultFixture f, Guid vault, UserAccount user, Guid? secret = null)
    {
        var state = await f.As<VaultAccessState>(f.SpaceAdmin, secret is { } id ? new GetSecretAccess(id) : new GetVaultAccess(vault));
        return state.People.SingleOrDefault(p => p.UserId == user.Id)?.Sources
            .SingleOrDefault(s => s.GroupId is null && s.Kind == (secret is null ? "vault" : "secret"))?.Version;
    }

    private static async Task AssertVault(VaultFixture f, UserAccount actor, Guid vault, VaultAccessLevel expected)
    {
        using var scope = f.Host.Services.CreateScope();
        var grants = scope.ServiceProvider.GetRequiredService<VaultGrantSnapshots>();
        foreach (var level in Levels)
        {
            var decision = await grants.AuthorizeVaultAsync(actor, vault, level, CancellationToken.None);
            Assert.True(Expected(expected, level) == decision.Outcome, $"{actor.Username} vault {level}: {decision.Outcome}");
            Assert.Equal(expected, decision.Level);
        }
    }

    private static async Task AssertSecret(VaultFixture f, UserAccount actor, Guid secret, VaultAccessLevel expected)
    {
        using var scope = f.Host.Services.CreateScope();
        var grants = scope.ServiceProvider.GetRequiredService<VaultGrantSnapshots>();
        var batch = await grants.ForSecretsAsync(actor, [secret, Guid.NewGuid()], VaultAccessLevel.Discover, CancellationToken.None);
        Assert.Equal(VaultAccessOutcome.Missing, batch.Single(pair => pair.Key != secret).Value.Outcome);
        Assert.Equal(expected, batch[secret].Level);
        foreach (var level in Levels)
        {
            var decision = await grants.AuthorizeSecretAsync(actor, secret, level, CancellationToken.None);
            Assert.True(Expected(expected, level) == decision.Outcome, $"{actor.Username} secret {level}: {decision.Outcome}");
        }
    }

    /// <summary>The staircase: a level includes every lower one.</summary>
    private static VaultAccessOutcome Expected(VaultAccessLevel held, VaultAccessLevel required) =>
        held >= required ? VaultAccessOutcome.Allowed
        : held.CanDiscover() ? VaultAccessOutcome.Forbidden
        : VaultAccessOutcome.Missing;

    private static async Task<ApiException> Denied(int status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        return error;
    }
}
