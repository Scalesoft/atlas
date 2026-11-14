using System.Globalization;
using Atlas.Server.Features.Access.Commands;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Tests.Hub;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Administration;

public sealed class ExternalGroupWriterTests
{
    private const string HubConnection = "seeds an enforced Hub connection of the installation (HubConnection) and its fixed external identity (ExternalIdentity)";

    [PostgreSqlFact]
    public async Task Bound_group_rejects_local_writers_even_when_disabled_before_noop_or_version_check()
    {
        await using var f = await AdministrationFixture.CreateAsync(HubConnection);
        var (member, group, _) = await HubEffectiveAuthorityTests.SeedAsync(f.Host, false);
        await f.Host.ExecuteAsync("UPDATE \"HubGroupBinding\" SET \"Enabled\"=false WHERE \"GroupId\"=:id", group);
        var summary = Assert.Single(await f.As<IReadOnlyList<GroupSummary>>(f.Admin, new ListGroups()), item => item.Id == group);
        using var before = f.Host.OpenSession();
        var membership = await before.QueryOver<GroupMember>().Where(item => item.GroupId == group && item.ActiveTo == null).SingleOrDefaultAsync();
        var version = membership.ActiveFrom.ToString("O", CultureInfo.InvariantCulture);
        var auditCount = await before.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\"").UniqueResultAsync<long>();

        await Managed(() => f.As(f.Admin, new SetGroupMembership(group, member.Id, new(true, version))));
        await Managed(() => f.As(f.Admin, new SetGroupMembership(group, member.Id, new(false, version))));
        await Managed(() => f.As(f.Admin, new SetGroupMembership(group, f.Member.Id, new(true, null))));
        await Managed(() => f.As<GroupSummary>(f.Admin, new RenameGroup(group, new(summary.Name, summary.Version))));
        await Managed(() => f.As<GroupSummary>(f.Admin, new RenameGroup(group, new("Renamed", "stale"))));

        using var after = f.Host.OpenSession();
        var retained = Assert.Single(await after.QueryOver<GroupMember>().Where(item => item.GroupId == group).ListAsync());
        Assert.Null(retained.ActiveTo);
        Assert.Equal(summary.Name, (await after.GetAsync<UserGroup>(group)).Name);
        Assert.Equal(auditCount, await after.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\"").UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Bulk_revoke_counts_and_closes_only_local_memberships_and_personal_grants()
    {
        await using var f = await AdministrationFixture.CreateAsync(HubConnection);
        var (member, externalGroup, identity) = await HubEffectiveAuthorityTests.SeedAsync(f.Host, false);
        var localGroup = await f.Host.SeedGroupAsync("Local group", member);
        await f.Host.SeedAsync(async session =>
        {
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = member.Id, Role = 1 });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, GroupId = externalGroup, Role = 1 });
        });
        using (var session = f.Host.OpenSession())
        {
            var impact = await RevokeAllRows.ImpactAsync(session, member.Id, CancellationToken.None);
            Assert.Equal(1, impact.Groups);
            Assert.Equal(1, impact.Grants);
        }
        await f.As(f.Admin, new RevokeAllAccess(member.Id));
        // Expiration hides effective membership, but must not make the retained upstream row locally writable.
        await HubEffectiveAuthorityTests.ExpireAsync(f.Host, identity);
        await f.As(f.Admin, new RevokeAllAccess(member.Id));
        using var after = f.Host.OpenSession();
        var current = Assert.Single(await after.QueryOver<GroupMember>().Where(item => item.UserId == member.Id && item.ActiveTo == null).ListAsync());
        Assert.Equal(externalGroup, current.GroupId);
        Assert.NotNull((await after.QueryOver<GroupMember>().Where(item => item.UserId == member.Id && item.GroupId == localGroup).SingleOrDefaultAsync()).ActiveTo);
        Assert.Empty(await after.QueryOver<SpaceGrant>().Where(item => item.UserId == member.Id && item.ActiveTo == null).ListAsync());
        Assert.Single(await after.QueryOver<SpaceGrant>().Where(item => item.GroupId == externalGroup && item.ActiveTo == null).ListAsync());
        Assert.Equal(0, (await RevokeAllRows.ImpactAsync(after, member.Id, CancellationToken.None)).Groups);
    }

    private static async Task Managed(Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(409, error.Status);
        Assert.Equal("external_group_managed", error.Code);
    }
}
