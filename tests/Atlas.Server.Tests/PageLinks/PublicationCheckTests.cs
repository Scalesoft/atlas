using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Xunit;
using static Atlas.Server.Tests.PageLinks.PageLinksFixture;

namespace Atlas.Server.Tests.PageLinks;

public sealed class PublicationCheckTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Check_reports_unavailable_and_unpublished_targets_without_naming_hidden_pages()
    {
        await using var f = await PageLinksFixture.CreateAsync();
        var visible = await f.CreatePublished("Visible");
        var trashed = await f.CreatePublished("Trashed secret");
        await f.Trash(trashed);
        var hidden = await f.CreatePublished("Hidden secret", spaceId: f.ForeignSpaceId, owner: f.Admin);
        var othersDraft = await f.Create("Colleague's draft", owner: f.OtherWriter);
        var ownDraft = await f.Create("Own draft");
        var missing = Guid.NewGuid();
        var page = await f.Create("Checked");
        // What Publish would publish: the working revision, links to itself ignored.
        page = await f.Save(page, "Checked", Doc(Ref(visible.Id), Ref(trashed.Id), Link($"#/pages/{hidden.Id}"), Ref(othersDraft.Id),
            Ref(missing), Link($"/#/pages/{ownDraft.Id}?heading=a"), Ref(page.Id)));

        var check = await f.As<PublicationCheck>(f.Writer, new CheckPublication(page.Id, page.Version));

        Assert.Equal(page.Version, check.Version);
        Assert.Equal(new[] { trashed.Id, hidden.Id, othersDraft.Id, missing }.Order().Select(id => new LinkFinding(id, "unavailable", null))
            .Append(new LinkFinding(ownDraft.Id, "unpublished", "Own draft")), check.Links);
    }

    [PostgreSqlFact]
    public async Task Check_requires_edit_rights_and_the_current_version()
    {
        await using var f = await PageLinksFixture.CreateAsync();
        var page = await f.CreatePublished("Published", Doc(Ref(Guid.NewGuid())));
        // The reader reads the published page, so it is told the missing right (review S3).
        await Status(403, () => f.As<PublicationCheck>(f.Reader, new CheckPublication(page.Id, page.Version)));
        await Status(409, () => f.As<PublicationCheck>(f.Writer, new CheckPublication(page.Id, page.Version - 1)));
        Assert.Single((await f.As<PublicationCheck>(f.OtherWriter, new CheckPublication(page.Id, page.Version))).Links);
    }

    [PostgreSqlFact]
    public async Task References_return_only_visible_pages_and_record_no_openings()
    {
        await using var f = await PageLinksFixture.CreateAsync();
        var published = await f.CreatePublished("Published title");
        await f.Save(published, "Working title", Doc(Text("draft")));
        var ownDraft = await f.Create("Own draft");
        var othersDraft = await f.Create("Colleague's draft", owner: f.OtherWriter);
        var trashed = await f.CreatePublished("Trashed");
        await f.Trash(trashed);
        var hidden = await f.CreatePublished("Hidden", spaceId: f.ForeignSpaceId, owner: f.Admin);
        Guid[] ids = [published.Id, ownDraft.Id, othersDraft.Id, trashed.Id, hidden.Id, Guid.NewGuid(), published.Id];
        var openings = await Openings(f);

        var writer = await f.As<PageReferenceList>(f.Writer, new ResolvePageReferences(ids));
        var reader = await f.As<PageReferenceList>(f.Reader, new ResolvePageReferences(ids));

        // The title the actor would open: the working one for an editor, the published one for a reader.
        Assert.Equal([new PageReference(published.Id, "Working title"), new PageReference(ownDraft.Id, "Own draft")], writer.Items);
        Assert.Equal([new PageReference(published.Id, "Published title")], reader.Items);
        Assert.Equal(openings, await Openings(f));
        await f.Pages.GetAsync(published.Id, f.Reader, default);
        Assert.Equal(openings + 1, await Openings(f));
        Assert.Empty((await f.As<PageReferenceList>(f.Writer, new ResolvePageReferences([]))).Items);
        await Status(400, () => f.As<PageReferenceList>(f.Writer, new ResolvePageReferences(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray())));
        await Status(400, () => f.As<PageReferenceList>(f.Writer, new ResolvePageReferences(null!)));
    }

    private static async Task<long> Openings(PageLinksFixture f)
    {
        using var session = f.Host.OpenSession();
        return await session.CreateSQLQuery("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\" IN (SELECT \"Id\" FROM \"Page\" WHERE \"SpaceId\" IN (:a, :b))")
            .SetParameter("a", f.SpaceId).SetParameter("b", f.ForeignSpaceId).UniqueResultAsync<long>();
    }

    private static async Task Status(int status, Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<ApiException>(call);
        Assert.Equal(status, error.Status);
    }
}
