using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary;
using Atlas.Server.Features.Pages.Contracts;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.PersonalLibrary.Domain;

namespace Atlas.Server.Tests.PersonalLibrary;

public sealed class PageActivityTests
{
    [PostgreSqlFact]
    public async Task Readership_rechecks_a_page_moved_between_space_lookup_and_authorization()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Moved");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, default);
        var target = await f.Host.SeedSpaceAsync("Inaccessible");
        f.Host.Actor.Current = f.Reader;
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        var access = new BeforeLockAccess(scope.ServiceProvider.GetRequiredService<IContentAccess>(), () =>
            f.Host.SeedAsync(s => s.CreateSQLQuery("""UPDATE "Page" SET "SpaceId"=:target WHERE "Id"=:page""")
                .SetParameter("target", target).SetParameter("page", page.Id).ExecuteUpdateAsync()));
        var locks = scope.ServiceProvider.GetRequiredService<IAtlasLocks>();
        // The guard enters through the page gate, which locks the actor through the same access.
        var guard = new PersonalPageGuard(session, scope.ServiceProvider.GetRequiredService<ICurrentActor>(),
            access, locks, new Atlas.Server.Platform.Identity.PageGate(session, access, locks), new Atlas.Server.Platform.Resources.ContentParticipants(scope.ServiceProvider));
        var handler = new AnalyticsHandler(session, guard, scope.ServiceProvider.GetRequiredService<Atlas.Server.Features.Vaults.VaultGrantSnapshots>(),
            scope.ServiceProvider.GetRequiredService<IContentAccess>());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() =>
            handler.Handle(new GetPageReadership(page.Id, today, today), default))).Status);
    }

    private sealed class BeforeLockAccess(IContentAccess inner, Func<Task> beforeLock) : IContentAccess
    {
        public async Task<UserAccount> LockActorAsync(Guid? spaceId, CancellationToken ct)
        {
            await beforeLock();
            return await inner.LockActorAsync(spaceId, ct);
        }
        public async Task<UserAccount> LockActorInSpacesAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct)
        {
            await beforeLock();
            return await inner.LockActorInSpacesAsync(spaceIds, ct);
        }
        public Task<SpaceRole> RequireSpaceAsync(Guid spaceId, SpaceRole required, CancellationToken ct) =>
            inner.RequireSpaceAsync(spaceId, required, ct);
        public Task<PageAuthorization> RequirePageAsync(Page page, AccessOperation operation, CancellationToken ct) =>
            inner.RequirePageAsync(page, operation, ct);
        public Task<PageAuthorization> AuthorizePageAsync(Page page, AccessOperation operation, CancellationToken ct) =>
            inner.AuthorizePageAsync(page, operation, ct);
        public Task<IReadOnlyList<PageAuthorization>> AuthorizePagesAsync(IReadOnlyList<Page> pages, AccessOperation operation, CancellationToken ct) =>
            inner.AuthorizePagesAsync(pages, operation, ct);
    }

    [PostgreSqlFact]
    public async Task Revision_counts_use_Prague_days_and_current_history_permissions()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Shared");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, default);
        await f.Execute("""UPDATE "PageRevision" SET "ActiveFrom"=TIMESTAMPTZ '2026-03-28 23:30:00+00' WHERE "PageId"=:id""", page.Id);
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Edited", PersonalLibraryFixture.Rich("edit"), "edit", 1), f.OtherWriter, default);
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Edited", PersonalLibraryFixture.Rich("edit"), "edit", 2), f.OtherWriter, default);
        await f.Create("Private");
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 12, 31);
        var mine = await f.As<ActivitySummary>(f.OtherWriter, new GetActivity(from, to, "mine"));
        Assert.Equal(1, mine.Revisions);
        var wiki = await f.As<ActivitySummary>(f.OtherWriter, new GetActivity(from, to, "wiki"));
        Assert.Equal(2, wiki.Revisions);
        Assert.Equal(1, wiki.Pages);
        Assert.Contains(wiki.Days, day => day.Day == "2026-03-29" && day.Count == 1);
        var changes = await f.As<ActivityChanges>(f.OtherWriter, new GetActivityChanges(new DateOnly(2026, 3, 29), "wiki"));
        Assert.Equal("Shared", Assert.Single(changes.Items).Title);
        Assert.Equal(f.Writer.Id, changes.Items[0].AuthorId);
        Assert.Empty((await f.As<ActivitySummary>(f.Reader, new GetActivity(from, to, "wiki"))).Days);
        await f.Execute("""UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=:id AND "ActiveTo" IS NULL""", f.OtherWriter.Id);
        Assert.Empty((await f.As<ActivityChanges>(f.OtherWriter, new GetActivityChanges(new DateOnly(2026, 3, 29), "wiki"))).Items);
    }

    [PostgreSqlFact]
    public async Task Readership_counts_users_and_hides_private_or_revoked_pages()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Public title");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, default);
        await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id));
        await f.As<PersonalPageState>(f.Reader, new RecordPageVisit(page.Id));
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Secret title", PersonalLibraryFixture.Rich("secret"), "secret", 1), f.Writer, default);
        var privatePage = await f.Create("Private");
        await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(privatePage.Id));
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var to = from.AddDays(2);
        var result = await f.As<PageReadership>(f.Reader, new GetPageReadership(page.Id, from, to));
        Assert.Equal(2, result.Views);
        Assert.Equal(2, result.Readers);
        Assert.Equal(2, result.Items.Count);
        Assert.Empty((await f.As<PageReadership>(f.Reader, new GetPageReadership(page.Id, from, to, 10050))).Items);
        Assert.All(result.Items, view => Assert.Equal(DateTimeKind.Utc, view.At.Kind));
        var popular = await f.As<PopularPages>(f.Reader, new GetPopularPages(from, to));
        Assert.Equal("Public title", Assert.Single(popular.Items).Title);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() =>
            f.As<PageReadership>(f.Reader, new GetPageReadership(privatePage.Id, from, to)))).Status);
        await f.Execute("""UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=:id AND "ActiveTo" IS NULL""", f.Reader.Id);
        Assert.Empty((await f.As<PopularPages>(f.Reader, new GetPopularPages(from, to))).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() =>
            f.As<PageReadership>(f.Reader, new GetPageReadership(page.Id, from, to)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            f.As<ActivitySummary>(f.Writer, new GetActivity(to, from, "wiki")))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() =>
            f.As<ActivityChanges>(f.Writer, new GetActivityChanges(from, "invalid")))).Status);
    }

    [PostgreSqlFact]
    public async Task Views_survive_navigation_retention_and_concurrent_refresh_is_throttled()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Viewed");
        await Task.WhenAll(
            f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id)),
            f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id)));
        await f.Execute("""DELETE FROM "PageVisit" WHERE "PageId"=:id""", page.Id);
        await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id));
        using var session = f.Host.OpenSession();
        var count = await session.CreateSQLQuery("""SELECT count(*) FROM "PageView" WHERE "PageId"=:id""")
            .SetParameter("id", page.Id).UniqueResultAsync<long>();
        Assert.Equal(1, count);
        await f.Execute("""UPDATE "PageView" SET "ViewedAt"="ViewedAt"-interval '61 seconds' WHERE "PageId"=:id""", page.Id);
        await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id));
        Assert.Equal(2, await session.CreateSQLQuery("""SELECT count(*) FROM "PageView" WHERE "PageId"=:id""")
            .SetParameter("id", page.Id).UniqueResultAsync<long>());
    }
}
