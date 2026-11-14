using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using Atlas.Server.Features.ExternalSharing.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.ExternalSharing;

public sealed class GrantValiditySharingTests
{
    [PostgreSqlFact]
    public async Task Temporary_admin_cannot_issue_a_link_beyond_own_authority()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        var expiry = DateTimeOffset.UtcNow.AddHours(2);
        await LimitAdminAsync(fixture, expiry);
        fixture.Host.Actor.Current = fixture.Admin;
        var denied = await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create()));
        Assert.Equal(403, denied.Status);
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(new CreateShareLink(fixture.PageId, new("Short review", expiry.AddMinutes(-1))));
        fixture.Host.Actor.Current = null;
        Assert.Equal("Published", (await fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret)))).Title);
    }

    [PostgreSqlFact]
    public async Task Unbounded_WikiAdmin_can_issue_a_link_despite_a_temporary_space_assignment()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        await LimitAdminAsync(fixture, DateTimeOffset.UtcNow.AddHours(2));
        fixture.Host.Actor.Current = fixture.WikiAdmin;
        Assert.Equal("active", (await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create())).Link.Status);
    }

    [PostgreSqlFact]
    public async Task Removing_unbounded_group_grant_revokes_links_that_outlive_remaining_authority()
        => await AssertLossOfUnboundedAuthorityRevokesAsync(removeMembership: false);

    [PostgreSqlFact]
    public async Task Removing_unbounded_group_membership_revokes_links_that_outlive_remaining_authority()
        => await AssertLossOfUnboundedAuthorityRevokesAsync(removeMembership: true);

    [PostgreSqlFact]
    public async Task Replacing_a_bounded_admin_assignment_keeps_old_links_revoked_even_when_extending_validity()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        await LimitAdminAsync(fixture, DateTimeOffset.UtcNow.AddDays(2));
        fixture.Host.Actor.Current = fixture.Admin;
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        fixture.Host.Actor.Current = fixture.WikiAdmin;
        var access = await fixture.Host.InvokeAsync<SpaceAccessSummary>(new GetSpaceAccess(fixture.SpaceId));
        var current = Assert.Single(access.Grants, grant => grant.UserId == fixture.Admin.Id);
        await fixture.Host.InvokeAsync(new SetSpaceGrant(fixture.SpaceId,
            new(fixture.Admin.Id, null, "Admin", current.Version, null, DateTimeOffset.UtcNow.AddDays(3))));
        fixture.Host.Actor.Current = null;
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<SharedPageDto>(
            new OpenSharedPage(ShareSecret.Hash(issued.Secret))))).Status);
    }

    /// <summary>An expiry closes no link when it happens: the first open it refuses records the revocation, once.</summary>
    [PostgreSqlFact]
    public async Task An_expired_grant_marks_the_link_revoked_at_its_first_refused_open()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        await LimitAdminAsync(fixture, DateTimeOffset.UtcNow.AddHours(2));
        fixture.Host.Actor.Current = fixture.Admin;
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(new CreateShareLink(fixture.PageId, new("Review", DateTimeOffset.UtcNow.AddHours(1))));
        // The grant runs out (as time would make it).
        await fixture.Host.ExecuteAsync("""UPDATE "SpaceGrant" SET "ExpiresAt" = now() - interval '1 minute' WHERE "UserId" = :id AND "ActiveTo" IS NULL""", fixture.Admin.Id);
        fixture.Host.Actor.Current = null;
        for (var open = 0; open < 2; open++)
            Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<SharedPageDto>(
                new OpenSharedPage(ShareSecret.Hash(issued.Secret))))).Status);
        using var read = fixture.Host.OpenSession();
        Assert.NotNull((await read.GetAsync<PageShareLink>(issued.Link.Id)).ActiveTo);
        Assert.Equal(1L, await read.CreateSQLQuery("""
            SELECT count(*) FROM "PageAccessEvent" WHERE "Action" = 'share.revoked' AND "ShareLinkId" = :link AND "ActorId" IS NULL
            """).SetParameter("link", issued.Link.Id).UniqueResultAsync<long>());
    }

    private static async Task AssertLossOfUnboundedAuthorityRevokesAsync(bool removeMembership)
    {
        await using var fixture = await SharingFixture.CreateAsync();
        await fixture.Enable();
        await LimitAdminAsync(fixture, DateTimeOffset.UtcNow.AddHours(2));
        var group = await fixture.Host.SeedGroupAsync("Permanent admins", fixture.Admin);
        await fixture.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = fixture.SpaceId, GroupId = group, Role = 3 }));
        fixture.Host.Actor.Current = fixture.Admin;
        var issued = await fixture.Host.InvokeAsync<IssuedShareLink>(fixture.Create());
        await fixture.Host.CloseAccessAsync(removeMembership ? AccessAuditLog.GroupMember : AccessAuditLog.SpaceGrant, "\"GroupId\"=:id", group);
        using (var read = fixture.Host.OpenSession())
            Assert.NotNull((await read.GetAsync<PageShareLink>(issued.Link.Id)).ActiveTo);
        await fixture.Host.SeedAsync(async session =>
        {
            if (removeMembership)
                await session.SaveAsync(new GroupMember { Id = Guid.NewGuid(), GroupId = group, UserId = fixture.Admin.Id, ActiveFrom = DateTime.UtcNow });
            else
                await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = fixture.SpaceId, GroupId = group, Role = 3 });
        });
        fixture.Host.Actor.Current = null;
        var denied = await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret))));
        Assert.Equal(404, denied.Status);
    }

    private static async Task LimitAdminAsync(SharingFixture fixture, DateTimeOffset expiry)
    {
        fixture.Host.Actor.Current = fixture.WikiAdmin;
        // Keep a permanent explicit owner before making the original owner temporary.
        await fixture.Host.InvokeAsync(new SetSpaceGrant(fixture.SpaceId, new(fixture.WikiAdmin.Id, null, "Admin", null)));
        var access = await fixture.Host.InvokeAsync<SpaceAccessSummary>(new GetSpaceAccess(fixture.SpaceId));
        var current = Assert.Single(access.Grants, grant => grant.UserId == fixture.Admin.Id);
        await fixture.Host.InvokeAsync(new SetSpaceGrant(fixture.SpaceId, new(fixture.Admin.Id, null, "Admin", current.Version, null, expiry)));
    }
}
