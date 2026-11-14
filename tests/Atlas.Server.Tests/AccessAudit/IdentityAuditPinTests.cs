using Atlas.Server.Features.Access.Commands;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.PersonalApi.Commands;
using Atlas.Server.Features.PersonalApi.Contracts;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// The audit and the history rows of the account, group, membership, space grant and API permission writers, pinned while the database
/// triggers wrote them (migration 022 and its successors). C# writes them since 3.6; the output must not change.
/// </summary>
public sealed class IdentityAuditPinTests
{
    [PostgreSqlFact]
    public async Task Account_group_membership_grant_and_api_permission_writers_audit_as_the_triggers_did()
    {
        await using var f = await AccessAuditFixture.CreateAsync("pins every audit event after its mark and every UserAccountRevision and UserGroupRevision row of the installation, by username");
        var mark = await AuditPin.MarkAsync(f.Host.ConnectionString);

        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("new-user", "New User")));
        const string password = "a long private test password";
        await f.As(null, new RedeemActivation(new(created.Token, password)));
        var account = await f.Account(created.User.Id);
        await f.As<ChangedPassword>(account, new ChangePassword(new(password, "a changed private test password")));
        var summary = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers()), x => x.Id == account.Id);
        await f.As<IssuedAccountToken>(f.Admin, new ResetPassword(account.Id, new(summary.Version)));
        summary = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers()), x => x.Id == account.Id);
        await f.As<UserSummary>(f.Admin, new SaveUser(account.Id, new("Renamed", true, false, summary.Version)));
        summary = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers()), x => x.Id == account.Id);
        // A no-op save writes nothing; a deactivation is an update.
        await f.As<UserSummary>(f.Admin, new SaveUser(account.Id, new("Renamed", true, false, summary.Version)));
        await f.As<UserSummary>(f.Admin, new SaveUser(account.Id, new("Renamed", false, false, summary.Version)));

        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new("Before")));
        group = await f.As<GroupSummary>(f.Admin, new RenameGroup(group.Id, new("After", group.Version)));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, null)));
        var member = Assert.Single(await f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, new ListGroupMembers(group.Id)));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(false, member.Version)));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, null)));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(null, group.Id, "Reader", null)));
        var grant = Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(null, group.Id, "Writer", grant.Version)));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null)));
        var permission = await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Member.Id, new ChangeApiPermission(true, null)));
        permission = await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Member.Id, new ChangeApiPermission(null, permission.Version, "write")));
        await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Member.Id, new ChangeApiPermission(false, permission.Version)));
        await f.As(f.Admin, new RevokeAllAccess(f.Member.Id));

        Assert.Equal(
        [
            "account.created admin AccountRevisionId",
            "credential.activated - AccountRevisionId",
            "credential.password_changed new-user AccountRevisionId",
            "credential.reset_requested admin AccountRevisionId",
            "account.updated admin AccountRevisionId",
            "account.updated admin AccountRevisionId",
            "group.created admin GroupRevisionId",
            "group.renamed admin GroupRevisionId",
            "membership.added admin GroupMemberId",
            "membership.removed admin GroupMemberId",
            "membership.added admin GroupMemberId",
            "grant.added admin SpaceGrantId",
            "grant.removed admin SpaceGrantId",
            "grant.added admin SpaceGrantId",
            "grant.added admin SpaceGrantId",
            "api_permission.enabled admin ApiPermissionId",
            "api_permission.disabled admin ApiPermissionId",
            "grant.removed admin SpaceGrantId",
            "membership.removed admin GroupMemberId",
        ], await AuditPin.EventsAsync(f.Host.ConnectionString, mark));
        // Every account write is a revision, stamp rotations included (the activation token's issue): the revision names its writer.
        Assert.Equal(
        [
            "- open",
            "- open",
            "admin closed",
            "admin closed",
            "- closed",
            "new-user closed",
            "admin closed",
            "admin closed",
            "admin open",
        ],await AuditPin.RevisionsAsync(f.Host.ConnectionString, "UserAccountRevision"));
        Assert.Equal(["admin closed", "admin open"], await AuditPin.RevisionsAsync(f.Host.ConnectionString, "UserGroupRevision"));
    }
}
