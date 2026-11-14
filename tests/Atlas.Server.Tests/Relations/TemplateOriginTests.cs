using Atlas.Server.Features.Contacts.LinkRun.Queries;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Relations;

/// <summary>Template origin (plan 3.1 §2.1, U1): a page records the template it was created from, and the template's „Použito na“ lists
/// the pages made from it (relation template). Migrations 2026103180 and 2026103185 over the rows an installation already has.</summary>
public sealed class TemplateOriginTests
{
    [PostgreSqlFact]
    public async Task A_page_created_from_a_template_is_its_use_and_Obsah_still_lists_no_template()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var draft = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Zápis z porady", RelationsFixture.Rich(), null, IsTemplate: true), f.Admin, default);
        var template = await f.Pages.PublishAsync(draft.Id, new PublishPageRequest(draft.Version), f.Admin, default);
        var made = await f.Pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Porada 1", RelationsFixture.Rich(), null, TemplatePageId: template.Id), f.Writer, default);
        var plain = await f.PageAsync("Bez šablony", publish: false);
        Assert.Equal(made.Id.ToString(), await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            $"""SELECT string_agg(CAST("Id" AS text), ',') FROM "Page" WHERE "TemplatePageId"='{template.Id}'"""));
        Assert.Equal(1L, await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT count(*) FROM "Page" WHERE "Id"='{plain.Id}' AND "TemplatePageId" IS NULL"""));

        // The template's usage is the page made from it, for a reader of the space too (an unpublished draft of another is hidden, counted).
        var usage = await f.As<ObjectUsageList>(f.Writer, new GetObjectUsage(template.Id));
        Assert.Equal((made.Id, RelationKinds.Template, 1), (Assert.Single(usage.Items).PlaceId, usage.Items[0].Kind, usage.Total));
        Assert.Equal(new UsageCount(template.Id, 1, 0, 1), await f.As<UsageCount>(f.Reader, new CountObjectUsage(template.Id)));
        // The page's relations name its template; Obsah lists the pages, never the template, and the page's own count is unchanged.
        Assert.Contains((await f.RelationsAsync(f.Writer, made.Id)).Edges, edge => edge.Kind == RelationKinds.Template && edge.TargetId == template.Id);
        var contents = await f.ContentsAsync(f.Writer);
        Assert.DoesNotContain(contents.Items, row => row.Id == template.Id);
        Assert.Equal(0, contents.Items.Single(row => row.Id == made.Id).UsedCount);

        // A page made from it in the trash is no use.
        await f.TrashPageAsync(made, f.Writer);
        Assert.Equal(0, (await f.As<UsageCount>(f.Writer, new CountObjectUsage(template.Id))).Total);
    }

    [PostgreSqlFact]
    public async Task After_migrations_2026103180_to_2026103190_existing_pages_keep_no_origin_no_link_run_exists_and_merges_are_indexed()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_template_origin_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026103180);
        const string Owner = "00000000-0000-0000-0000-000000000001";
        const string Template = "00000000-0000-0000-0000-0000000000a1";
        const string Page = "00000000-0000-0000-0000-0000000000a2";
        await OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',true,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","IsTemplate") VALUES('{Template}','00000000-0000-0000-0000-00000000000a','{Owner}',1,true),
              ('{Page}','00000000-0000-0000-0000-00000000000a','{Owner}',1,false);
            INSERT INTO "ContentResource"("Id","TypeId","Scope","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data","TrashedAt") VALUES
              ('00000000-0000-0000-0000-0000000000c1','atlas.contact','wiki',1,now(),'{Owner}',now(),'Jana','{"{"}"name":"Jana"{"}"}',NULL),
              ('00000000-0000-0000-0000-0000000000c2','atlas.contact','wiki',1,now(),'{Owner}',now(),'J.','{"{"}"name":"J.","mergedInto":"00000000-0000-0000-0000-0000000000c1"{"}"}',now());
            """);
        await using var host = await AtlasTestHost.CreateAsync(schema, modules: TestModules.AllEnabled);
        // 3.0 recorded no origin: the existing pages keep none, nothing is guessed from their content.
        Assert.Equal(2L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM "Page" WHERE "TemplatePageId" IS NULL"""));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM "ContactLinkRun" """));
        // The contact merged before is the one row of the merge index.
        Assert.Equal("00000000-0000-0000-0000-0000000000c1", await OutboxTests.ScalarAsync(host.ConnectionString, """
            SELECT string_agg("Data"->>'mergedInto', ',') FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "Data"->>'mergedInto' IS NOT NULL
            """));
        Assert.Equal(1L, await OutboxTests.ScalarAsync(host.ConnectionString, """SELECT count(*) FROM pg_indexes WHERE indexname='IX_ContentResource_MergedInto'"""));
        var admin = await host.SeedUserAsync("admin", isWikiAdmin: true);
        host.Actor.Current = admin;
        Assert.Null(await host.InvokeAsync<Atlas.Server.Features.Contacts.LinkRun.ContactLinkRunView?>(new GetContactLinkRun()));
    }
}
