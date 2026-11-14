using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Vaults;

/// <summary>Constraints, history and audit of 2026092702_vault_access.sql, exercised with raw SQL so the database is under test.</summary>
public sealed class VaultAccessSchemaTests
{
    [PostgreSqlFact]
    public async Task Resource_grant_targets_capabilities_and_history_are_enforced_by_the_schema()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_vault_access_schema_");
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var user = await host.SeedUserAsync("user");
        var spaceId = await host.SeedSpaceAsync("Provoz");
        var otherSpace = await host.SeedSpaceAsync("Jiný");
        await using var sql = new NpgsqlConnection(host.ConnectionString);
        await sql.OpenAsync();
        async Task<int> Run(string text, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(text, sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            return await command.ExecuteNonQueryAsync();
        }
        async Task<object?> Scalar(string text, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(text, sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            return await command.ExecuteScalarAsync();
        }
        async Task Rejected(string state, string text, params (string Name, object Value)[] parameters)
            => Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(() => Run(text, parameters))).SqlState);

        var location = Guid.NewGuid();
        await Run("INSERT INTO \"VaultLocation\"(\"Id\",\"Name\",\"Kind\",\"Root\") VALUES(@id,@name,'local','/srv/keepass')", ("id", location), ("name", TestLogin.For("Local")));
        var vault = Guid.NewGuid();
        var otherVault = Guid.NewGuid();
        foreach (var (id, file) in new[] { (vault, "a.kdbx"), (otherVault, "b.kdbx") })
        {
            await Run("INSERT INTO \"Vault\"(\"Id\",\"SpaceId\",\"Name\",\"LocationId\",\"FileName\",\"State\",\"CreatedBy\",\"CreatedAt\") VALUES(@id,@space,@file,@location,@file,'locked',@by,now())",
                ("id", id), ("space", spaceId), ("file", file), ("location", location), ("by", admin.Id));
        }
        var secret = Guid.NewGuid();
        await Run("INSERT INTO \"VaultSecret\"(\"Id\",\"VaultId\",\"EntryUuid\",\"Title\",\"GroupPath\",\"AvailableFields\",\"FirstSeenAt\") VALUES(@id,@vault,gen_random_uuid(),'Router','',3,now())",
            ("id", secret), ("vault", vault));
        var pageId = await PageAccess.ItemAccessSeed.PageAsync(host, spaceId, admin);

        const string insert = "INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"VaultId\",\"SecretId\",\"VaultSpaceId\",\"UserId\",\"Role\") VALUES(@id,@page,@vault,@secret,@space,@user,@role)";
        (string, object)[] Grant(object page, object vaultId, object secretId, object space, int role, Guid? id = null) =>
            [("id", id ?? Guid.NewGuid()), ("page", page), ("vault", vaultId), ("secret", secretId), ("space", space), ("user", user.Id), ("role", role)];
        var none = DBNull.Value;

        // Exactly one target: a page, a vault, or a secret together with its vault.
        await Rejected("23514", insert, Grant(pageId, vault, none, spaceId, 10));
        await Rejected("23514", insert, Grant(none, none, none, none, 10));
        await Rejected("23514", insert, Grant(none, vault, none, none, 10));
        await Rejected("23514", insert, Grant(none, none, secret, spaceId, 10));
        // The composite keys keep the vault's space and the secret's vault consistent.
        await Rejected("23503", insert, Grant(none, vault, none, otherSpace, 10));
        await Rejected("23503", insert, Grant(none, otherVault, secret, spaceId, 10));
        // Page roles stay 1-3; vault and secret grants store a level 10/20/40 (30, Zapisuje, is reserved and not grantable).
        await Rejected("23514", insert, Grant(pageId, none, none, none, 4));
        await Rejected("23514", insert, Grant(none, vault, none, spaceId, 3));
        await Rejected("23514", insert, Grant(none, vault, none, spaceId, 30));
        var vaultGrant = Guid.NewGuid();
        var secretGrant = Guid.NewGuid();
        await Run(insert, Grant(none, vault, none, spaceId, 20, vaultGrant));
        await Run(insert, Grant(none, vault, secret, spaceId, 40, secretGrant));
        // The audit is the writer's (AccessAuditLog, C# since 3.6): here as VaultGrantHandler writes it, with the actor.
        await host.SeedAsync(session => AccessAuditLog.OpenedAsync(session, AccessAuditLog.ResourceGrant, [vaultGrant, secretGrant], admin.Id, CancellationToken.None));
        await Run(insert, Grant(pageId, none, none, none, 3));
        // One current assignment per subject on each target: a second vault grant overlaps, a secret grant does not.
        await Rejected("23P01", insert, Grant(none, vault, none, spaceId, 40));
        await Rejected("23P01", insert, Grant(none, vault, secret, spaceId, 10));
        await Run(insert, Grant(none, otherVault, none, spaceId, 10));

        // Rows are immutable except for closing: the mapping is read-only (an update never reaches the database) and IntegrityRules
        // refuses a delete; the audit records the actor and the view reports the vault's space.
        await host.SeedAsync(async s => (await s.GetAsync<ResourceGrant>(vaultGrant))!.Role = 40);
        Assert.Equal(20, await Scalar("SELECT \"Role\" FROM \"ResourceGrant\" WHERE \"Id\"=@id", ("id", vaultGrant)));
        await IntegrityRefusal.AssertAsync(host, IntegrityRules.HistoryNotDeleted, async s => await s.DeleteAsync(await s.GetAsync<ResourceGrant>(vaultGrant)));
        await Run("UPDATE \"ResourceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"Id\"=@id", ("id", vaultGrant));
        await host.SeedAsync(session => AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [vaultGrant], admin.Id, CancellationToken.None));
        await Run(insert, Grant(none, vault, none, spaceId, 40));
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM \"AccessAuditView\" WHERE \"ResourceGrantId\"=@id AND \"ActorId\"=@actor AND \"VaultId\"=@vault AND \"SpaceId\"=@space AND \"Role\"=20",
            ("id", vaultGrant), ("actor", admin.Id), ("vault", vault), ("space", spaceId)));
        Assert.Equal("resource_grant.removed", await Scalar("SELECT \"Action\" FROM \"AccessAuditView\" WHERE \"ResourceGrantId\"=@id ORDER BY \"Id\" DESC LIMIT 1", ("id", vaultGrant)));
        Assert.Equal(secret, await Scalar("SELECT \"SecretId\" FROM \"AccessAuditView\" WHERE \"ResourceGrantId\"=@id", ("id", secretGrant)));
        // Grants keep their targets alive: an archived vault or a removed secret is soft state, never a delete.
        await Rejected("23001", "DELETE FROM \"VaultSecret\" WHERE \"Id\"=@id", ("id", secret));
        await Rejected("23001", "DELETE FROM \"Vault\" WHERE \"Id\"=@id", ("id", otherVault));
        // Page decisions ignore vault rows: the user reaches exactly the one page of its page grant.
        Assert.Equal(pageId, await Scalar("SELECT \"PageId\" FROM \"EffectivePageAccess\" WHERE \"UserId\"=@user", ("user", user.Id)));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM \"EffectivePageAccess\" WHERE \"UserId\"=@user", ("user", user.Id)));
    }
}
