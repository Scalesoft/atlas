using NHibernate;
using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Tests.PersonalLibrary;
using Atlas.Server.Tests.Platform;
using SearchQuery = Atlas.Server.Features.Search.Queries.Search;
using Xunit;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Tests;

public sealed class GrantValidityReadPathsTests
{
    [PostgreSqlFact]
    public async Task Search_filters_expired_and_future_direct_and_group_grants_in_every_scope()
    {
        await using var fixture = await PersonalLibraryFixture.CreateAsync();
        var page = await fixture.Create("Validity published");
        await fixture.Pages.PublishAsync(page.Id, new(1), fixture.Writer, default);
        await fixture.Pages.SaveAsync(page.Id, new("Validity working", PersonalLibraryFixture.Rich("Validity draft"), "Validity draft", 1), fixture.Writer, default);
        var subjects = await SeedIneffectiveSubjects(fixture.Host, fixture.SpaceId);
        foreach (var subject in subjects)
        {
            var scopes = new[] { new SearchOptions(), new("published"), new("published", "week"), new("drafts") };
            var hits = new List<SearchHit>();
            foreach (var scope in scopes)
                hits.AddRange((await fixture.As<SearchResponse>(subject, new SearchQuery("Validity", null, 0, 20, scope))).Items);
            Assert.Empty(hits);
            Assert.Empty((await fixture.As<SearchResponse>(subject, new SearchQuery("History", null, 0, 20, new()))).Spaces);
        }
        Assert.Single((await fixture.As<SearchResponse>(fixture.Reader, new SearchQuery("Validity", null, 0, 20, new("published")))).Items);
        Assert.Single((await fixture.As<SearchResponse>(fixture.Writer, new SearchQuery("Validity", null, 0, 20, new("drafts")))).Items);
    }

    [PostgreSqlFact]
    public async Task Library_projections_do_not_disclose_content_through_ineffective_grants()
    {
        await using var fixture = await PersonalLibraryFixture.CreateAsync();
        var page = await fixture.Create("Validity published");
        await fixture.Pages.PublishAsync(page.Id, new(1), fixture.Writer, default);
        foreach (var subject in await SeedIneffectiveSubjects(fixture.Host, fixture.SpaceId))
            Assert.Empty((await fixture.As<PersonalLibraryList>(subject, new ListPersonalLibrary("changes", 0, 20))).Items);
        Assert.Single((await fixture.As<PersonalLibraryList>(fixture.Reader, new ListPersonalLibrary("changes", 0, 20))).Items);
    }

    [PostgreSqlFact]
    public async Task Tag_projections_do_not_disclose_content_through_ineffective_grants()
    {
        await using var fixture = await PersonalLibraryFixture.CreateAsync();
        var page = await fixture.Create("Validity published");
        await fixture.Pages.PublishAsync(page.Id, new(1), fixture.Writer, default);
        var tag = Assert.Single((await fixture.As<PageTagState>(fixture.Writer, new SetPageTags(page.Id, new(["Validity tag"], [])))).Tags);
        foreach (var subject in await SeedIneffectiveSubjects(fixture.Host, fixture.SpaceId))
        {
            Assert.Empty((await fixture.As<TagSuggestions>(subject, new SuggestTags("Validity"))).Items);
            Assert.Empty((await fixture.As<SearchResponse>(subject, new ListTaggedPages(tag.Id, null, null, 0, 20, new()))).Items);
        }
        Assert.Single((await fixture.As<TagSuggestions>(fixture.Reader, new SuggestTags("Validity"))).Items);
    }

    [PostgreSqlFact]
    public async Task Effective_reader_cannot_borrow_an_expired_group_writer_role_or_its_explanation()
    {
        await using var fixture = await PersonalLibraryFixture.CreateAsync();
        var page = await fixture.Create("Validity public title");
        await fixture.Pages.PublishAsync(page.Id, new(1), fixture.Writer, default);
        await fixture.Pages.SaveAsync(page.Id, new("Validity private title", PersonalLibraryFixture.Rich("Private body"), "Private body", 1), fixture.Writer, default);
        var tag = Assert.Single((await fixture.As<PageTagState>(fixture.Writer, new SetPageTags(page.Id, new(["Validity tag"], [])))).Tags);
        var group = await fixture.Host.SeedGroupAsync(TestLogin.For("Expired writers"), fixture.Reader);
        await SeedGrant(fixture.Host, fixture.SpaceId, null, group, false);
        Assert.Empty((await fixture.As<SearchResponse>(fixture.Reader, new SearchQuery("Validity", null, 0, 20, new("drafts")))).Items);
        var library = Assert.Single((await fixture.As<PersonalLibraryList>(fixture.Reader, new ListPersonalLibrary("changes", 0, 20))).Items);
        Assert.Equal("Validity public title", library.Title);
        Assert.False(library.HasUnpublishedChanges);
        var tagged = Assert.Single((await fixture.As<SearchResponse>(fixture.Reader, new ListTaggedPages(tag.Id, null, null, 0, 20, new()))).Items);
        Assert.Equal("Validity public title", tagged.Title);
        Assert.False(tagged.IsDraft);
        var access = await fixture.As<PageAccessState>(fixture.Reader, new GetPageAccess(page.Id));
        Assert.Equal("Reader", access.MyAccess.Role);
        Assert.Equal("space", Assert.Single(access.MyAccess.Sources).Kind);
        Assert.False(access.MyAccess.CanEdit);
    }

