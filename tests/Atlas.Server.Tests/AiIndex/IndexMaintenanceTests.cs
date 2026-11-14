using Xunit;

namespace Atlas.Server.Tests.AiIndex;

public sealed class IndexMaintenanceTests
{
    [PostgreSqlFact]
    public async Task Consistency_check_enqueues_missing_and_stale_pages_and_orphans()
    {
        await using var f = await IndexFixture.CreateAsync();
        var missing = await f.PublishNewAsync("Chybí", "Řádek zmizel.");
        var stale = await f.PublishNewAsync("Zastaralá", "Řádek drží jinou revizi.");
        var orphan = await f.PublishNewAsync("Sirotek", "Stránka je v koši, řádek zůstal.");
        var generation = await f.ActiveIndexAsync();
        Assert.True((await f.Maintenance.RunOnceAsync(f.Clock.GetUtcNow(), default)).Check is { Missing: 0, Stale: 0, Orphaned: 0 });

        // A hand-broken index: a deleted row, a row of another revision of the page, a row of a trashed page; no tasks.
        await f.SaveDraftAsync(stale.Id, "Zastaralá", "Pracovní verze.");
        await f.Host.ExecuteAsync("""DELETE FROM "KnowledgePage" WHERE "PageId"=:id""", missing.Id);
        await f.Host.ExecuteAsync("""
            UPDATE "KnowledgePage" k SET "RevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=k."PageId" AND r."ActiveTo" IS NULL AND k."PageId"=:id
            """, stale.Id);
        await f.Host.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"="OwnerId","TrashBatchId"=gen_random_uuid() WHERE "Id"=:id
            """, orphan.Id);
        await f.Host.ExecuteAsync("""DELETE FROM "IndexTask" WHERE "GenerationId"=:id""", generation.Id);

        var (check, _) = await f.Maintenance.RunOnceAsync(f.Clock.GetUtcNow(), default);
        Assert.Equal((1, 1, 1), (check.Missing, check.Stale, check.Orphaned));
        Assert.Equal(3, (await f.TasksAsync()).Count);

        await f.RunAsync();
        Assert.Equal(await f.PublishedRevisionAsync(missing.Id), (await f.IndexedAsync(generation.Id, missing.Id))!.RevisionId);
        Assert.Equal(await f.PublishedRevisionAsync(stale.Id), (await f.IndexedAsync(generation.Id, stale.Id))!.RevisionId);
        Assert.Null(await f.IndexedAsync(generation.Id, orphan.Id));
        Assert.True((await f.Maintenance.RunOnceAsync(f.Clock.GetUtcNow(), default)).Check is { Missing: 0, Stale: 0, Orphaned: 0 });
    }
}
