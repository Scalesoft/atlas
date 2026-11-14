using System.Text.Json.Nodes;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>The rights of Projector's commands and reads beyond the page's own edit check (review cycle 1, A2–A7): what a copy may carry,
/// who may make a catalog, the openings the reads record, that a foreign page answers as a missing one, and the request bounds.</summary>
public sealed class ProjectorRightsTests
{
    private const string B1 = "00000000-0000-4000-8000-0000000000b1", C1 = "00000000-0000-4000-8000-0000000000c1", C2 = "00000000-0000-4000-8000-0000000000c2";

    [PostgreSqlFact]
    public async Task A_copy_into_another_space_or_of_an_own_access_page_needs_edit_rights_on_the_source_and_keeps_its_access()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var other = await f.Host.SeedSpaceAsync("Jiný prostor", (f.Reader, SpaceRole.Writer), (f.Writer, SpaceRole.Writer));
        var open = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL")));
        var confidential = await f.PageAsync("Důvěrné", Json(Block(B1, "portal.ssl", "SSL")));
        await f.OwnAccessAsync(confidential);
        await f.GrantAsync(confidential, f.Writer, 2);
        await f.GrantAsync(confidential, f.Reader, 1);

        // Row by row: who copies what where, and the answer.
        var readerAcross = await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Reader, new CreateDeploymentFromPage(open, other, null, "Kopie")));
        var readerOfConfidential = await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Reader, new CreateDeploymentFromPage(confidential, other, null, "Kopie")));
        var writerAcross = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(open, other, null, "Kopie"));
        var confidentialCopy = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(confidential, f.SpaceId, null, "Důvěrné — kopie"));

        Assert.Equal((403, 403), (readerAcross.Status, readerOfConfidential.Status));
        Assert.Equal(other, writerAcross.SpaceId);
        // The copy keeps the source's own access; an author who does not manage the source's access takes none of its grants, only
        // their own edit grant, so they still edit it and nobody else of the space reads it.
        Assert.Equal(1, await f.ScalarAsync("""SELECT CASE WHEN "OwnAccess" THEN 1 ELSE 0 END FROM "Page" WHERE "Id"=:id""", confidentialCopy.Id));
        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL AND "Role"=2""", confidentialCopy.Id));
        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", confidentialCopy.Id));
        Assert.True((await f.As<PageRequirements>(f.Writer, new GetPageRequirements(confidentialCopy.Id))).Working);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Pages.GetAsync(confidentialCopy.Id, f.Reader, default))).Status);
        // Reading the source for a copy is an opening of it, as any read of its content.
        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "PageAccessEvent" WHERE "PageId"=:id AND "Action"='page.opened'""", confidential));
    }

    [PostgreSqlFact]
    public async Task A_copy_carries_the_sources_grants_only_for_who_manages_the_access_of_both_and_never_into_another_space()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var other = await f.Host.SeedSpaceAsync("Jiný prostor", (f.Writer, SpaceRole.Writer));
        var confidential = await f.PageAsync("Důvěrné", Json(Block(B1, "portal.ssl", "SSL")));
        await f.OwnAccessAsync(confidential);
        await f.GrantAsync(confidential, f.Writer, 2);
        await f.GrantAsync(confidential, f.Outsider, 3);

        // review A2-1: an editor of the source copies it into a space of their own; the source's manager grant stays behind.
        var across = await f.As<PageDto>(f.Writer, new CreateDeploymentFromPage(confidential, other, null, "Venku"));

        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Pages.GetAsync(across.Id, f.Outsider, default))).Status);
        var under = await Assert.ThrowsAsync<ApiException>(() => f.Pages.CreateAsync(new CreatePageRequest(other, across.Id, "Pod kopií", Json(Paragraph("x")), null), f.Outsider, default));
        Assert.Equal(404, under.Status);
        Assert.Equal(0, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "Role"=3""", across.Id));
        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", across.Id));
        Assert.Equal(1, await f.ScalarAsync("""SELECT CASE WHEN "OwnAccess" THEN 1 ELSE 0 END FROM "Page" WHERE "Id"=:id""", across.Id));

        // Who manages the access of the source and of the copy in the same space gets the source's grants through the grant path.
        var managed = await f.As<PageDto>(f.Admin, new CreateDeploymentFromPage(confidential, f.SpaceId, null, "Uvnitř"));

        // Exactly the source's grants: the space defaults the own-access switch copies do not stay where the source has none.
        Assert.Equal(2, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", managed.Id));
        Assert.Equal(0, await f.ScalarAsync($"""
            SELECT count(*) FROM (SELECT "UserId","Role" FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL
              EXCEPT SELECT "UserId","Role" FROM "ResourceGrant" WHERE "PageId"='{managed.Id}' AND "ActiveTo" IS NULL) x
            """, confidential));
        Assert.Equal(1, await f.ScalarAsync("""SELECT CASE WHEN "OwnAccess" THEN 1 ELSE 0 END FROM "Page" WHERE "Id"=:id""", managed.Id));
    }

    /// <summary>Review A3-1: an editor whose edit right ends keeps the copy no longer than that, whether the right comes from the parent's
    /// grant in the same space or from a time-limited space grant in another space.</summary>
    [PostgreSqlFact]
    public async Task A_temporary_editors_own_grant_on_a_copy_ends_when_their_edit_right_does()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var temporary = await f.Host.SeedUserAsync("temporary");
        var confidential = await f.PageAsync("Důvěrné", Json(Block(B1, "portal.ssl", "SSL")));
        await f.OwnAccessAsync(confidential);
        await f.GrantAsync(confidential, temporary, 2);
        var parent = await f.PageAsync("Nasazení týmu", Json(Paragraph("x")));
        await f.OwnAccessAsync(parent);
        await f.Host.ExecuteAsync($"""
            INSERT INTO "ResourceGrant"("Id","PageId","UserId","Role","ExpiresAt") VALUES (gen_random_uuid(),:id,'{temporary.Id}',2,now()+interval '1 day')
            """, parent);
        var other = await f.Host.SeedSpaceAsync("Jiný prostor");
        await f.Host.ExecuteAsync($"""
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ExpiresAt") VALUES (gen_random_uuid(),:id,'{temporary.Id}',2,now()+interval '2 days')
            """, other);

        var child = await f.As<PageDto>(temporary, new CreateDeploymentFromPage(confidential, f.SpaceId, parent, "Pod rodičem"));
        var across = await f.As<PageDto>(temporary, new CreateDeploymentFromPage(confidential, other, null, "Venku"));

        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", child.Id));
        Assert.Equal(1, await f.ScalarAsync($"""
            SELECT count(*) FROM "ResourceGrant" g JOIN "ResourceGrant" p ON p."PageId"='{parent}' AND p."UserId"=g."UserId" AND p."ActiveTo" IS NULL
            WHERE g."PageId"=:id AND g."ActiveTo" IS NULL AND g."Role"=2 AND g."ExpiresAt"=p."ExpiresAt"
            """, child.Id));
        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", across.Id));
        Assert.Equal(1, await f.ScalarAsync("""
            SELECT count(*) FROM "ResourceGrant" g JOIN "SpaceGrant" s ON s."UserId"=g."UserId" AND s."ActiveTo" IS NULL
            WHERE g."PageId"=:id AND g."ActiveTo" IS NULL AND g."Role"=2 AND g."ExpiresAt"=s."ExpiresAt"
            """, across.Id));
    }

    /// <summary>Review A3-3: who manages the access only until a date copies every grant of the source up to that date, and is told so.</summary>
    [PostgreSqlFact]
    public async Task A_temporary_managers_copy_carries_the_grants_until_their_authority_ends_and_says_so()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var confidential = await f.PageAsync("Důvěrné", Json(Block(B1, "portal.ssl", "SSL")));
        await f.OwnAccessAsync(confidential);
        await f.GrantAsync(confidential, f.Reader, 1);
        var temporary = await f.Host.SeedUserAsync("temporary");
        await f.Host.ExecuteAsync($"""
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ExpiresAt") VALUES (gen_random_uuid(),:id,'{temporary.Id}',3,now()+interval '1 day')
            """, f.SpaceId);

        var copy = await f.As<PageDto>(temporary, new CreateDeploymentFromPage(confidential, f.SpaceId, null, "Kopie"));

        Assert.Equal(1, await f.ScalarAsync($"""
            SELECT count(*) FROM "ResourceGrant" g JOIN "SpaceGrant" s ON s."UserId"<>g."UserId" AND s."ActiveTo" IS NULL AND s."Role"=3 AND s."SpaceId"='{f.SpaceId}'
            WHERE g."PageId"=:id AND g."ActiveTo" IS NULL AND g."Role"=1 AND g."ExpiresAt"=s."ExpiresAt"
            """, copy.Id));
        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "ResourceGrant" WHERE "PageId"=:id AND "ActiveTo" IS NULL""", copy.Id));
        Assert.Contains("reader", Assert.Single(copy.Warnings!));
    }

    [PostgreSqlFact]
    public async Task Only_a_writer_of_the_space_marks_a_catalog_and_the_catalog_marked_first_keeps_its_keys()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var trusted = await f.PageAsync("Ž — katalog týmu", Json(Block(C1, "portal.ssl", "SSL od týmu")));
        var guestPage = await f.PageAsync("A — stránka hosta", Json(Block(C2, "portal.ssl", "SSL od hosta")));
        var guest = await f.Host.SeedUserAsync("guest");
        await f.GrantAsync(guestPage, guest, 2);
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(trusted, true));

        // A page writer without a role in the space edits the page but does not decide the space's catalog.
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<CatalogPageState>(guest, new SetCatalogPage(guestPage, true)));
        Assert.Equal(403, refused.Status);
        // A later catalog page with the same key does not shadow the one marked first, whatever its title.
        await f.As<CatalogPageState>(f.Admin, new SetCatalogPage(guestPage, true));
        var catalog = await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId));
        var resolution = await f.As<RequirementResolution>(f.Reader, new ResolveProjectorRequirements(f.SpaceId, ["portal.ssl"], []));

        Assert.Equal(("SSL od týmu", trusted), (Assert.Single(catalog.Definitions).Title, catalog.Definitions[0].PageId));
        Assert.Equal(["Klíč „portal.ssl“ je v katalogu víckrát; platí první výskyt („SSL od týmu“)."], catalog.Warnings);
        Assert.Equal(trusted, Assert.Single(resolution.Items).Definition.PageId);
        Assert.True((await f.Pages.GetAsync(trusted, f.Reader, default)).IsCatalog);
        // Every mark and unmark is audited with its actor.
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(trusted, false));
        Assert.False((await f.Pages.GetAsync(trusted, f.Reader, default)).IsCatalog);
        Assert.Equal(2, await f.ScalarAsync("""SELECT count(*) FROM "ProjectorCatalogEvent" WHERE "PageId"=:id""", trusted));
        Assert.Equal(0, await f.ScalarAsync("""SELECT count(*) FROM "ProjectorCatalogEvent" WHERE "PageId"=:id AND "ActorId" IS NULL""", trusted));
        // Review A2-4: the page's access history shows the marks to who manages its access, and the trail outlives nothing it
        // describes (a page with marks cannot be deleted under them, as with its access events).
        var history = await f.As<PageAccessHistory>(f.Admin, new GetPageAccessHistory(trusted));
        Assert.Equal([false, true], history.CatalogMarks!.Select(x => x.IsCatalog));
        Assert.Equal((f.Writer.Id, "web"), (history.CatalogMarks![0].ActorId, history.CatalogMarks[0].Channel));
        Assert.Null((await f.As<PageAccessHistory>(f.Admin, new GetPageAccessHistory(guestPage, BeforeId: long.MaxValue))).CatalogMarks);
        Assert.Equal(0, await f.ScalarAsync("""
            SELECT count(*) FROM pg_constraint WHERE conrelid=CAST('"ProjectorCatalogEvent"' AS regclass) AND contype='f' AND confdeltype='c' AND :id IS NOT NULL
            """, trusted));
    }

    [PostgreSqlFact]
    public async Task Reads_over_the_api_and_mcp_record_an_opening_of_every_page_they_return()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var kbn = await f.PageAsync("KBN", Json(Block(B1, "portal.sms", "SMS", state: "waiting_for_input", party: "IT KBN")));
        var catalog = await f.PageAsync("Katalog", Json(Block(C1, "portal.ssl", "SSL")));
        await f.As<CatalogPageState>(f.Writer, new SetCatalogPage(catalog, true));
        async Task ReadAllAsync()
        {
            await f.As<PageRequirements>(f.Reader, new GetPageRequirements(kbn));
            await f.As<RequirementOverview>(f.Reader, new GetProjectorOverview(f.SpaceId, "all"));
            await f.As<WaitingList>(f.Reader, new GetProjectorWaiting(f.SpaceId));
            await f.As<ProjectorCatalog>(f.Reader, new GetProjectorCatalog(f.SpaceId));
        }

        // The web's screens refresh often and record nothing; a token's reads are what the page's audit has to show.
        await ReadAllAsync();
        Assert.Equal(0, await f.ScalarAsync("""SELECT count(*) FROM "PageAccessEvent" WHERE "ActorId"=:id""", f.Reader.Id));
        var token = await TestApiTokens.SeedAsync(f.Host, f.Reader, "Agent");
        f.Host.Channel.Current = new RequestChannel(ContentChannel.Mcp, token);
        await ReadAllAsync();

        Assert.Equal(3, await f.ScalarAsync("""SELECT count(*) FROM "PageAccessEvent" WHERE "PageId"=:id AND "Channel"='mcp' AND "ApiTokenId" IS NOT NULL""", kbn));
        // The catalog page is read by the catalog only: it is no deployment of the overview (U2-5).
        Assert.Equal(1, await f.ScalarAsync("""SELECT count(*) FROM "PageAccessEvent" WHERE "PageId"=:id AND "Channel"='mcp'""", catalog));
        // The revision the reader was given: the publication.
        Assert.Equal(0, await f.ScalarAsync("""
            SELECT count(*) FROM "PageAccessEvent" e JOIN "Page" p ON p."Id"=e."PageId" WHERE e."PageId"=:id AND e."RevisionId"<>p."PublishedRevisionId"
            """, kbn));
    }

    [PostgreSqlFact]
    public async Task An_insertion_into_a_page_of_a_foreign_space_answers_as_into_a_missing_page()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL")));

        var foreign = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Outsider, new InsertRequirements(page, ["portal.ssl"], new RequirementPosition(), 1)));
        var missing = await Assert.ThrowsAsync<ApiException>(() => f.As<RequirementInsertResult>(f.Outsider, new InsertRequirements(Guid.NewGuid(), ["portal.ssl"], new RequirementPosition(), 1)));

        Assert.Equal((404, missing.Message, missing.Code), (foreign.Status, foreign.Message, foreign.Code));
    }

    [PostgreSqlFact]
    public async Task Requests_without_their_lists_or_with_too_many_or_malformed_keys_are_400_invalid_argument()
    {
        await using var f = await ProjectorFixture.CreateAsync();
        var page = await f.PageAsync("KBN", Json(Block(B1, "portal.ssl", "SSL")));
        var version = (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version;
        var many = Enumerable.Range(0, 101).Select(i => $"k{i}").ToArray();

        var refusals = new Func<Task>[]
        {
            () => f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, null!, new RequirementPosition(), version)),
            () => f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, many, new RequirementPosition(), version)),
            () => f.As<RequirementInsertResult>(f.Writer, new InsertRequirements(page, ["portal.ssl"], null!, version)),
            () => f.As<RequirementWriteResult>(f.Writer, new UpdateRequirement(page, B1, null!, version)),
            () => f.As<RequirementWriteResult>(f.Writer, new UpdateRequirement(page, B1, new RequirementChange(Requires: [null!]), version)),
            () => f.As<RequirementWriteResult>(f.Writer, new MoveRequirement(page, B1, null!, version)),
            () => f.As<RequirementWriteResult>(f.Writer, new UpdateRequirements(page, null!, version)),
            () => f.As<RequirementResolution>(f.Writer, new ResolveProjectorRequirements(f.SpaceId, null!, [])),
            () => f.As<RequirementResolution>(f.Writer, new ResolveProjectorRequirements(f.SpaceId, many, [])),
            () => f.As<RequirementResolution>(f.Writer, new ResolveProjectorRequirements(f.SpaceId, ["Not A Key"], [])),
            () => f.As<RequirementResolution>(f.Writer, new ResolveProjectorRequirements(f.SpaceId, ["portal.ssl"], [null!])),
        };
        foreach (var refusal in refusals)
        {
            var error = await Assert.ThrowsAsync<ApiException>(refusal);
            Assert.Equal((400, "invalid_argument"), (error.Status, error.Code));
        }
        // Present keys are optional: the web's catalog panel on a page without blocks sends none.
        Assert.Empty((await f.As<RequirementResolution>(f.Writer, new ResolveProjectorRequirements(f.SpaceId, ["portal.ssl"], null!))).Items);
        Assert.Equal(version, (await f.As<PageRequirements>(f.Writer, new GetPageRequirements(page))).Version);
    }
}
