using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>Migration 2026111400: the company provider's revisions stored as 'strata' before 5.0 read 'gateway', their assignments stay,
/// the checks keep holding (and keep accepting the legacy id for a restore), and the migration runs again without a change; on an
/// installation that never stored 'strata' it changes nothing.</summary>
public sealed class GatewayProviderIdMigrationTests
{
    private const long GatewayIdVersion = 2026111400;

    private const string Revision = """
        INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
          VALUES (@id,'chat',true,@provider,@location,'http://gateway.example:8080/v1','qwen',120,2,@admin,now())
        """;

    private const string State = """
        SELECT (SELECT string_agg("Id"::text || '=' || "Provider", ',' ORDER BY "Id") FROM "AiProfileRevision") || ' | '
          || (SELECT string_agg(conname || ' ' || pg_get_constraintdef(oid), ' | ' ORDER BY conname) FROM pg_constraint
              WHERE conrelid = '"AiProfileRevision"'::regclass AND conname LIKE 'CK_AiProfileRevision_%')
        """;

    [PostgreSqlFact]
    public async Task Strata_revisions_read_gateway_and_the_migration_runs_again_without_a_change()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_ai_gateway_id_");
        // The checks as the company provider's migration made them before 5.0 (its file now says gateway).
        await ExecuteAsync(schema.ConnectionString, $"""
            ALTER TABLE "AiProfileRevision" DROP CONSTRAINT "CK_AiProfileRevision_Gateway", DROP CONSTRAINT "CK_AiProfileRevision_Provider";
            ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Provider"
                CHECK ("Provider" IN ('openai-compatible', 'anthropic', 'strata'));
            ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Strata"
                CHECK ("Provider" <> 'strata' OR ("Location" = 'company' AND "Kind" IN ('chat', 'quick')));
            DELETE FROM "SchemaVersion" WHERE "Version" = {GatewayIdVersion};
            """);
        var (admin, strata) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES (@admin,'admin','Admin',true,true,'stamp');
            {Revision};
            INSERT INTO "AiProfileAssignment"("Id","Kind","RevisionId","ChangedById","ActiveFrom","ActiveTo") VALUES (gen_random_uuid(),'chat',@id,@admin,now(),NULL);
            """, ("admin", admin), ("id", strata), ("provider", "strata"), ("location", "company"));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("gateway", await ScalarAsync(schema.ConnectionString, """SELECT "Provider" FROM "AiProfileRevision" WHERE "Id"=@id""", ("id", strata)));
        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "AiProfileAssignment" WHERE "RevisionId"=@id AND "ActiveTo" IS NULL""", ("id", strata)));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM pg_constraint WHERE conrelid = '"AiProfileRevision"'::regclass AND conname = 'CK_AiProfileRevision_Strata'"""));
        await ExecuteAsync(schema.ConnectionString, Revision, ("admin", admin), ("id", Guid.NewGuid()), ("provider", "gateway"), ("location", "company"));
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(schema.ConnectionString, Revision, ("admin", admin), ("id", Guid.NewGuid()), ("provider", "gateway"), ("location", "cloud")));

        var migrated = await ScalarAsync(schema.ConnectionString, State);
        await ExecuteAsync(schema.ConnectionString, $"""DELETE FROM "SchemaVersion" WHERE "Version" = {GatewayIdVersion}""");
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        Assert.Equal(migrated, await ScalarAsync(schema.ConnectionString, State));
    }

    [PostgreSqlFact]
    public async Task An_installation_that_never_stored_strata_keeps_its_revisions()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_ai_gateway_id_");
        // The checks of a fresh 5.0 installation before this migration: the company provider's migration as this tree has it.
        await ExecuteAsync(schema.ConnectionString, $"""
            ALTER TABLE "AiProfileRevision" DROP CONSTRAINT "CK_AiProfileRevision_Gateway", DROP CONSTRAINT "CK_AiProfileRevision_Provider";
            ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Provider"
                CHECK ("Provider" IN ('openai-compatible', 'anthropic', 'gateway'));
            ALTER TABLE "AiProfileRevision" ADD CONSTRAINT "CK_AiProfileRevision_Gateway"
                CHECK ("Provider" <> 'gateway' OR ("Location" = 'company' AND "Kind" IN ('chat', 'quick')));
            DELETE FROM "SchemaVersion" WHERE "Version" = {GatewayIdVersion};
            """);
        var admin = Guid.NewGuid();
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES (@admin,'admin','Admin',true,true,'stamp');
            {Revision};
            """, ("admin", admin), ("id", Guid.NewGuid()), ("provider", "gateway"), ("location", "company"));
        var rows = await ScalarAsync(schema.ConnectionString, """SELECT string_agg("Id"::text || '=' || "Provider", ',') FROM "AiProfileRevision" """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(rows, await ScalarAsync(schema.ConnectionString, """SELECT string_agg("Id"::text || '=' || "Provider", ',') FROM "AiProfileRevision" """));
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(schema.ConnectionString, Revision, ("admin", admin), ("id", Guid.NewGuid()), ("provider", "gateway"), ("location", "cloud")));
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
        => ScalarAsync(connectionString, sql, parameters);
}
