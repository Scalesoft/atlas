using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using Atlas.Server.Features.ExternalSharing.Queries;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class PageAccessEventTests
{
    [PostgreSqlFact]
    public async Task Automatic_revocation_is_audited_once_and_transaction_rollback_keeps_the_link_live()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        using (var session = fixture.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var closed = await session.CreateSQLQuery("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL RETURNING \"Id\"")
                .SetParameter("id", fixture.Admin.Id).ListAsync<Guid>();
            await AccessAuditLog.ClosedAsync(session, AccessAuditLog.SpaceGrant, [.. closed], null, CancellationToken.None);
            Assert.Equal(1, await session.CreateSQLQuery("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"Action\"='share.revoked' AND \"ShareLinkId\"=:link").SetParameter("link", issued.Link.Id).UniqueResultAsync<long>());
            await transaction.RollbackAsync();
        }
        using (var session = fixture.Host.OpenSession())
        {
            Assert.Null((await session.GetAsync<PageShareLink>(issued.Link.Id)).ActiveTo);
            Assert.Equal(0, await session.CreateSQLQuery("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"Action\"='share.revoked' AND \"ShareLinkId\"=:link").SetParameter("link", issued.Link.Id).UniqueResultAsync<long>());
        }
        await fixture.Host.CloseAccessAsync(AccessAuditLog.SpaceGrant, "\"UserId\"=:id", fixture.Admin.Id);
        using var read = fixture.Host.OpenSession();
        Assert.NotNull((await read.GetAsync<PageShareLink>(issued.Link.Id)).ActiveTo);
        Assert.Equal(1, await read.CreateSQLQuery("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"Action\"='share.revoked' AND \"ShareLinkId\"=:link").SetParameter("link", issued.Link.Id).UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Audit_failure_blocks_anonymous_content_and_rolls_back_link_creation_and_revocation()
    {
        await using var fixture = await SharingFixture.CreateAsync(isolated: true);
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        await fixture.Host.SeedAsync(async session =>
            await session.CreateSQLQuery("""
                CREATE FUNCTION test_block_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Blocked audit'; END $$;
                CREATE TRIGGER "TestBlockAudit" BEFORE INSERT ON "PageAccessEvent" FOR EACH ROW EXECUTE FUNCTION test_block_audit()
                """).ExecuteUpdateAsync());
        fixture.Host.Actor.Current = null;
        await Assert.ThrowsAnyAsync<NHibernate.HibernateException>(() => fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret))));
        fixture.Host.Actor.Current = fixture.Admin;
        await Assert.ThrowsAnyAsync<NHibernate.HibernateException>(() => fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create("Blocked")));
        await Assert.ThrowsAnyAsync<NHibernate.HibernateException>(() => fixture.Host.InvokeAsync(new RevokeShareLink(fixture.PageId, issued.Link.Id)));
        using var read = fixture.Host.OpenSession();
        Assert.Equal(1, await read.QueryOver<PageShareLink>().RowCountAsync());
        Assert.Null((await read.GetAsync<PageShareLink>(issued.Link.Id)).ActiveTo);
        Assert.Equal(1, await read.CreateSQLQuery("SELECT count(*) FROM \"PageAccessEvent\"").UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task HTTP_history_requires_authentication_and_serializes_safe_string_cursors()
    {
        await using var fixture = await SharingSite.CreateAsync();
        var path = fixture.PagePath + "/access-history";
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await fixture.Anonymous.GetAsync(path)).StatusCode);
        await fixture.Admin.GetAsync(fixture.PagePath);
        await fixture.Admin.GetAsync(fixture.PagePath);
        var result = await Platform.AtlasHttpSite.Json(await fixture.Admin.GetAsync(path + "?limit=1"), System.Net.HttpStatusCode.OK);
        Assert.Single(result["items"]!.AsArray());
        Assert.NotNull(result["items"]![0]!["id"]!.GetValue<string>());
        Assert.NotNull(result["nextBeforeId"]!.GetValue<string>());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await fixture.Admin.GetAsync(path + "?beforeId=invalid")).StatusCode);
        Assert.DoesNotContain("contentJson", result.ToJsonString());
    }

    [PostgreSqlFact]
    public async Task Managers_see_last_openings_and_cursor_history_including_revoked_access()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        fixture.Host.Actor.Current = fixture.Reader;
        await fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId));
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        Assert.Null(issued.Link.LastOpenedAt);
        fixture.Host.Actor.Current = null;
        await fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret)));
        fixture.Host.Actor.Current = fixture.Admin;
        // Looked up by login: the shared database holds more WikiAdmins than one page of the list.
        var readerRow = (await fixture.Host.InvokeAsync<PageAccessState>(new GetPageAccess(fixture.PageId, 0, 50, fixture.Reader.Username))).Items;
        Assert.NotNull(Assert.Single(readerRow, p => p.UserId == fixture.Reader.Id).LastViewedAt);
        var adminRow = (await fixture.Host.InvokeAsync<PageAccessState>(new GetPageAccess(fixture.PageId, 0, 50, fixture.Admin.Username))).Items;
        Assert.Null(Assert.Single(adminRow, p => p.UserId == fixture.Admin.Id).LastViewedAt);
        var links = await fixture.Host.InvokeAsync<ShareLinksDto>(new ListShareLinks(fixture.SpaceId, fixture.PageId));
        Assert.NotNull(Assert.Single(links.Items).LastOpenedAt);
        await fixture.Host.ExecuteAsync("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", fixture.Reader.Id);
        var first = await fixture.Host.InvokeAsync<PageAccessHistory>(new GetPageAccessHistory(fixture.PageId, null, 2));
        Assert.Equal(new[] { "share.opened", "share.created" }, first.Items.Select(e => e.Action));
        Assert.NotNull(first.NextBeforeId);
        var last = await fixture.Host.InvokeAsync<PageAccessHistory>(new GetPageAccessHistory(fixture.PageId, long.Parse(first.NextBeforeId!), 2));
        Assert.Equal(fixture.Reader.Id, Assert.Single(last.Items).ActorId);
        Assert.Null(last.NextBeforeId);
        Assert.Null(first.Items[0].ActorId);
        Assert.Equal(issued.Link.Id, first.Items[0].ShareLinkId);
        Assert.Equal("Review", first.Items[0].ShareLinkName);
    }

    [PostgreSqlFact]
    public async Task History_is_admin_only_page_scoped_and_validates_pagination()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        foreach (var actor in new[] { fixture.Reader, fixture.Writer, fixture.Outsider })
        {
            fixture.Host.Actor.Current = actor;
            var error = await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<PageAccessHistory>(new GetPageAccessHistory(fixture.PageId)));
            Assert.Contains(error.Status, new[] { 403, 404 });
        }
        fixture.Host.Actor.Current = fixture.Admin;
        foreach (var query in new[] { new GetPageAccessHistory(fixture.PageId, -1), new GetPageAccessHistory(fixture.PageId, null, 0), new GetPageAccessHistory(fixture.PageId, null, 101) })
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<PageAccessHistory>(query))).Status);
        var other = await fixture.Host.InvokeAsync<PageDto>(new Features.Pages.Commands.CreatePage(new(fixture.SpaceId, null, "Other", SharingSite.Rich("Other"), "Other")));
        await fixture.Host.InvokeAsync<PageDto>(new GetPage(other.Id));
        Assert.Empty((await fixture.Host.InvokeAsync<PageAccessHistory>(new GetPageAccessHistory(fixture.PageId))).Items);
    }

    [PostgreSqlFact]
    public async Task Every_authorized_read_is_recorded_but_denied_reads_are_not()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        fixture.Host.Actor.Current = fixture.Reader;
        await fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId));
        await fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId));
        fixture.Host.Actor.Current = fixture.Outsider;
        await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId)));
        using var read = fixture.Host.OpenSession();
        var rows = await read.CreateSQLQuery("""
            SELECT e."ActorId",e."RevisionId",e."ShareLinkId" FROM "PageAccessEvent" e
            JOIN "Page" p ON p."Id"=e."PageId" AND p."PublishedRevisionId"=e."RevisionId"
            WHERE e."Action"='page.opened' AND e."PageId"=:page
            """).SetParameter("page", fixture.PageId).ListAsync<object[]>();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Equal(fixture.Reader.Id, row[0]); Assert.Null(row[2]); });
    }

    [PostgreSqlFact]
    public async Task Link_lifecycle_and_anonymous_reads_are_attributed_without_tokens()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        fixture.Host.Actor.Current = null;
        await fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret)));
        fixture.Host.Actor.Current = fixture.Admin;
        await fixture.Host.InvokeAsync(new RevokeShareLink(fixture.PageId, issued.Link.Id));
        await fixture.Host.InvokeAsync(new RevokeShareLink(fixture.PageId, issued.Link.Id));
        await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret))));
        using var read = fixture.Host.OpenSession();
        var rows = await read.CreateSQLQuery("SELECT \"Action\",\"ActorId\",\"ShareLinkId\" FROM \"PageAccessEvent\" WHERE \"ShareLinkId\"=:link ORDER BY \"Id\"").SetParameter("link", issued.Link.Id).ListAsync<object[]>();
        Assert.Equal(new[] { "share.created", "share.opened", "share.revoked" }, rows.Select(row => row[0]));
        Assert.Equal(fixture.Admin.Id, rows[0][1]);
        Assert.Null(rows[1][1]);
        Assert.Equal(fixture.Admin.Id, rows[2][1]);
        Assert.All(rows, row => Assert.Equal(issued.Link.Id, row[2]));
        var json = await read.CreateSQLQuery("SELECT CAST(json_agg(e) AS text) FROM \"PageAccessEvent\" e").UniqueResultAsync<string>();
        Assert.DoesNotContain(issued.Secret, json);
        Assert.DoesNotContain(ShareSecret.Hash(issued.Secret), json);
    }

    [PostgreSqlFact]
    public async Task Events_are_immutable_and_a_failed_audit_prevents_content_delivery()
    {
        await using var fixture = await SharingFixture.CreateAsync(isolated: true);
        fixture.Host.Actor.Current = fixture.Reader;
        await fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId));
        // The audit is append-only for the application (IntegrityRules; native SQL: IntegrityRulesConventionTests).
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.SeedAsync(async session =>
        {
            var opened = await session.QueryOver<Atlas.Server.Features.PageSource.Domain.PageSourceEditEvent>().Where(x => x.PageId == fixture.PageId)
                .Take(1).SingleOrDefaultAsync();
            await session.DeleteAsync(opened);
            await session.FlushAsync();
        }));
        Assert.Equal(IntegrityRules.AuditAppendOnly, refused.Message);
        await fixture.Host.SeedAsync(async session =>
        {
            await session.CreateSQLQuery("""
                CREATE FUNCTION test_block_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Blocked audit'; END $$;
                CREATE TRIGGER "TestBlockAudit" BEFORE INSERT ON "PageAccessEvent" FOR EACH ROW EXECUTE FUNCTION test_block_audit()
                """).ExecuteUpdateAsync();
        });
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId)));
    }
}
