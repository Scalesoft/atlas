using Npgsql;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Vaults;

/// <summary>Constraints of 2026092701_vaults.sql, exercised with raw SQL so the database, not the handlers, is under test.</summary>
public sealed class VaultSchemaTests
{
    [PostgreSqlFact]
    public async Task Location_vault_and_secret_constraints_reject_inconsistent_rows()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_vault_schema_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var spaceId = await host.SeedSpaceAsync("Prostor");
        await using var sql = new NpgsqlConnection(host.ConnectionString);
        await sql.OpenAsync();
        async Task Run(string text, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(text, sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            await command.ExecuteNonQueryAsync();
        }
        async Task Rejected(string state, string text, params (string Name, object Value)[] parameters)
            => Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => Run(text, parameters))).SqlState);

        // Location names are unique in the whole installation (ignoring case): this test's own.
        var local = TestLogin.For("Local");
        var location = Guid.NewGuid();
        await Run("INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(@id,@name,'local','/srv/keepass')", ("id", location), ("name", local));
        await Rejected("23514", "INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\",\"ProtectedPassword\") VALUES(gen_random_uuid(),'Heslo','local','/srv/x','payload')");
        await Rejected("23514", "INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(gen_random_uuid(),'Mix','local','smb://server/share')");
        await Rejected("23514", "INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(gen_random_uuid(),'Mix2','smb','/srv/x')");
        await Rejected("23514", "INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(gen_random_uuid(),'Ftp','ftp','ftp://x')");
        await Rejected("23505", "INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(gen_random_uuid(),@name,'local','/srv/y')", ("name", local.ToUpperInvariant()));
        // A managed location stores no path: Atlas derives its folder from the data directory.
        await Run("INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(gen_random_uuid(),@name,'managed','')", ("name", TestLogin.For("Spravované")));
        await Rejected("23514", "INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(gen_random_uuid(),'Cesta','managed','/srv/z')");

        const string insertVault = "INSERT INTO \"Vault\"(\"Id\",\"SpaceId\",\"Name\",\"LocationId\",\"FileName\",\"ProtectedKdbxPassword\",\"State\",\"LastSnapshotSha256\",\"CreatedBy\",\"CreatedAt\") " +
            "VALUES(@id,@space,@name,@location,@file,@password,@state,@sha,@by,now())";
        (string, object)[] Vault(Guid id, string name, string file, object password, string state, object? sha = null) =>
            [("id", id), ("space", spaceId), ("name", name), ("location", location), ("file", file), ("password", password), ("state", state), ("sha", sha ?? DBNull.Value), ("by", admin.Id)];
        var vault = Guid.NewGuid();
        await Run(insertVault, Vault(vault, "Trezor", "Provoz.kdbx", "payload", "unlocked", new byte[32]));
        await Rejected("23505", insertVault, Vault(Guid.NewGuid(), "Jiný", "provoz.KDBX", "payload", "unlocked"));
        await Rejected("23505", insertVault, Vault(Guid.NewGuid(), "trezor", "jiny.kdbx", "payload", "unlocked"));
        await Rejected("23514", insertVault, Vault(Guid.NewGuid(), "Bez hesla", "bez.kdbx", DBNull.Value, "unlocked"));
        await Rejected("23514", insertVault, Vault(Guid.NewGuid(), "Stav", "stav.kdbx", "payload", "open"));
        await Rejected("23514", insertVault, Vault(Guid.NewGuid(), "Otisk", "otisk.kdbx", "payload", "unlocked", new byte[16]));
        await Rejected("23514", insertVault, Vault(Guid.NewGuid(), "Cesta", "sub/x.kdbx", "payload", "unlocked"));
        await Rejected("23514", insertVault, Vault(Guid.NewGuid(), "Cesta2", "sub\\x.kdbx", "payload", "unlocked"));
        await Run(insertVault, Vault(Guid.NewGuid(), "Zamčený", "zamceny.kdbx", DBNull.Value, "locked"));
        // Archiving frees the file and the name for a new registration.
        await Run("UPDATE \"Vault\" SET \"ArchivedAt\"=now() WHERE \"Id\"=@id", ("id", vault));
        await Run(insertVault, Vault(Guid.NewGuid(), "Trezor", "provoz.kdbx", "payload", "unlocked"));

        const string insertSecret = "INSERT INTO \"VaultSecret\"(\"Id\",\"VaultId\",\"EntryUuid\",\"Title\",\"GroupPath\",\"AvailableFields\",\"FirstSeenAt\") VALUES(gen_random_uuid(),@vault,@entry,'Titulek','',@fields,now())";
        var entry = Guid.NewGuid();
        await Run(insertSecret, ("vault", vault), ("entry", entry), ("fields", (short)15));
        await Rejected("23505", insertSecret, ("vault", vault), ("entry", entry), ("fields", (short)1));
        await Rejected("23514", insertSecret, ("vault", vault), ("entry", Guid.NewGuid()), ("fields", (short)16));
        await Rejected("23503", insertSecret, ("vault", Guid.NewGuid()), ("entry", Guid.NewGuid()), ("fields", (short)0));
        await Rejected("23001", "DELETE FROM \"Vault\" WHERE \"Id\"=@id", ("id", vault));
        Assert.Contains((await Assert.ThrowsAsync<PostgresException>(() => Run("DELETE FROM \"VaultLocation\" WHERE \"Id\"=@id", ("id", location)))).SqlState, new[] { "23001", "23503" });
    }

    [PostgreSqlFact]
    public async Task Location_history_records_changes_without_the_password_and_is_immutable()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_vault_history_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        await using var sql = new NpgsqlConnection(host.ConnectionString);
        await sql.OpenAsync();
        var id = Guid.NewGuid();
        var name = TestLogin.For("Síť");
        // VaultLocationHistory (C# since 3.6) with the actor each writer passes.
        async Task Write(Guid? actor, Action<VaultLocation> change) => await host.SeedAsync(async session =>
        {
            var location = await session.GetAsync<VaultLocation>(id);
            if (location is null)
            {
                location = new VaultLocation { Id = id };
                change(location);
                await session.SaveAsync(location);
            }
            else
            {
                change(location);
            }
            await VaultLocationHistory.RecordAsync(session, location, actor, CancellationToken.None);
        });
        await Write(null, l => (l.Name, l.Kind, l.Root, l.Username, l.Domain, l.ProtectedPassword) = (name, "smb", "smb://files/keepass", "atlas", "EXAMPLE", "payload-1"));
        await Write(admin.Id, l => l.ProtectedPassword = "payload-2");
        await Write(admin.Id, l => l.Name = name);
        await Write(null, l => l.Root = "smb://files/keepass2");
        await using var read = new NpgsqlCommand($"SELECT \"Root\",\"HasPassword\",\"PasswordChanged\",\"ChangedBy\",\"ActiveTo\" IS NULL FROM \"VaultLocationRevision\" WHERE \"LocationId\"='{id}' ORDER BY \"ActiveFrom\"", sql);
        var rows = new List<object[]>();
        await using (var reader = await read.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var row = new object[5];
                reader.GetValues(row);
                rows.Add(row);
            }
        }
        // The no-op rename writes nothing; the password change is recorded without its value.
        Assert.Equal(3, rows.Count);
        Assert.Equal(new object[] { "smb://files/keepass", true, false, DBNull.Value, false }, rows[0]);
        Assert.Equal(new object[] { "smb://files/keepass", true, true, admin.Id, false }, rows[1]);
        Assert.Equal(new object[] { "smb://files/keepass2", true, false, DBNull.Value, true }, rows[2]);
        await using var columns = new NpgsqlCommand("SELECT count(*) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='VaultLocationRevision' AND column_name ILIKE '%Protected%'", sql);
        Assert.Equal(0L, await columns.ExecuteScalarAsync());
        // The history is append-only: no server code rewrites it (IntegrityRulesConventionTests, C# since 3.6).
    }
}
