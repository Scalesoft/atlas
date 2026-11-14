using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Platform.Retention;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.PersonalLibrary;

public sealed class PersonalLibraryHandlerTests
{
    [PostgreSqlFact]
    public async Task Favorites_are_explicit_idempotent_and_temporal_even_across_concurrent_tabs()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Favorite");
        var states = await Task.WhenAll(
            f.As<PersonalPageState>(f.Writer, new SetPageFavorite(page.Id, new SetFavorite(true))),
            f.As<PersonalPageState>(f.Writer, new SetPageFavorite(page.Id, new SetFavorite(true))));
        Assert.All(states, state => Assert.True(state.IsFavorite));
        await f.As<PersonalPageState>(f.Writer, new SetPageFavorite(page.Id, new SetFavorite(false)));
        Assert.False((await f.As<PersonalPageState>(f.Writer, new SetPageFavorite(page.Id, new SetFavorite(false)))).IsFavorite);
        await f.As<PersonalPageState>(f.Writer, new SetPageFavorite(page.Id, new SetFavorite(true)));
        using var session = f.Host.OpenSession();
        var history = await session.QueryOver<PageFavorite>().Where(x => x.UserId == f.Writer.Id && x.PageId == page.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, history.Count);
        Assert.NotNull(history[0].ActiveTo);
        Assert.Null(history[1].ActiveTo);
        Assert.True(history[0].ActiveTo < history[1].ActiveFrom);
        Assert.All(history, row => Assert.Equal(DateTimeKind.Utc, row.ActiveFrom.Kind));
        Assert.Equal(page.Id, Assert.Single((await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("favorites", 0, 20))).Items).Id);
        Assert.Empty((await f.As<PersonalLibraryList>(f.OtherWriter, new ListPersonalLibrary("favorites", 0, 20))).Items);
    }

    [PostgreSqlFact]
    public async Task Visits_are_throttled_retained_to_100_and_never_shared_between_users()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync(isolated: true) /* own database: runs the retention sweep, which trims and ages every operational log of the installation (job runs, notifications, page views) */;
        var first = await f.Create("First");
        var before = await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(first.Id));
        var repeated = await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(first.Id));
        Assert.Equal(before.VisitedAt, repeated.VisitedAt);
        Assert.Equal(DateTimeKind.Utc, before.VisitedAt!.Value.Kind);
        Assert.Empty((await f.As<PersonalLibraryList>(f.OtherWriter, new ListPersonalLibrary("recent", 0, 20))).Items);
        for (var i = 0; i < 100; i++)
        {
            var page = await f.Create("Page " + i);
            await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id));
        }
        // The writer only records; the hourly retention keeps the newest hundred per account (ARCH3-1).
        await f.Host.Services.GetRequiredService<OperationalRetention>().RunAsync(default);
        using var session = f.Host.OpenSession();
        Assert.Equal(100, await session.QueryOver<PageVisit>().Where(x => x.UserId == f.Writer.Id).RowCountAsync());
        Assert.Null((await f.As<PersonalPageState>(f.Writer, new GetPersonalPageState(first.Id))).VisitedAt);
        var recent = await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("recent", 0, 50));
        Assert.Equal(50, recent.Items.Count);
        Assert.True(recent.HasMore);
        Assert.Equal(50, recent.NextOffset);
        var next = await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("recent", 50, 50));
        Assert.Equal(50, next.Items.Count);
        Assert.False(next.HasMore);
        Assert.Empty(recent.Items.Select(x => x.Id).Intersect(next.Items.Select(x => x.Id)));
    }

    [PostgreSqlFact]
    public async Task Personal_lists_revalidate_visibility_publication_and_security_stamp()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Published name");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, default);
        await f.As<PersonalPageState>(f.Reader, new SetPageFavorite(page.Id, new SetFavorite(true)));
        await f.As<PersonalPageState>(f.Reader, new RecordPageVisit(page.Id));
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Draft name", PersonalLibraryFixture.Rich("draft"), "draft", 1), f.Writer, default);
        Assert.Equal("Published name", Assert.Single((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("favorites", 0, 20))).Items).Title);
        Assert.Equal("Published name", Assert.Single((await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("updates", 0, 20))).Items).Title);
        Assert.Empty((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("drafts", 0, 20))).Items);
        var privatePage = await f.Create("Private");
        await Denied(404, () => f.As<PersonalPageState>(f.OtherWriter, new GetPersonalPageState(privatePage.Id)));
        await Denied(404, () => f.As<PersonalPageState>(f.Reader, new SetPageFavorite(privatePage.Id, new SetFavorite(true))));
        await Denied(404, () => f.As<PersonalPageState>(f.Reader, new RecordPageVisit(privatePage.Id)));
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.Reader.Id);
        foreach (var kind in new[] { "recent", "favorites", "drafts", "updates", "changes" })
        {
            Assert.Empty((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary(kind, 0, 20))).Items);
        }
        await Denied(404, () => f.As<PersonalPageState>(f.Reader, new GetPersonalPageState(page.Id)));
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Admin.Id);
        await Denied(401, () => f.As<PersonalLibraryList>(f.Admin, new ListPersonalLibrary("updates", 0, 20)));
    }

    [PostgreSqlFact]
    public async Task Changes_order_working_and_publication_events_and_page_with_the_existing_bounds()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var publishedLast = await f.Create("Published last");
        await f.Pages.PublishAsync(publishedLast.Id, new PublishPageRequest(1), f.Writer, CancellationToken.None);
        var draftOnly = await f.Create("Draft only");
        var edited = await f.Create("Published then edited");
        await f.Pages.PublishAsync(edited.Id, new PublishPageRequest(1), f.Writer, CancellationToken.None);
        await f.Pages.SaveAsync(edited.Id,
            new SavePageRequest("Private draft", PersonalLibraryFixture.Rich("draft"), "draft", 1), f.Writer, CancellationToken.None);

        await f.Execute("""
            UPDATE "PageRevision" SET "ActiveFrom"=TIMESTAMPTZ '2030-01-01 00:00:00+00'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, publishedLast.Id);
        await f.Execute("""
            UPDATE "PagePublication" SET "ActiveFrom"=TIMESTAMPTZ '2030-01-05 00:00:00+00'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, publishedLast.Id);
        await f.Execute("""
            UPDATE "PageRevision" SET "ActiveFrom"=TIMESTAMPTZ '2030-01-04 00:00:00+00'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, draftOnly.Id);
        await f.Execute("""
            UPDATE "PageRevision" SET "ActiveFrom"=TIMESTAMPTZ '2030-01-03 00:00:00+00'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, edited.Id);
        await f.Execute("""
            UPDATE "PagePublication" SET "ActiveFrom"=TIMESTAMPTZ '2030-01-02 00:00:00+00'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, edited.Id);

        var first = await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("changes", 0, 2));
        Assert.Equal([publishedLast.Id, draftOnly.Id], first.Items.Select(x => x.Id));
        Assert.Equal(new DateTime(2030, 1, 5, 0, 0, 0, DateTimeKind.Utc), first.Items[0].ChangedAt);
        Assert.False(first.Items[0].HasUnpublishedChanges);
        Assert.Equal(new DateTime(2030, 1, 4, 0, 0, 0, DateTimeKind.Utc), first.Items[1].ChangedAt);
        Assert.True(first.Items[1].HasUnpublishedChanges);
        Assert.True(first.HasMore);
        Assert.Equal(2, first.NextOffset);

        var tail = await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("changes", 2, 2));
        var editedItem = Assert.Single(tail.Items);
        Assert.Equal(edited.Id, editedItem.Id);
        Assert.Equal("Private draft", editedItem.Title);
        Assert.Equal(new DateTime(2030, 1, 3, 0, 0, 0, DateTimeKind.Utc), editedItem.ChangedAt);
        Assert.True(editedItem.HasUnpublishedChanges);
        Assert.False(tail.HasMore);
        await Denied(400, () => f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("changes", 0, 51)));
    }

    [PostgreSqlFact]
    public async Task Changes_show_readers_only_the_published_metadata_and_publication_time()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Published title");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, CancellationToken.None);
        await f.Pages.SaveAsync(page.Id,
            new SavePageRequest("Confidential draft", PersonalLibraryFixture.Rich("secret"), "secret", 1), f.Writer, CancellationToken.None);
        await f.Execute("""
            UPDATE "PageRevision" SET "ActiveFrom"=TIMESTAMPTZ '2030-02-06 00:00:00+00'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, page.Id);
        await f.Execute("""
            UPDATE "PagePublication" SET "ActiveFrom"=TIMESTAMPTZ '2030-02-02 00:00:00+00'
            WHERE "PageId"=:id AND "ActiveTo" IS NULL
            """, page.Id);

        var readerItem = Assert.Single((await f.As<PersonalLibraryList>(
            f.Reader, new ListPersonalLibrary("changes", 0, 20))).Items);
        Assert.Equal("Published title", readerItem.Title);
        Assert.Equal(1, readerItem.Version);
        Assert.Equal(new DateTime(2030, 2, 2, 0, 0, 0, DateTimeKind.Utc), readerItem.ChangedAt);
        Assert.False(readerItem.HasUnpublishedChanges);

        var writerItem = Assert.Single((await f.As<PersonalLibraryList>(
            f.Writer, new ListPersonalLibrary("changes", 0, 20))).Items);
        Assert.Equal("Confidential draft", writerItem.Title);
        Assert.Equal(2, writerItem.Version);
        Assert.Equal(new DateTime(2030, 2, 6, 0, 0, 0, DateTimeKind.Utc), writerItem.ChangedAt);
        Assert.True(writerItem.HasUnpublishedChanges);
    }

    [PostgreSqlFact]
    public async Task Updates_carry_the_change_summary_of_the_current_publication()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Described");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1, "Doplněn postup"), f.Writer, default);
        await f.As<PersonalPageState>(f.Reader, new RecordPageVisit(page.Id));
        Assert.Equal("Doplněn postup", Assert.Single((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("updates", 0, 20))).Items).ChangeSummary);
        // Only the publication list carries it: a recent entry is not a description of a change.
        Assert.Null(Assert.Single((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("recent", 0, 20))).Items).ChangeSummary);
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Described", PersonalLibraryFixture.Rich("next"), "next", 1), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(2), f.Writer, default);
        Assert.Null(Assert.Single((await f.As<PersonalLibraryList>(f.Reader, new ListPersonalLibrary("updates", 0, 20))).Items).ChangeSummary);
    }

    [PostgreSqlFact]
    public async Task Draft_participation_tracks_work_since_publication_without_exposing_foreign_private_drafts()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Shared");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, default);
        Assert.Empty((await f.As<PersonalLibraryList>(f.OtherWriter, new ListPersonalLibrary("drafts", 0, 20))).Items);
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Other edit", PersonalLibraryFixture.Rich("o"), "o", 1), f.OtherWriter, default);
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Owner edit", PersonalLibraryFixture.Rich("w"), "w", 2), f.Writer, default);
        Assert.Equal(page.Id, Assert.Single((await f.As<PersonalLibraryList>(f.OtherWriter, new ListPersonalLibrary("drafts", 0, 20))).Items).Id);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(3), f.Writer, default);
        Assert.Empty((await f.As<PersonalLibraryList>(f.OtherWriter, new ListPersonalLibrary("drafts", 0, 20))).Items);
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Next work", PersonalLibraryFixture.Rich("n"), "n", 3), f.Writer, default);
        Assert.Empty((await f.As<PersonalLibraryList>(f.OtherWriter, new ListPersonalLibrary("drafts", 0, 20))).Items);
        await f.Create("Private concept");
        Assert.Equal(2, (await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("drafts", 0, 20))).Items.Count);
        Assert.Empty((await f.As<PersonalLibraryList>(f.OtherWriter, new ListPersonalLibrary("drafts", 0, 20))).Items);
    }

    [PostgreSqlFact]
    public async Task Trashed_pages_disappear_without_erasing_personal_state_and_pagination_is_bounded()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Kept");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(1), f.Writer, default);
        await f.As<PersonalPageState>(f.Writer, new SetPageFavorite(page.Id, new SetFavorite(true)));
        await f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id));
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        var changed = await f.As<OrganizationChange>(f.Writer, new TrashPage(page.Id, new TrashPageRequest(false, tree.Version, 1)));
        foreach (var kind in new[] { "favorites", "recent", "drafts", "updates", "changes" })
        {
            Assert.Empty((await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary(kind, 0, 20))).Items);
        }
        await Denied(404, () => f.As<PersonalPageState>(f.Writer, new GetPersonalPageState(page.Id)));
        await f.As<OrganizationChange>(f.Admin, new UntrashPage(page.Id, new UntrashPageRequest(null, false, changed.Version)));
        Assert.True((await f.As<PersonalPageState>(f.Writer, new GetPersonalPageState(page.Id))).IsFavorite);
        Assert.Equal(page.Id, Assert.Single((await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("recent", 0, 20))).Items).Id);
        await Denied(400, () => f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("unknown", 0, 20)));
        await Denied(400, () => f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("recent", -1, 20)));
        await Denied(400, () => f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("recent", 0, 51)));
        await Denied(400, () => f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("recent", 1001, 20)));
    }

    private static async Task Denied(int status, Func<Task> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }
}
