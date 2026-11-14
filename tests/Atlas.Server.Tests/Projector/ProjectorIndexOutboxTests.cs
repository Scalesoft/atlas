using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Persistence;
using Atlas.Server.Features.Projector.Queries;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine.Tracking;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;
using Converter = Atlas.Server.Tests.Pages.RequirementHeadingConverterTests;

namespace Atlas.Server.Tests.Projector;

/// <summary>The requirement index in code (migration 2026110870) follows a save through the outbox: the save announces its revision once,
/// so the index gets exactly one refresh of the page, delivered after the commit; the index then holds the new revision without any read.</summary>
public sealed class ProjectorIndexOutboxTests
{
    [PostgreSqlFact]
    public async Task A_save_sends_one_refresh_of_the_page_and_the_index_follows_it()
    {
        // Private: the outbox's queue delivers and DrainAsync waits for the whole envelope table (wolverine_incoming/outgoing) of its database.
        await using var host = await AtlasTestHost.CreateAsync("atlas_projector_index_outbox_", modules: TestModules.AllEnabled, outbox: true);
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        var space = await host.SeedSpaceAsync("Provoz");
        host.Actor.Current = admin;
        var page = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, null, "Nasazení", Json(Paragraph("Úvod")), null)));
        await OutboxTests.DrainAsync(host);

        var tracked = await host.TrackAsync(() => host.InvokeAsync<PageDto>(new SavePage(page.Id,
            new SavePageRequest("Nasazení", Json(Paragraph("Druhá verze")), null, page.Version))));
        await OutboxTests.DrainAsync(host);

        Assert.Single(tracked.Sent.MessagesOf<ProjectorIndexRevision>(), x => x.Event.ResourceId == page.Id);
        Assert.Equal(1L, await OutboxTests.ScalarAsync(host.ConnectionString, $"""
            SELECT count(*) FROM "ProjectorIndexedRevision" x JOIN "PageRevision" r ON r."Id" = x."RevisionId" AND r."ActiveTo" IS NULL
            WHERE x."PageId" = '{page.Id}'
            """));
    }

    /// <summary>Review P2-4: a read that finds the index of a page behind while another transaction holds the page's refresh (the outbox's,
    /// here a bare lock) waits for it and refreshes the page itself: a just-saved requirement is found, never a 404 from the stale index.</summary>
    [PostgreSqlFact]
    public async Task A_read_waits_for_a_refresh_another_transaction_holds_and_finds_the_saved_requirement()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL")));
        await f.FreshIndexAsync();

        await using var holder = new NpgsqlConnection(f.Host.ConnectionString);
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 812))", holder, transaction))
        {
            command.Parameters.AddWithValue("key", "atlas.projector-index:" + page.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        // Saved under the held lock: another test's read across every page (DependentsAsync) cannot refresh the page before this read.
        await f.SaveWorkingAsync(page, Json(Block(B1, "portal.ssl", "SSL"), Block(B2, "portal.dns", "DNS")));
        var read = f.As<RequirementDependents>(f.Writer, new GetRequirementDependents(page, B2));
        Assert.NotSame(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(500))));
        await transaction.CommitAsync();

        Assert.Equal("portal.dns", (await read.WaitAsync(TimeSpan.FromSeconds(30))).Key);
    }

    /// <summary>4.0 first start (a deadlock on every run): the requirement conversion holds its page (FOR UPDATE) and later drops the page's
    /// index marks, while the outbox or the index job refreshes the same page. The refresh used to take the marks first and then wait for
    /// the page's row at its foreign key, so each waited for the other. The interleaving is forced: the refresh (a batch, as the job's)
    /// starts while the conversion holds the page, and the conversion goes on once the refresh is done or waits for a lock. Now the
    /// refresh takes the page's row first and leaves a page a writer holds; the page stays stale for the next read, which refreshes it.</summary>
    [PostgreSqlFact]
    public async Task A_refresh_of_pages_the_requirement_conversion_holds_does_not_deadlock_with_it()
    {
        // Private: the interleaving is forced by waiting for any lock wait of the database (pg_stat_activity), which other tests' writes would
        // satisfy; and the "projector" host's trigger marks every page rolled back, which the conversion under test then skips.
        await using var host = await AtlasTestHost.CreateAsync("atlas_projector_index_conversion_", modules: TestModules.AllEnabled);
        var writer = await host.SeedUserAsync("writer");
        var space = await host.SeedSpaceAsync("Provoz");
        Guid page = Guid.NewGuid(), other = Guid.NewGuid();
        await OutboxTests.ExecuteAsync(host.ConnectionString, $"""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES ('{page}','{space}','{writer.Id}',1), ('{other}','{space}','{writer.Id}',1);
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","ActiveTo","Channel") VALUES
              (gen_random_uuid(),'{page}',1,'Nasazení','{Converter.Envelope(Converter.Block(B1, "portal.ssl", "SSL"))}','','{writer.Id}',now(),NULL,'web'),
              (gen_random_uuid(),'{other}',1,'Jiné','{Converter.Envelope(Converter.Block(B2, "portal.dns", "DNS"))}','','{writer.Id}',now(),NULL,'web');
            """);
        await RefreshAsync(host, [page, other]);
        Task<object?> MarksAsync(Guid id) => OutboxTests.ScalarAsync(host.ConnectionString, $"""SELECT count(*) FROM "ProjectorIndexedRevision" WHERE "PageId" = '{id}'""");

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
            using var transaction = session.BeginTransaction();
            await scope.ServiceProvider.GetRequiredService<IAtlasLocks>().LockPagesForUpdateAsync([page], default);
            var refresh = RefreshAsync(host, [page, other]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!refresh.IsCompleted && (long)(await OutboxTests.ScalarAsync(host.ConnectionString,
                       """SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'"""))! == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
            var conversion = await scope.ServiceProvider.GetRequiredService<RequirementPageConversion>().ConvertPagesAsync([page], null, default);
            await transaction.CommitAsync();
            await refresh.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(page, Assert.Single(conversion.Converted));
        }

        // The refresh left the converted page to its writer, whose rewrite dropped its marks; the other page it refreshed. A read catches up.
        Assert.Equal(0L, await MarksAsync(page));
        Assert.Equal(1L, await MarksAsync(other));
        using (var session = host.OpenSession())
        {
            await ProjectorIndex.EnsureFreshAsync(session, [page], default);
        }
        Assert.Equal(1L, await MarksAsync(page));
    }

    /// <summary>The refresh of the outbox and the job (<see cref="ProjectorIndex.RefreshAsync"/> without waiting), in its own transaction.</summary>
    private static Task RefreshAsync(AtlasTestHost host, IReadOnlyCollection<Guid> pages) => Task.Run(async () =>
    {
        using var session = host.OpenSession();
        using var transaction = session.BeginTransaction();
        await ProjectorIndex.RefreshAsync(session, pages, default);
        await transaction.CommitAsync();
    });

    private const string B1 ="00000000-0000-4000-8000-0000000000b1", B2 = "00000000-0000-4000-8000-0000000000b2";
}
