using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>Migration 2026102040: existing profiles, assignments, saved profiles and usage stay; the kind rerank and its usage become
/// valid, on an OpenAI-compatible server only.</summary>
public sealed class AiRerankProfileMigrationTests
{
    private const long RerankVersion = 2026102040;

    [PostgreSqlFact]
    public async Task Existing_profiles_and_usage_stay_and_rerank_becomes_valid()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_ai_rerank_");
        await AtlasMigrations.MigrateAsync(schema.ConnectionString);
        // The schema as it was before this migration, with the rows of an installation that used AI.
        await ExecuteAsync(schema.ConnectionString, $"""
            ALTER TABLE "AiProfileRevision" DROP CONSTRAINT "CK_AiProfileRevision_Rerank", DROP CONSTRAINT "CK_AiProfileRevision_Kind",
                ADD CONSTRAINT "AiProfileRevision_Kind_check" CHECK ("Kind" IN ('chat', 'embeddings', 'quick'));
            ALTER TABLE "AiProfileAssignment" DROP CONSTRAINT "CK_AiProfileAssignment_Kind",
                ADD CONSTRAINT "AiProfileAssignment_Kind_check" CHECK ("Kind" IN ('chat', 'embeddings', 'quick'));
            ALTER TABLE "AiSavedProfile" DROP CONSTRAINT "CK_AiSavedProfile_Kind",
                ADD CONSTRAINT "AiSavedProfile_Kind_check" CHECK ("Kind" IN ('chat', 'quick', 'embeddings'));
            ALTER TABLE "AiUsage" DROP CONSTRAINT "CK_AiUsage_Operation",
                ADD CONSTRAINT "AiUsage_Operation_check" CHECK ("Operation" IN ('chat', 'embedding'));
            DELETE FROM "SchemaVersion" WHERE "Version" = {RerankVersion};
            """);
        var admin = Guid.NewGuid();
        var chat = Guid.NewGuid();
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES (@admin,'admin','Admin',true,true,'stamp');
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
              VALUES (@chat,'chat',true,'openai-compatible','company','http://lmstudio.example:1234/v1','gemma-4',120,2,@admin,now());
            INSERT INTO "AiProfileAssignment"("Id","Kind","RevisionId","ChangedById","ActiveFrom","ActiveTo") VALUES (gen_random_uuid(),'chat',@chat,@admin,now(),NULL);
            INSERT INTO "AiUsage"("OccurredAt","Task","Kind","Operation","ProfileRevisionId","Model","LatencyMs","Outcome")
              VALUES (now(),'chat.answer','chat','chat',@chat,'gemma-4',100,'ok');
            """, ("admin", admin), ("chat", chat));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString,
            """SELECT count(*) FROM "AiProfileAssignment" a JOIN "AiProfileRevision" r ON r."Id"=a."RevisionId" WHERE a."Kind"='chat' AND r."Model"='gemma-4'"""));
        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "AiUsage" WHERE "Operation"='chat'"""));
        var rerank = Guid.NewGuid();
        await ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
              VALUES (@rerank,'rerank',true,'openai-compatible','company','http://reranker.example:8012/v1','bge-reranker-v2-m3',10,4,@admin,now());
            INSERT INTO "AiProfileAssignment"("Id","Kind","RevisionId","ChangedById","ActiveFrom","ActiveTo") VALUES (gen_random_uuid(),'rerank',@rerank,@admin,now(),NULL);
            INSERT INTO "AiUsage"("OccurredAt","Task","Kind","Operation","ProfileRevisionId","Model","Items","LatencyMs","Outcome")
              VALUES (now(),'search.rerank','rerank','rerank',@rerank,'bge-reranker-v2-m3',30,80,'ok');
            """, ("admin", admin), ("rerank", rerank));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(schema.ConnectionString, """
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
              VALUES (gen_random_uuid(),'rerank',true,'anthropic','cloud',NULL,'claude',10,4,@admin,now())
            """, ("admin", admin)));
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
