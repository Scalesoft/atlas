using Atlas.Server.Features.KnowledgeMaintenance.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Vaults;
using Xunit;

namespace Atlas.Server.Tests.KnowledgeMaintenance;

public sealed class ContentValidationTests
{
    private const string Broken = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"image","attrs":{"src":"/a.png","align":"center"}}]}}""";

    [PostgreSqlFact]
    public async Task Space_admin_sees_the_stored_revisions_the_editor_would_refuse_and_reading_them_still_works()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_content_validation_");
        var admin = await host.SeedUserAsync("admin");
        var writer = await host.SeedUserAsync("writer");
        var spaceId = await host.SeedSpaceAsync("Příručky", (admin, SpaceRole.Admin), (writer, SpaceRole.Writer));
        var pages = host.Pages();
        var valid = await pages.CreateAsync(new(spaceId, null, "Platná", SecretReferenceGuardTests.Rich(), ""), writer, default);
        await pages.PublishAsync(valid.Id, new(valid.Version), writer, default);
        var broken = await pages.CreateAsync(new(spaceId, null, "Rozbitá", SecretReferenceGuardTests.Rich(), ""), writer, default);
        await pages.PublishAsync(broken.Id, new(broken.Version), writer, default);
        await pages.SaveAsync(broken.Id, new("Rozbitá", SecretReferenceGuardTests.Rich("""{"type":"paragraph","content":[{"type":"text","text":"koncept"}]}"""), "koncept", broken.Version), writer, default);
        // Stored before writes were validated: only the working revision breaks the schema, the publication is intact.
        await host.SeedAsync(session => session.CreateSQLQuery("""UPDATE "PageRevision" SET "ContentJson"=:json WHERE "PageId"=:id AND "ActiveTo" IS NULL""")
            .SetParameter("json", Broken).SetParameter("id", broken.Id).ExecuteUpdateAsync());
        // An Atlas 2.x shape the editor repairs when it opens it (a link in a link) is not listed.
        await host.SeedAsync(session => session.CreateSQLQuery("""UPDATE "PageRevision" SET "ContentJson"=:json WHERE "PageId"=:id""")
            .SetParameter("json", """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"link","attrs":{"href":"https://a.example"}},{"type":"link","attrs":{"href":"https://b.example"}}]}]}]}}""")
            .SetParameter("id", valid.Id).ExecuteUpdateAsync());

        host.Actor.Current = admin;
        var report = await host.InvokeAsync<ContentValidationReport>(new GetContentValidation(spaceId));
        var issue = Assert.Single(report.Items);
        Assert.Equal((broken.Id, "Rozbitá", "working", "document.content[0].attrs.align"), (issue.PageId, issue.Title, issue.Revision, issue.Path));
        Assert.Equal(3, report.Checked);
        Assert.False(report.Truncated);
        // Reads never validate: the broken page still opens for its writer, so it can be repaired.
        host.Actor.Current = writer;
        Assert.Equal(broken.Id, (await host.InvokeAsync<PageDto>(new GetPage(broken.Id))).Id);
        // The report is for space administrators only.
        await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<ContentValidationReport>(new GetContentValidation(spaceId)));
    }

    [PostgreSqlFact]
    public async Task Another_users_private_draft_stays_hidden_from_a_space_admin_who_is_not_a_wiki_admin()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_content_validation_");
        var admin = await host.SeedUserAsync("admin");
        var writer = await host.SeedUserAsync("writer");
        var wikiAdmin = await host.SeedUserAsync("wikiadmin", isWikiAdmin: true);
        var spaceId = await host.SeedSpaceAsync("Příručky", (admin, SpaceRole.Admin), (writer, SpaceRole.Writer), (wikiAdmin, SpaceRole.Admin));
        var draft = await host.Pages().CreateAsync(new(spaceId, null, "Soukromý koncept", SecretReferenceGuardTests.Rich(), ""), writer, default);
        await host.SeedAsync(session => session.CreateSQLQuery("""UPDATE "PageRevision" SET "ContentJson"=:json WHERE "PageId"=:id AND "ActiveTo" IS NULL""")
            .SetParameter("json", Broken).SetParameter("id", draft.Id).ExecuteUpdateAsync());

        // Never published: only the owner and a wiki administrator see it, exactly as in PageVisibility.
        host.Actor.Current = admin;
        Assert.Empty((await host.InvokeAsync<ContentValidationReport>(new GetContentValidation(spaceId))).Items);
        host.Actor.Current = wikiAdmin;
        Assert.Equal(draft.Id, Assert.Single((await host.InvokeAsync<ContentValidationReport>(new GetContentValidation(spaceId))).Items).PageId);
    }
}
