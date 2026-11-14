using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Tests.ExternalSharing;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// The audit of page grants, the history of a page's access mode and the share events of every path that ends a link's authority,
/// pinned while the database triggers of external sharing and item access wrote them. C# writes them since 3.6.
/// </summary>
public sealed class RightsAuditPinTests
{
    [PostgreSqlFact]
    public async Task Page_grants_access_mode_and_share_revocation_audit_as_the_triggers_did()
    {
        await using var f = await SharingFixture.CreateAsync(services => services.AddAdministration());
        await f.Enable();
        var host = f.Host;
        var mark = await AuditPin.MarkAsync(host.ConnectionString);

        // Explicit revocation by a manager.
        host.Actor.Current = f.Admin;
        var first = await host.InvokeAsync<IssuedShareLink>(f.Create("first"));
        await host.InvokeAsync(new RevokeShareLink(f.PageId, first.Link.Id));
        // A page DocumentManager shares; closing the grant ends the link.
        await host.InvokeAsync(new SetPageGrant(f.PageId, new UpdatePageGrant(f.Reader.Id, null, "Admin", null)));
        host.Actor.Current = f.Reader;
        await host.InvokeAsync<IssuedShareLink>(f.Create("reader"));
        host.Actor.Current = f.Admin;
        await host.InvokeAsync(new SetPageGrant(f.PageId, new UpdatePageGrant(f.Reader.Id, null, null, await GrantVersionAsync(host, f.PageId, f.Reader.Id))));
        // A space Admin who loses the grant loses the link; the WikiAdmin's stamp rotation ends the WikiAdmin's.
        await host.InvokeAsync<IssuedShareLink>(f.Create("admin"));
        host.Actor.Current = f.WikiAdmin;
        await host.InvokeAsync<IssuedShareLink>(f.Create("wiki admin"));
        host.Actor.Current = await host.InvokeAsync<UserAccount>(new EndOtherSessions());
        await host.InvokeAsync(new SetSpaceGrant(f.SpaceId, new(f.Writer.Id, null, "Admin",
            Assert.Single((await host.InvokeAsync<SpaceAccessSummary>(new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Writer.Id).Version)));
        var grant = Assert.Single((await host.InvokeAsync<SpaceAccessSummary>(new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Admin.Id);
        await host.InvokeAsync(new SetSpaceGrant(f.SpaceId, new(f.Admin.Id, null, null, grant.Version)));
        // Own access: the mode history and the copied space defaults; back to the defaults closes them.
        var mode = await host.InvokeAsync<PageAccessModeResult>(new SetPageAccessMode(f.PageId, new(true, await ModeVersionAsync(host, f.PageId))));
        await host.InvokeAsync<PageAccessModeResult>(new SetPageAccessMode(f.PageId, new(false, mode.Version)));
        // Trashing the page ends its links; so does turning sharing off in the space.
        await host.InvokeAsync<IssuedShareLink>(f.Create("trashed"));
        await host.InvokeAsync<OrganizationChange>(new TrashPage(f.PageId, new TrashPageRequest(false, await TreeVersionAsync(host, f.SpaceId),
            await PageVersionAsync(host, f.PageId))));

        Assert.Equal(
        [
            "resource_grant.added admin ResourceGrantId",
            "resource_grant.removed admin ResourceGrantId",
            "grant.removed wikiadmin SpaceGrantId",
            "grant.added wikiadmin SpaceGrantId",
            "grant.removed wikiadmin SpaceGrantId",
            "resource_grant.added wikiadmin ResourceGrantId",
            "resource_grant.removed wikiadmin ResourceGrantId",
        ], await AuditPin.EventsAsync(host.ConnectionString, mark));
        Assert.Equal(
        [
            "share.created admin",
            "share.revoked admin",
            "share.created reader",
            "share.revoked admin",
            "share.created admin",
            "share.created wikiadmin",
            "share.revoked wikiadmin",
            "share.revoked wikiadmin",
            "share.created wikiadmin",
            "share.revoked wikiadmin",
        ], await AuditPin.ShareEventsAsync(host.ConnectionString));
        // The page's creator opened its mode history.
        Assert.Equal(["writer closed false", "wikiadmin closed true", "wikiadmin open false"],
            await AuditPin.RevisionsAsync(host.ConnectionString, "PageAccessModeRevision", "ChangedBy", "OwnAccess"));
    }

    private static Task<string?> GrantVersionAsync(Platform.AtlasTestHost host, Guid pageId, Guid userId)
        => VersionAsync(host, """SELECT "ActiveFrom" FROM "ResourceGrant" WHERE "PageId"=@a AND "UserId"=@b AND "ActiveTo" IS NULL""", pageId, userId);

    private static Task<string?> ModeVersionAsync(Platform.AtlasTestHost host, Guid pageId)
        => VersionAsync(host, """SELECT "ActiveFrom" FROM "PageAccessModeRevision" WHERE "PageId"=@a AND "ActiveTo" IS NULL""", pageId, Guid.Empty);

    private static async Task<string?> VersionAsync(Platform.AtlasTestHost host, string sql, Guid a, Guid b)
        => await ScalarAsync(host, sql, a, b) is DateTime at ? AdministrationVersion.Format(at) : null;

    private static async Task<long> TreeVersionAsync(Platform.AtlasTestHost host, Guid spaceId)
        => (long)(await ScalarAsync(host, """SELECT "Version" FROM "SpaceOrganization" WHERE "SpaceId"=@a""", spaceId, Guid.Empty))!;

    private static async Task<int> PageVersionAsync(Platform.AtlasTestHost host, Guid pageId)
        => (int)(await ScalarAsync(host, """SELECT "Version" FROM "Page" WHERE "Id"=@a""", pageId, Guid.Empty))!;

    private static async Task<object?> ScalarAsync(Platform.AtlasTestHost host, string sql, Guid a, Guid b)
    {
        await using var connection = new NpgsqlConnection(host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("a", a);
        command.Parameters.AddWithValue("b", b);
        return await command.ExecuteScalarAsync();
    }
}
