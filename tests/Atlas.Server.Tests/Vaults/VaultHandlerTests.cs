using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Kdbx;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Platform.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

public sealed class VaultHandlerTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";

    [PostgreSqlFact]
    public async Task Locations_are_wiki_admin_surface_and_space_admins_see_only_names()
    {
        await using var f = await VaultFixture.CreateAsync();
        await Denied(403, () => f.As<VaultLocationView>(f.SpaceAdmin, new SaveVaultLocation(null, new("Síť", "local", f.Folder))));
        var local = await f.LocalLocationAsync();
        var server = f.Name("Souborový server");
        Assert.Equal("local", local.Kind);
        Assert.False(local.HasPassword);
        var smb = await f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null,
            new(server, "smb", "smb://files.example.test/keepass", "atlas", "EXAMPLE", "smb-heslo")));
        Assert.True(smb.HasPassword);
        await Denied(400, () => f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null, new("Relativní", "local", "relative/folder"))));
        await Denied(400, () => f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null, new("Smíšené", "local", "smb://server/share"))));
        await Denied(400, () => f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null, new("UNC", "smb", @"\\server\share"))));
        await Denied(409, () => f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null, new(local.Name.ToUpperInvariant(), "local", f.Folder))));

        // An empty password keeps the stored one; switching to a local folder drops every credential.
        var kept = await f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(smb.Id, new(server, "smb", "smb://files.example.test/keepass2", "atlas", "EXAMPLE")));
        Assert.True(kept.HasPassword);
        Assert.Equal("smb://files.example.test/keepass2", kept.Root);
        // SEC-T-2: the stored password goes only to the server and account it was saved for, in a save and in a test alike.
        await Denied(400, () => f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(smb.Id, new(server, "smb", "smb://other.example.test/keepass2", "atlas", "EXAMPLE"))));
        await Denied(400, () => f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(smb.Id, new(server, "smb", "smb://files.example.test/keepass2", "someone", "EXAMPLE"))));
        await Denied(400, () => f.As<FileStorageOptions>(f.Admin, new PrepareLocationTest(new("smb", "smb://other.example.test/keepass2", "atlas", "EXAMPLE", LocationId: smb.Id))));
        Assert.Equal("smb-heslo", (await f.As<FileStorageOptions>(f.Admin, new PrepareLocationTest(new("smb", "smb://files.example.test/other", "atlas", "EXAMPLE", LocationId: smb.Id)))).Password);
        var names = await f.As<IReadOnlyList<VaultLocationView>>(f.SpaceAdmin, new ListVaultLocations());
        // Locations are the installation's: this test's two among every other test's.
        names = [.. names.Where(l => l.Id == local.Id || l.Id == smb.Id)];
        Assert.Equal([local.Name, server], names.Select(l => l.Name));
        Assert.All(names, l => Assert.Null(l.Root));
        var outsider = await f.Host.SeedUserAsync("reader");
        await Denied(403, () => f.As<IReadOnlyList<VaultLocationView>>(outsider, new ListVaultLocations()));

        await f.As(f.Admin, new ArchiveVaultLocation(smb.Id));
        Assert.Equal(local.Id, Assert.Single(await f.As<IReadOnlyList<VaultLocationView>>(f.Admin, new ListVaultLocations()), l => l.Id == local.Id || l.Id == smb.Id).Id);
        using var session = f.Host.OpenSession();
        var revisions = await session.CreateSQLQuery("SELECT \"HasPassword\", \"PasswordChanged\", \"ChangedBy\", \"ArchivedAt\" IS NOT NULL FROM \"VaultLocationRevision\" WHERE \"LocationId\"=:id ORDER BY \"ActiveFrom\"")
            .SetParameter("id", smb.Id).ListAsync<object[]>();
        Assert.Equal(3, revisions.Count);
        Assert.Equal(new object[] { true, false, f.Admin.Id, false }, revisions[0]);
        Assert.Equal(new object[] { true, false, f.Admin.Id, false }, revisions[1]);
        Assert.Equal(new object[] { true, false, f.Admin.Id, true }, revisions[2]);
    }

    [PostgreSqlFact]
    public async Task Managed_locations_keep_files_in_the_data_folder_and_local_folders_stay_outside_wwwroot()
    {
        await using var f = await VaultFixture.CreateAsync();
        var environment = f.Host.Services.GetRequiredService<IWebHostEnvironment>();
        // A managed location ignores any path it is sent and gets its own folder under .data/vaults.
        var managed = await f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null, new(f.Name("Na serveru wiki"), "managed", "/ignored")));
        Assert.Equal("managed", managed.Kind);
        Assert.Equal(Path.Combine(environment.ContentRootPath, ".data", "vaults", managed.Id.ToString("N")), managed.Root);
        Assert.Empty((await f.Operate(f.Admin, o => o.ListLocationFilesAsync(managed.Id, null, null, CancellationToken.None))).Files);
        await f.Operate(f.Admin, o => o.TestLocationAsync(new TestVaultLocationRequest("managed", "", LocationId: managed.Id), CancellationToken.None));

        var created = await f.Operate(f.SpaceAdmin, o => o.CreateAsync(new CreateVaultRequest(f.SpaceId, managed.Id, "Spravovaný", "spravovany.kdbx", true), CancellationToken.None));
        Assert.Equal("unlocked", created.Vault.Status);
        Assert.True(File.Exists(Path.Combine(managed.Root!, "spravovany.kdbx")));

        await Denied(400, () => f.As<VaultLocationView>(f.Admin, new SaveVaultLocation(null, new("Publikované", "local", Path.Combine(environment.WebRootPath, "trezory")))));
        await Denied(400, () => f.Operate(f.Admin, o => o.TestLocationAsync(new TestVaultLocationRequest("local", environment.WebRootPath), CancellationToken.None)));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Attach_registers_entries_outside_the_recycle_bin_and_rejects_wrong_or_unsupported_files()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        f.PutFile("wrong.kdbx", Fixture);
        var wrong = await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(new AttachVaultRequest(f.SpaceId, location.Id, "Chybné", "wrong.kdbx", "not the password"), CancellationToken.None)));
        Assert.Equal("vault_file_wrongkey", wrong.Code);
        f.PutFile("keyfile.kdbx", "keyfile.kdbx");
        Assert.Equal("vault_file_wrongkey", (await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(
            new AttachVaultRequest(f.SpaceId, location.Id, "Klíč", "keyfile.kdbx", Password("keyfile.kdbx")), CancellationToken.None)))).Code);
        f.PutFile("old.kdbx", "kdbx31.kdbx");
        Assert.Equal("vault_file_unsupported", (await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(
            new AttachVaultRequest(f.SpaceId, location.Id, "Stará", "old.kdbx", Password("kdbx31.kdbx")), CancellationToken.None)))).Code);
        Assert.Equal("vault_file_unavailable", (await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(
            new AttachVaultRequest(f.SpaceId, location.Id, "Chybí", "missing.kdbx", "x"), CancellationToken.None)))).Code);
        await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(new AttachVaultRequest(f.SpaceId, location.Id, "Přípona", "notes.txt", "x"), CancellationToken.None)));
        await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(new AttachVaultRequest(f.SpaceId, location.Id, "Cesta", "../up.kdbx", "x"), CancellationToken.None)));
        Assert.DoesNotContain(await f.As<IReadOnlyList<VaultView>>(f.Admin, new ListVaults()), v => v.SpaceId == f.SpaceId);

        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        Assert.Equal("unlocked", vault.Status);
        Assert.Equal(4, vault.EntryCount);
        Assert.Equal("Provoz", vault.SpaceName);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Read(Fixture))), vault.SnapshotSha256);
        var secrets = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        Assert.Equal(4, secrets.Count);
        Assert.DoesNotContain(secrets, s => s.Title == "Old VPN");
        var router = Assert.Single(secrets, s => s.Title == "Router");
        Assert.Equal("Servers / Linux", router.GroupPath);
        Assert.Equal(["userName", "password", "url", "notes"], router.AvailableFields);
        Assert.Equal(Uuid("6C3EB0A2F721A14E9FCBC26B6BE7D704"), router.EntryUuid);
        Assert.Equal(["userName", "password", "url"], Assert.Single(secrets, s => s.Title == "Mail").AvailableFields);

        // The same file, even under different casing, cannot be registered twice; a vault name is unique per space.
        f.PutFile("second.kdbx", Fixture);
        Assert.Equal("vault_file_registered", (await Denied(409, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(
            new AttachVaultRequest(f.SpaceId, location.Id, "Jiný", "PROVOZ.kdbx", Password(Fixture)), CancellationToken.None)))).Code);
        Assert.Equal("vault_name_taken", (await Denied(409, () => f.Operate(f.SpaceAdmin, o => o.AttachAsync(
            new AttachVaultRequest(f.SpaceId, location.Id, "provozní HESLA", "second.kdbx", Password(Fixture)), CancellationToken.None)))).Code);
        await Denied(409, () => f.As(f.Admin, new ArchiveVaultLocation(location.Id)));
    }

    [PostgreSqlFact]
    public async Task Only_the_space_admin_and_wiki_admin_manage_vaults_of_a_space()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var writer = await f.Host.SeedUserAsync("writer");
        var otherAdmin = await f.Host.SeedUserAsync("other-admin");
        await f.Host.SeedAsync(async session => await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = writer.Id, Role = (int)SpaceRole.Writer }));
        await f.Host.SeedSpaceAsync("Jiný", (otherAdmin, SpaceRole.Admin));
        foreach (var actor in new[] { writer, otherAdmin })
        {
            Assert.Empty(await f.As<IReadOnlyList<VaultView>>(actor, new ListVaults()));
            await Denied(404, () => f.As<IReadOnlyList<VaultSecretView>>(actor, new ListVaultSecrets(vault.Id)));
            await Denied(404, () => f.Operate(actor, o => o.VerifyAsync(vault.Id, CancellationToken.None)));
            await Denied(404, () => f.Operate(actor, o => o.ReplacePasswordAsync(vault.Id, new VaultPasswordRequest(Password(Fixture)), CancellationToken.None)));
            await Denied(404, () => f.Operate(actor, o => o.ArchiveAsync(vault.Id, CancellationToken.None)));
        }
        await Denied(404, () => f.Operate(writer, o => o.AttachAsync(new AttachVaultRequest(f.SpaceId, location.Id, "Pisatel", "provoz.kdbx", Password(Fixture)), CancellationToken.None)));
        Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.Admin, new ListVaults()), v => v.SpaceId == f.SpaceId);
        Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults()));

        await f.Operate(f.Admin, o => o.ArchiveAsync(vault.Id, CancellationToken.None));
        Assert.DoesNotContain(await f.As<IReadOnlyList<VaultView>>(f.Admin, new ListVaults()), v => v.SpaceId == f.SpaceId);
        Assert.False(f.Keyring.Contains(vault.Id));
        using var session = f.Host.OpenSession();
        var archived = await session.GetAsync<Vault>(vault.Id);
        Assert.Null(archived.ProtectedKdbxPassword);
        Assert.NotNull(archived.ArchivedAt);
        // The file can be attached again once the old registration is archived.
        Assert.Equal("unlocked", (await f.AttachAsync(location.Id, "provoz.kdbx", Fixture, name: "Znovu")).Status);
    }

    [PostgreSqlFact]
    public async Task Create_new_publishes_a_kdbx_the_generated_password_opens_and_never_replaces_a_file()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var created = await f.Operate(f.SpaceAdmin, o => o.CreateAsync(new CreateVaultRequest(f.SpaceId, location.Id, "Nový trezor", "novy.kdbx", true), CancellationToken.None));
        Assert.NotNull(created.GeneratedPassword);
        Assert.Equal(28, created.GeneratedPassword!.Length);
        Assert.Equal("unlocked", created.Vault.Status);
        Assert.Equal(0, created.Vault.EntryCount);
        using (var database = Reader().Read(File.ReadAllBytes(Path.Combine(f.Folder, "novy.kdbx")), created.GeneratedPassword, false, CancellationToken.None))
        {
            Assert.Equal("Nový trezor", database.DatabaseName);
            Assert.Empty(database.Entries);
        }
        Assert.True(f.Keyring.Contains(created.Vault.Id));

        var custom = await f.Operate(f.SpaceAdmin, o => o.CreateAsync(new CreateVaultRequest(f.SpaceId, location.Id, "Vlastní", "vlastni.kdbx", false, "Vlastní heslo 123"), CancellationToken.None));
        Assert.Null(custom.GeneratedPassword);
        await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.CreateAsync(new CreateVaultRequest(f.SpaceId, location.Id, "Krátké", "kratke.kdbx", false, "short"), CancellationToken.None)));
        File.WriteAllText(Path.Combine(f.Folder, "existing.kdbx"), "keep me");
        Assert.Equal("vault_file_exists", (await Denied(409, () => f.Operate(f.SpaceAdmin, o => o.CreateAsync(
            new CreateVaultRequest(f.SpaceId, location.Id, "Existující", "existing.kdbx", true), CancellationToken.None)))).Code);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(f.Folder, "existing.kdbx")));
    }

    [PostgreSqlFact]
    public async Task Key_changed_in_keepass_locks_the_vault_until_the_admin_enters_the_current_password()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "tym.kdbx", "argon2id-chacha20-none.kdbx");
        var before = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));

        // Someone changes the master key in KeePass and saves: the stored password no longer opens the file.
        f.PutFile("tym.kdbx", "changed-key.kdbx");
        await f.RefreshAsync();
        var locked = Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults()));
        Assert.Equal("needsPassword", locked.Status);
        Assert.Contains("Zadejte aktuální hlavní heslo", locked.LastFailure);
        Assert.False(f.Keyring.Contains(vault.Id));
        Assert.Equal(Features.Vaults.Runtime.VaultRuntimeState.KeyChanged, f.Snapshots.Current(vault.Id)!.State);
        Assert.Empty(f.Snapshots.Current(vault.Id)!.Entries);
        // A locked vault is not retried in the background and its registry is left as it was.
        await f.RefreshAsync();
        Assert.Equal(Shape(before), Shape(await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id))));
        var verify = await f.Operate(f.SpaceAdmin, o => o.VerifyAsync(vault.Id, CancellationToken.None));
        Assert.Equal("needsPassword", verify.Vault.Status);
        Assert.Null(verify.EntryCount);

        Assert.Equal("vault_file_wrongkey", (await Denied(400, () => f.Operate(f.SpaceAdmin, o => o.ReplacePasswordAsync(vault.Id,
            new VaultPasswordRequest(Password("argon2id-chacha20-none.kdbx")), CancellationToken.None)))).Code);
        var unlocked = await f.Operate(f.SpaceAdmin, o => o.ReplacePasswordAsync(vault.Id, new VaultPasswordRequest(Password("changed-key.kdbx")), CancellationToken.None));
        Assert.Equal("unlocked", unlocked.Status);
        Assert.Null(unlocked.LastFailure);
        Assert.Equal(1, unlocked.EntryCount);
        Assert.True(f.Keyring.Contains(vault.Id));
        var after = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        Assert.Equal("Share", after[0].Title);
        Assert.All(after.Skip(1), s => Assert.NotNull(s.RemovedAt));
        await f.RefreshAsync();
        Assert.Equal("unlocked", Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults())).Status);
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Refresh_adds_removes_and_restores_entries_under_their_stable_secret_ids()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var original = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));

        // Same master password, different content: four entries disappear, one appears.
        f.PutFile("provoz.kdbx", "changed-key.kdbx");
        await f.RefreshAsync();
        var changed = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        Assert.Equal(5, changed.Count);
        var share = changed[0];
        Assert.Equal("Share", share.Title);
        Assert.Null(share.RemovedAt);
        Assert.All(changed.Skip(1), s => Assert.NotNull(s.RemovedAt));
        var view = Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults()));
        Assert.Equal(1, view.EntryCount);

        // The original file returns: its entries are restored under the same secret ids.
        f.PutFile("provoz.kdbx", Fixture);
        await f.RefreshAsync();
        var restored = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        Assert.Equal(original.Select(s => s.SecretId).Order(), restored.Where(s => s.RemovedAt is null).Select(s => s.SecretId).Order());
        Assert.Equal(original.Select(s => s.FirstSeenAt).Order(), restored.Where(s => s.RemovedAt is null).Select(s => s.FirstSeenAt).Order());
        Assert.NotNull(Assert.Single(restored, s => s.SecretId == share.SecretId).RemovedAt);

        var verify = await f.Operate(f.SpaceAdmin, o => o.VerifyAsync(vault.Id, CancellationToken.None));
        Assert.Equal(4, verify.EntryCount);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Read(Fixture))), verify.SnapshotSha256);
        Assert.Equal(share.SecretId, Assert.Single(verify.Missing).SecretId);
    }

    [PostgreSqlFact]
    public async Task Unchanged_file_is_not_decrypted_again_and_a_touched_file_keeps_its_state_version()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var accepted = f.Snapshots.Current(vault.Id)!;
        await f.RefreshAsync();
        var same = f.Snapshots.Current(vault.Id)!;
        Assert.Equal(accepted.StateVersion, same.StateVersion);
        Assert.Same(accepted.Entries, same.Entries);

        // New modification time, same bytes: read and hashed, but no key derivation and no new state.
        File.SetLastWriteTimeUtc(Path.Combine(f.Folder, "provoz.kdbx"), DateTime.UtcNow.AddMinutes(5));
        await f.RefreshAsync();
        var touched = f.Snapshots.Current(vault.Id)!;
        Assert.Equal(accepted.StateVersion, touched.StateVersion);
        Assert.Same(accepted.Entries, touched.Entries);
        Assert.True(touched.CheckedAt > accepted.CheckedAt);
    }

    [PostgreSqlFact]
    public async Task Unavailable_location_hides_entries_keeps_the_registry_and_recovers_by_itself()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var registry = await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id));
        var hidden = f.Folder + "-offline";
        Directory.Move(f.Folder, hidden);
        try
        {
            await f.RefreshAsync();
            var snapshot = f.Snapshots.Current(vault.Id)!;
            Assert.Equal(Features.Vaults.Runtime.VaultRuntimeState.Unavailable, snapshot.State);
            Assert.Empty(snapshot.Entries);
            Assert.Null(snapshot.Sha256);
            var view = Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults()));
            Assert.Equal("unavailable", view.Status);
            Assert.Equal(4, view.EntryCount);
            Assert.Equal(Shape(registry), Shape(await f.As<IReadOnlyList<VaultSecretView>>(f.SpaceAdmin, new ListVaultSecrets(vault.Id))));
            // The key stays: the vault is unavailable, not locked.
            Assert.True(f.Keyring.Contains(vault.Id));
            var verify = await f.Operate(f.SpaceAdmin, o => o.VerifyAsync(vault.Id, CancellationToken.None));
            Assert.Equal("unavailable", verify.Vault.Status);
            Assert.Null(verify.EntryCount);
        }
        finally
        {
            Directory.Move(hidden, f.Folder);
        }
        await f.RefreshAsync();
        Assert.Equal(Features.Vaults.Runtime.VaultRuntimeState.Fresh, f.Snapshots.Current(vault.Id)!.State);
        Assert.Equal(4, f.Snapshots.Current(vault.Id)!.Entries.Count);
        Assert.Equal("unlocked", Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults())).Status);

        File.Delete(Path.Combine(f.Folder, "provoz.kdbx"));
        await f.RefreshAsync();
        var missing = Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults()));
        Assert.Equal("unavailable", missing.Status);
        Assert.Equal("Soubor trezoru v umístění nebyl nalezen.", missing.LastFailure);
    }

    [PostgreSqlFact]
    public async Task Damaged_file_is_an_error_until_a_readable_file_returns()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        var bytes = Read(Fixture);
        File.WriteAllBytes(Path.Combine(f.Folder, "provoz.kdbx"), bytes[..(bytes.Length / 2)]);
        await f.RefreshAsync();
        var broken = Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults()));
        Assert.Equal("error", broken.Status);
        Assert.Equal(KdbxFailure.Corrupt, f.Snapshots.Current(vault.Id)!.KdbxFailure);
        Assert.Equal(4, broken.EntryCount);
        f.PutFile("provoz.kdbx", Fixture);
        await f.RefreshAsync();
        Assert.Equal("unlocked", Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults())).Status);
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Stored_password_unlocks_automatically_after_a_restart()
    {
        await using var f = await VaultFixture.CreateAsync();
        var location = await f.LocalLocationAsync();
        var vault = await f.AttachAsync(location.Id, "provoz.kdbx", Fixture);
        // A restart forgets keys and snapshots; the next pass unlocks from the protected password in the database.
        f.Snapshots.Forget(vault.Id);
        Assert.False(f.Keyring.Contains(vault.Id));
        await f.RefreshAsync();
        Assert.True(f.Keyring.Contains(vault.Id));
        Assert.Equal(4, f.Snapshots.Current(vault.Id)!.Entries.Count);
        using (var session = f.Host.OpenSession())
        {
            var stored = await session.GetAsync<Vault>(vault.Id);
            Assert.NotNull(stored.ProtectedKdbxPassword);
            Assert.DoesNotContain(Password(Fixture), stored.ProtectedKdbxPassword, StringComparison.Ordinal);
        }

        // A lost key ring cannot decrypt the payload: the vault asks for the password instead of failing silently.
        await f.Host.ExecuteAsync("UPDATE \"Vault\" SET \"ProtectedKdbxPassword\"='CfDJ8-not-a-payload' WHERE \"Id\"=:id", vault.Id);
        f.Snapshots.Forget(vault.Id);
        await f.RefreshAsync();
        var view = Assert.Single(await f.As<IReadOnlyList<VaultView>>(f.SpaceAdmin, new ListVaults()));
        Assert.Equal("needsPassword", view.Status);
        Assert.Equal(Features.Vaults.Runtime.VaultFailures.NoStoredPassword, view.LastFailure);
    }

    private static IEnumerable<string> Shape(IEnumerable<VaultSecretView> secrets) =>
        secrets.Select(s => $"{s.SecretId}|{s.EntryUuid}|{s.Title}|{s.GroupPath}|{string.Join(',', s.AvailableFields)}|{s.FirstSeenAt:O}|{s.RemovedAt:O}");

    private static async Task<ApiException> Denied(int status, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        return error;
    }
}
