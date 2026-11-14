using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiUsageFlusherTests
{
    [PostgreSqlFact]
    public async Task Buffered_usage_reaches_the_table_in_one_batch_and_purge_keeps_180_days()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings()));
        var revision = Assert.Single(await f.RevisionsAsync(AiProfileKind.Embeddings));
        var recorder = f.Host.Services.GetRequiredService<AiUsageRecorder>();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var flusher = new AiUsageFlusher(f.Host.Services.GetRequiredService<IServiceScopeFactory>(), recorder, f.Host.Database, clock,
            NullLogger<AiUsageFlusher>.Instance);

        // Through the real path: the fake provider behind the production pipeline fills the in-memory recorder.
        var fake = f.Host.Services.GetRequiredService<FakeAiClientFactory>();
        using var generator = fake.Embeddings(AiProfileSnapshot.From(revision), AiTaskCatalog.IndexEmbed);
        for (var i = 0; i < 3; i++)
        {
            await generator.GenerateAsync(["první kapitola", "druhá kapitola"]);
        }
        var now = clock.GetUtcNow().UtcDateTime;
        recorder.Record(new AiUsageEntry(now.AddDays(-181), AiTaskCatalog.SearchEmbedQuery, AiProfileKind.Embeddings, AiUsageEntry.Embedding,
            revision.Id, revision.Model, 1, null, null, 12, AiOutcome.Ok));
        recorder.Record(new AiUsageEntry(now.AddDays(-179), AiTaskCatalog.SearchEmbedQuery, AiProfileKind.Embeddings, AiUsageEntry.Embedding,
            revision.Id, revision.Model, 1, 4, null, 15, AiOutcome.Timeout));
        await flusher.FlushAsync(default);
        Assert.Empty(recorder.Drain());

        await using var sql = new NpgsqlConnection(f.Host.ConnectionString);
        await sql.OpenAsync();
        Assert.Equal(5L, await Scalar(sql, """SELECT count(*) FROM "AiUsage" """));
        // One INSERT: every row carries the same inserting transaction.
        Assert.Equal(1L, await Scalar(sql, """SELECT count(DISTINCT xmin::text) FROM "AiUsage" """));
        Assert.Equal(3L, await Scalar(sql, """SELECT count(*) FROM "AiUsage" WHERE "Task"='index.embed' AND "Items"=2 AND "InputTokens" IS NULL AND "Outcome"='ok'"""));
        Assert.Equal(4L, await Scalar(sql, """SELECT "InputTokens" FROM "AiUsage" WHERE "Outcome"='timeout'"""));

        await f.Host.Services.GetRequiredService<Atlas.Server.Platform.Retention.OperationalRetention>().RunAsync(default);
        Assert.Equal(4L, await Scalar(sql, """SELECT count(*) FROM "AiUsage" """));
        // The 181-day-old row went, the 179-day-old one stays.
        Assert.Equal(1L, await Scalar(sql, """SELECT count(*) FROM "AiUsage" WHERE "Outcome"='timeout'"""));
    }

    private static async Task<object?> Scalar(NpgsqlConnection sql, string text)
    {
        await using var command = new NpgsqlCommand(text, sql);
        return await command.ExecuteScalarAsync();
    }
}
