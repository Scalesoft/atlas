using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Collaboration;
using Atlas.Server.Tests.ContentExport;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class DelayedAuthorizationTests
{
    [PostgreSqlFact]
    public async Task Export_final_guard_rejects_another_writers_private_draft_and_a_page_trashed_after_build()
    {
        await using var fixture = await ContentExportFixture.CreateAsync(nameof(DelayedAuthorizationTests));
        var page = await fixture.As<PageDto>(fixture.Writer, new CreatePage(new CreatePageRequest(
            fixture.SpaceId, null, "Private draft", ContentExportFixture.Rich("private"), "private")));
        await using var archive = await fixture.As<ContentExportArchive>(fixture.Writer, new BuildContentExport(page.Id, false));

        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => fixture.As(fixture.OtherWriter,
            new RevalidateContentExport(fixture.SpaceId, archive.Selected)))).Status);

        using var session = fixture.Host.Database.OpenSession();
        var treeVersion = await TreeVersion.ReadAsync(session, fixture.SpaceId, CancellationToken.None);
        await fixture.As<OrganizationChange>(fixture.Writer, new TrashPage(page.Id, new TrashPageRequest(false, treeVersion, page.Version)));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => fixture.As(fixture.Writer,
            new RevalidateContentExport(fixture.SpaceId, archive.Selected)))).Status);
    }

    [PostgreSqlFact]
    public async Task Collaboration_append_rechecks_expiry_within_the_same_open_transaction()
    {
        await using var fixture = await CollaborationFixture.CreateAsync();
        var opened = await fixture.Store.OpenAsync(fixture.Page.Id, fixture.Writer);
        await fixture.Host.ExecuteAsync("""
            WITH closed AS (
                UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp()
                WHERE "UserId"=:id AND "ActiveTo" IS NULL
                RETURNING "SpaceId", "UserId", "ActiveTo"
            )
            INSERT INTO "SpaceGrant" ("Id", "SpaceId", "UserId", "Role", "ActiveFrom", "ExpiresAt")
            SELECT gen_random_uuid(), "SpaceId", "UserId", 2, "ActiveTo", statement_timestamp()+interval '1 day' FROM closed
            """, fixture.Writer.Id);
        using var scope = fixture.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        var roles = new SpaceRoles(session);
        Assert.Equal(SpaceRole.Writer, await roles.EffectiveAsync(fixture.Writer, fixture.Page.SpaceId, CancellationToken.None));
        // Advance only this transaction's statement clock, without changing the grant or transaction now().
        await session.CreateSQLQuery("CREATE FUNCTION statement_timestamp() RETURNS timestamptz LANGUAGE SQL STABLE AS $$ SELECT pg_catalog.statement_timestamp() + interval '2 days' $$").ExecuteUpdateAsync();
        await session.CreateSQLQuery("SELECT set_config('search_path', quote_ident(current_schema()) || ',pg_catalog,public', true)").UniqueResultAsync<string>();
        var handler = ActivatorUtilities.CreateInstance<AppendUpdateHandler>(scope.ServiceProvider);
        var command = new AppendUpdate(fixture.Page.Id, opened.Generation, Guid.NewGuid(), fixture.Writer.Id, DateTimeOffset.UtcNow, [1]);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => handler.Handle(command, CancellationToken.None))).Status);
        Assert.Equal(0, await session.QueryOver<CollaborationUpdate>().Where(x => x.PageId == fixture.Page.Id).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Collaboration_append_rechecks_writer_after_waiting_for_the_page_lock()
    {
        await using var fixture = await CollaborationFixture.CreateAsync();
        var opened = await fixture.Store.OpenAsync(fixture.Page.Id, fixture.Writer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var blocker = new NpgsqlConnection(fixture.Host.ConnectionString);
        await blocker.OpenAsync(deadline.Token);
        await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
        await using (var command = new NpgsqlCommand("SELECT 1 FROM \"Page\" WHERE \"Id\"=@page FOR UPDATE", blocker, transaction))
        {
            command.Parameters.AddWithValue("page", fixture.Page.Id);
            await command.ExecuteScalarAsync(deadline.Token);
        }
        var append = fixture.Store.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), fixture.Writer.Id.ToString(),
            DateTimeOffset.UtcNow, new byte[] { 1 }, deadline.Token);
        try
        {
            await WaitForBlockedQueryAsync(fixture.Host.ConnectionString, blocker.ProcessID, deadline.Token);
            // Direct SQL changes the authority while the transaction is suspended, without taking the
            // application's content lock. Clock expiry likewise is not serialized by that lock.
            await fixture.Host.ExecuteAsync("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", fixture.Writer.Id);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => append)).Status);
        Assert.Equal(0, (await fixture.Store.LoadAsync(opened.DocumentId, deadline.Token))!.Seq);
    }

    [PostgreSqlFact]
    public Task Export_rechecks_writer_after_waiting_for_private_pages()
        => RevalidateAfterRoleLossAsync(published: false);

    [PostgreSqlFact]
    public Task Export_of_published_content_remains_allowed_after_writer_becomes_reader()
        => RevalidateAfterRoleLossAsync(published: true);

    private static async Task RevalidateAfterRoleLossAsync(bool published)
    {
        await using var fixture = await ContentExportFixture.CreateAsync(nameof(DelayedAuthorizationTests));
        var page = await fixture.As<PageDto>(fixture.Writer, new CreatePage(new CreatePageRequest(
            fixture.SpaceId, null, "Private draft", ContentExportFixture.Rich("private"), "private")));
        if (published)
        {
            await fixture.As<PageDto>(fixture.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        }
        await using var archive = await fixture.As<ContentExportArchive>(fixture.Writer, new BuildContentExport(page.Id, false));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var blocker = new NpgsqlConnection(fixture.Host.ConnectionString);
        await blocker.OpenAsync(deadline.Token);
        await using var transaction = await blocker.BeginTransactionAsync(deadline.Token);
        await using (var command = new NpgsqlCommand("LOCK TABLE \"Page\" IN ACCESS EXCLUSIVE MODE", blocker, transaction))
        {
            await command.ExecuteNonQueryAsync(deadline.Token);
        }
        fixture.Host.Actor.Current = fixture.Writer;
        var validation = fixture.Host.InvokeAsync(new RevalidateContentExport(fixture.SpaceId, archive.Selected), deadline.Token);
        try
        {
            await WaitForBlockedQueryAsync(fixture.Host.ConnectionString, blocker.ProcessID, deadline.Token);
            await fixture.Host.ExecuteAsync("""
                WITH closed AS (
                    UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp()
                    WHERE "UserId"=:id AND "ActiveTo" IS NULL
                    RETURNING "SpaceId", "UserId", "ActiveTo"
                )
                INSERT INTO "SpaceGrant" ("Id", "SpaceId", "UserId", "Role", "ActiveFrom")
                SELECT gen_random_uuid(), "SpaceId", "UserId", 1, "ActiveTo" FROM closed
                """, fixture.Writer.Id);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        if (published)
        {
            await validation;
        }
        else
        {
            Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => validation)).Status);
        }
    }

    private static async Task WaitForBlockedQueryAsync(string connectionString, int blockerPid, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid)))", connection);
        command.Parameters.AddWithValue("blocker", blockerPid);
        while (!Convert.ToBoolean(await command.ExecuteScalarAsync(ct)))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
