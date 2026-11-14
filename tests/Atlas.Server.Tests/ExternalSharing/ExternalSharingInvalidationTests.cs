using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using Atlas.Server.Features.ExternalSharing.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

/// <summary>
/// Share link closing is terminal and exact (ShareRevocation, C# since 3.6; the database triggers did it before): a trashed page ends its
/// links and restoring it revives none; losing one Admin source keeps the link while another covers it, losing the last one closes it,
/// a rolled back closure closes nothing; a rotated security stamp ends the issuer's links even when the old stamp comes back.
/// </summary>
public sealed class ExternalSharingInvalidationTests
{
    [PostgreSqlFact]
    public async Task Trash_and_restore_never_resurrect_issued_links()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        int version;
        long tree;
        using (var read = fixture.Host.OpenSession())
        {
            version = (await read.GetAsync<Page>(fixture.PageId))!.Version;
            tree = await read.CreateSQLQuery("SELECT \"Version\" FROM \"SpaceOrganization\" WHERE \"SpaceId\"=:id").SetParameter("id", fixture.SpaceId).UniqueResultAsync<long>();
        }
        fixture.Host.Actor.Current = fixture.Admin;
        await fixture.Host.InvokeAsync<OrganizationChange>(new TrashPage(fixture.PageId, new TrashPageRequest(false, tree, version)));
        // A restore is no revocation path: it only clears the trash columns.
        await fixture.Host.ExecuteAsync("UPDATE \"Page\" SET \"IsTrashed\"=false,\"TrashedAt\"=NULL,\"TrashedById\"=NULL,\"TrashBatchId\"=NULL WHERE \"Id\"=:id", fixture.PageId);
        await DeniedAsync(fixture, issued);
    }

    [PostgreSqlFact]
    public async Task Grant_and_membership_loss_revoke_but_alternative_Admin_source_and_rollback_keep_link()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var creator = await fixture.Host.SeedUserAsync("shareadmin");
        var group = await fixture.Host.SeedGroupAsync("Admins", creator);
        await fixture.Host.SeedAsync(async session =>
        {
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = fixture.SpaceId, UserId = creator.Id, Role = 3 });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = fixture.SpaceId, GroupId = group, Role = 3 });
        });
        fixture.Host.Actor.Current = creator;
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());

        await fixture.Host.CloseAccessAsync(AccessAuditLog.SpaceGrant, "\"UserId\"=:id", creator.Id);
        await OpensAsync(fixture, issued);
        using (var session = fixture.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var closed = await session.CreateSQLQuery("UPDATE \"GroupMember\" SET \"ActiveTo\"=clock_timestamp() WHERE \"GroupId\"=:id AND \"ActiveTo\" IS NULL RETURNING \"Id\"")
                .SetParameter("id", group).ListAsync<Guid>();
            await AccessAuditLog.ClosedAsync(session, AccessAuditLog.GroupMember, [.. closed], null, CancellationToken.None);
            await transaction.RollbackAsync();
        }
        await OpensAsync(fixture, issued);
        await fixture.Host.CloseAccessAsync(AccessAuditLog.GroupMember, "\"GroupId\"=:id", group);
        await fixture.Host.ExecuteAsync("INSERT INTO \"GroupMember\" (\"Id\",\"GroupId\",\"UserId\",\"ActiveFrom\") SELECT gen_random_uuid(),\"GroupId\",\"UserId\",clock_timestamp() FROM \"GroupMember\" WHERE \"GroupId\"=:id LIMIT 1", group);
        await DeniedAsync(fixture, issued);
    }

    [PostgreSqlFact]
    public async Task Security_stamp_change_is_terminal_even_if_old_stamp_is_restored()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        var stamp = fixture.Admin.SecurityStamp;
        foreach (var next in new[] { "changed", stamp })
        {
            await fixture.Host.SeedAsync(async session =>
            {
                var account = (await session.GetAsync<UserAccount>(fixture.Admin.Id))!;
                account.SecurityStamp = next;
                await AccountHistory.RecordAsync(session, account, null, CancellationToken.None);
            });
        }
        await DeniedAsync(fixture, issued);
    }

    private static async Task OpensAsync(SharingFixture fixture, IssuedShareLink issued)
    {
        fixture.Host.Actor.Current = null;
        Assert.NotNull(await fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret))));
    }

    private static async Task DeniedAsync(SharingFixture fixture, IssuedShareLink issued)
    {
        fixture.Host.Actor.Current = null;
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<SharedPageDto>(
            new OpenSharedPage(ShareSecret.Hash(issued.Secret))))).Status);
    }
}