    [PostgreSqlFact]
    public async Task Page_access_explanations_omit_ineffective_assignments_but_keep_people_searchable()
    {
        await using var fixture = await PersonalLibraryFixture.CreateAsync();
        var page = await fixture.Create("Validity published");
        await fixture.Pages.PublishAsync(page.Id, new(1), fixture.Writer, default);
        var subjects = await SeedIneffectiveSubjects(fixture.Host, fixture.SpaceId);
        var listed = await fixture.As<PageAccessState>(fixture.Admin, new GetPageAccess(page.Id));
        Assert.DoesNotContain(listed.Items, item => subjects.Any(subject => subject.Id == item.UserId));
        // The access list holds only people an effective or current page grant reaches; the directory of the add wizard and
        // "Zobrazit jako" still finds them, and the policy explains that nothing reaches them.
        Assert.Empty((await fixture.As<PageAccessState>(fixture.Admin, new GetPageAccess(page.Id, Query: "timed-"))).Items);
        foreach (var subject in subjects)
        {
            // By the whole login: other tests' "timed-" users share the shared directory, whose search is bounded.
            Assert.Single(await fixture.As<IReadOnlyList<AccessCandidate>>(fixture.Admin, new GetPageAccessCandidates(page.Id, "user", subject.Username)));
            var explained = await fixture.As<PageAccessExplanation>(fixture.Admin, new ExplainPageAccess(page.Id, subject.Id));
            Assert.Equal((false, (string?)null, "none"), (explained.CanView, explained.Role, explained.Denial));
            Assert.Empty(explained.Sources);
        }
    }

    [PostgreSqlFact]
    public async Task Import_quarantine_rejects_future_assignments_that_could_later_expose_imported_content()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_validity_import_");
        var user = await host.SeedUserAsync("scheduled-reader");
        var space = await host.SeedSpaceAsync("Quarantine");
        using (var empty = host.OpenSession()) await ImportGuards.RequireQuarantineAsync(empty, space, default);
        await SeedGrant(host, space, user.Id, null, true);
        using var session = host.OpenSession();
        var error = await Assert.ThrowsAsync<ApiException>(() => ImportGuards.RequireQuarantineAsync(session, space, default));
        Assert.Equal(409, error.Status);
    }

    [PostgreSqlFact]
    public async Task Read_projections_preserve_a_current_bounded_group_reader_grant()
    {
        await using var fixture = await PersonalLibraryFixture.CreateAsync();
        var page = await fixture.Create("Validity published");
        await fixture.Pages.PublishAsync(page.Id, new(1), fixture.Writer, default);
        var tag = Assert.Single((await fixture.As<PageTagState>(fixture.Writer, new SetPageTags(page.Id, new(["Validity tag"], [])))).Tags);
        var reader = await fixture.Host.SeedUserAsync("bounded-group-reader");
        var group = await fixture.Host.SeedGroupAsync(TestLogin.For("Current readers"), reader);
        await fixture.Host.SeedAsync(async session =>
        {
            await session.CreateSQLQuery("""
                INSERT INTO "SpaceGrant"("Id","SpaceId","GroupId","Role","StartAt","ExpiresAt")
                VALUES (:id,:space,:group,1,statement_timestamp()-interval '1 day',statement_timestamp()+interval '1 day')
                """).SetParameter("id", Guid.NewGuid()).SetParameter("space", fixture.SpaceId)
                .SetParameter("group", group).ExecuteUpdateAsync();
        });
        Assert.Single((await fixture.As<SearchResponse>(reader, new SearchQuery("Validity", null, 0, 20, new()))).Items);
        Assert.Single((await fixture.As<SearchResponse>(reader, new SearchQuery("History", null, 0, 20, new()))).Spaces);
        Assert.Single((await fixture.As<PersonalLibraryList>(reader, new ListPersonalLibrary("changes", 0, 20))).Items);
        Assert.Single((await fixture.As<TagSuggestions>(reader, new SuggestTags("Validity"))).Items);
        Assert.Single((await fixture.As<SearchResponse>(reader, new ListTaggedPages(tag.Id, null, null, 0, 20, new()))).Items);
        var access = await fixture.As<PageAccessState>(reader, new GetPageAccess(page.Id));
        Assert.Equal("Reader", access.MyAccess.Role);
        var source = Assert.Single(access.MyAccess.Sources);
        Assert.Equal(("space", (Guid?)group), (source.Kind, source.GroupId));
    }

    private static async Task<UserAccount[]> SeedIneffectiveSubjects(AtlasTestHost host, Guid space)
    {
        var subjects = new List<UserAccount>();
        foreach (var future in new[] { false, true })
        foreach (var viaGroup in new[] { false, true })
        {
            var user = await host.SeedUserAsync($"timed-{(future ? "future" : "expired")}-{(viaGroup ? "group" : "direct")}");
            var group = viaGroup ? await host.SeedGroupAsync(user.Username, user) : (Guid?)null;
            await SeedGrant(host, space, viaGroup ? null : user.Id, group, future);
            subjects.Add(user);
        }
        return subjects.ToArray();
    }

    private static Task SeedGrant(AtlasTestHost host, Guid space, Guid? user, Guid? group, bool future)
    {
        var now = DateTime.UtcNow;
        return host.SeedAsync(async session =>
        {
            await session.CreateSQLQuery("""
                INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","GroupId","Role","StartAt","ExpiresAt")
                VALUES (:id,:space,:user,:group,2,:start,:end)
                """).SetParameter("id", Guid.NewGuid()).SetParameter("space", space)
                .SetParameter("user", user, NHibernateUtil.Guid).SetParameter("group", group, NHibernateUtil.Guid)
                .SetParameter("start", now.AddDays(future ? 1 : -2), NHibernateUtil.UtcDateTime)
                .SetParameter("end", now.AddDays(future ? 2 : -1), NHibernateUtil.UtcDateTime)
                .ExecuteUpdateAsync();
        });
    }
}
