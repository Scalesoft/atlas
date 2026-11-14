using Xunit;
using static Atlas.Server.Tests.PageLinks.PageLinksFixture;

namespace Atlas.Server.Tests.PageLinks;

public sealed class PageLinkProjectionTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Publishing_projects_page_references_and_internal_links_of_the_published_revision()
    {
        await using var f = await PageLinksFixture.CreateAsync();
        var b = await f.Create("B");
        // Targets are not validated: C, D and E need not exist (an import may create them later).
        Guid c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid(), unlinked = Guid.NewGuid();
        var source = await f.Create("Source");
        var rich = Doc(Ref(b.Id), Link($"#/pages/{c}?heading=x"), Link($"/#/pages/{d}"),
            Link($"https://atlas.example/#/pages/{e.ToString().ToUpperInvariant()}"), Ref(source.Id), Link($"#/pages/{source.Id}"),
            Link("https://example.com/pages/x"), Text($"#/pages/{unlinked}"), Link($"#/pages/{unlinked}x"));
        var saved = await f.Save(source, "Source", rich);
        Assert.Empty(await f.Targets(source.Id));

        await f.Publish(saved);

        Assert.Equal(new[] { b.Id, c, d, e }.Order(), await f.Targets(source.Id));
        using var session = f.Host.OpenSession();
        var revisions = await session.CreateSQLQuery("""
            SELECT DISTINCT l."SourceRevisionId"=p."PublishedRevisionId" FROM "PageLink" l JOIN "Page" p ON p."Id"=l."SourcePageId"
            WHERE l."SourcePageId"=:id
            """).SetParameter("id", source.Id).ListAsync<bool>();
        Assert.True(Assert.Single(revisions));
    }

    [PostgreSqlFact]
    public async Task Saved_drafts_change_links_only_when_published()
    {
        await using var f = await PageLinksFixture.CreateAsync();
        Guid b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
        var source = await f.CreatePublished("Source", Doc(Ref(b)));
        Assert.Equal([b], await f.Targets(source.Id));

        var draft = await f.Save(source, "Source", Doc(Ref(c)));
        Assert.Equal([b], await f.Targets(source.Id));
        var published = await f.Publish(draft);
        Assert.Equal([c], await f.Targets(source.Id));

        // A space draft approved by the WikiAdmin publishes through the same pointer.
        var proposed = await f.Save(published, "Source", Doc(Link($"#/pages/{d}")), f.OtherWriter);
        Assert.Equal([c], await f.Targets(source.Id));
        await f.Approve(proposed);
        Assert.Equal([d], await f.Targets(source.Id));
    }
}
