using System.Globalization;
using Npgsql;
using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

public sealed class AccessAuditHandlerTests
{
    [PostgreSqlFact]
    public async Task Membership_and_grants_record_the_real_actor_and_immutable_role_at_each_transition()
    {
        await using var f = await AccessAuditFixture.CreateAsync();
        var mark = await f.MarkAsync();
        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new(TestLogin.For("Engineering"))));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, null)));
        var member = Assert.Single(await f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, new ListGroupMembers(group.Id)));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, member.Version)));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(false, member.Version)));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(null, group.Id, "Reader", null)));
        var grant = Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(null, group.Id, "Writer", grant.Version)));
        var audit = await f.Audit(f.Admin);
        // The audit lists every test's events: this test's are those of its group.
        var items = (await f.AuditSinceAsync(f.Admin, mark)).Where(x => x.GroupId == group.Id).ToArray();
        var membership = items.Where(x => x.Action.StartsWith("membership.", StringComparison.Ordinal)).Reverse().ToArray();
        Assert.Equal(new[] { "membership.added", "membership.removed" }, membership.Select(x => x.Action));
        Assert.All(membership, item =>
        {
            Assert.Equal(f.Admin.Id, item.ActorId);
            Assert.Equal(f.Member.Id, item.UserId);
            Assert.Equal(group.Id, item.GroupId);
        });
        var grants = items.Where(x => x.Action.StartsWith("grant.", StringComparison.Ordinal)).Reverse().ToArray();
        Assert.Equal(new[] { "grant.added", "grant.removed", "grant.added" }, grants.Select(x => x.Action));
        Assert.Equal(new[] { "Reader", "Reader", "Writer" }, grants.Select(x => x.Role));
        Assert.All(grants, item =>
        {
            Assert.Equal(f.Admin.Id, item.ActorId);
            Assert.Equal(group.Id, item.GroupId);
            Assert.Equal(f.SpaceId, item.SpaceId);
        });
        Assert.All(items, item => Assert.Equal(DateTimeKind.Utc, item.OccurredAt.Kind));
        Assert.True(audit.CoverageStartedAt <= items.Max(x => x.OccurredAt));
    }

    [PostgreSqlFact]
    public async Task Account_group_and_api_permissions_have_attributed_events_but_noop_and_stamp_rotation_do_not()
    {
        await using var f = await AccessAuditFixture.CreateAsync();
        var mark = await f.MarkAsync();
        var summary = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: f.Member.Id)));
        await f.As<UserSummary>(f.Admin, new SaveUser(f.Member.Id, new("Renamed", false, false, summary.Version)));
        var (first, second) = (TestLogin.For("Before"), TestLogin.For("After"));
        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new(first)));
        await f.As<GroupSummary>(f.Admin, new RenameGroup(group.Id, new(second, group.Version)));
        var permission = await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Member.Id, new ChangeApiPermission(true, null)));
        await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Member.Id, new ChangeApiPermission(false, permission.Version)));
        // The audit lists every test's events: this test's are those of its member and its group.
        async Task<AccessAuditItem[]> OwnAsync() => (await f.AuditSinceAsync(f.Admin, mark)).Where(x => x.UserId == f.Member.Id || x.GroupId == group.Id).ToArray();
        var before = await OwnAsync();
        Assert.Contains(before, x => x.Action == "account.updated" && x.ActorId == f.Admin.Id && x.UserName == "Renamed" && x.IsActive == false);
        Assert.Contains(before, x => x.Action == "group.renamed" && x.GroupName == second && x.ActorId == f.Admin.Id);
        var permissions = before.Where(x => x.Action.StartsWith("api_permission.", StringComparison.Ordinal)).Reverse().ToArray();
        Assert.Equal(new[] { "api_permission.enabled", "api_permission.disabled" }, permissions.Select(x => x.Action));
        Assert.All(permissions, x => Assert.Equal(f.Admin.Id, x.ActorId));
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='new-stamp' WHERE \"Id\"=@id", f.Member.Id);
        var renamed = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: f.Member.Id)));
        await f.As<UserSummary>(f.Admin, new SaveUser(f.Member.Id, new(renamed.DisplayName, renamed.IsActive, renamed.IsWikiAdmin, renamed.Version)));
        Assert.Equal(before.Length, (await OwnAsync()).Length);
    }

    [PostgreSqlFact]
    public async Task Credential_lifecycle_has_semantic_events_and_no_secrets_or_guessed_anonymous_actor()
    {
        await using var f = await AccessAuditFixture.CreateAsync();
        var mark = await f.MarkAsync();
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("new-user"), "New User")));
        const string password = "a long private test password";
        await f.As(null, new RedeemActivation(new(created.Token, password)));
        var account = await f.Account(created.User.Id);
        var changed = await f.As<ChangedPassword>(account, new ChangePassword(new(password, "a changed private test password")));
        // The password change rotates the stamp, which the account history records as a new revision; the event must name that current one.
        Assert.Equal(1L, await f.Scalar($"SELECT count(*) FROM \"AccessAuditEvent\" e JOIN \"UserAccountRevision\" r ON r.\"Id\"=e.\"AccountRevisionId\" AND r.\"ActiveTo\" IS NULL WHERE e.\"Action\"='credential.password_changed' AND r.\"UserId\"='{account.Id}'"));
        var summary = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: account.Id)));
        var reset = await f.As<IssuedAccountToken>(f.Admin, new ResetPassword(account.Id, new(summary.Version)));
        var audit = await f.AuditSinceAsync(f.Admin, mark);
        Assert.Contains(audit, x => x.Action == "credential.activated" && x.ActorId == null && x.UserId == account.Id);
        Assert.Contains(audit, x => x.Action == "credential.password_changed" && x.ActorId == changed.Id && x.UserId == changed.Id);
        Assert.Contains(audit, x => x.Action == "credential.reset_requested" && x.ActorId == f.Admin.Id && x.UserId == account.Id);
        // Every audit row written since the mark (the audit holds every test's events; this test's are among these).
        var stored = (string)(await f.Scalar($"SELECT COALESCE(jsonb_agg(to_jsonb(e))::text,'[]') FROM \"AccessAuditView\" e WHERE e.\"Id\" > {mark}"))!;
        Assert.DoesNotContain(password, stored);
        Assert.DoesNotContain(created.Token, stored);
        Assert.DoesNotContain(reset.Token, stored);
        Assert.DoesNotContain("PasswordHash", stored);
        Assert.DoesNotContain("SecurityStamp", stored);
        Assert.DoesNotContain("TokenHash", stored);
    }

    [PostgreSqlFact]
    public async Task Failed_audit_insert_rolls_back_access_mutation_and_history_cannot_be_rewritten()
    {
        await using var f = await AccessAuditFixture.CreateAsync("installs a failing trigger on AccessAuditEvent (DDL), which would fail every other test's audited write");
        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new("Engineering")));
        await f.Execute("CREATE FUNCTION fail_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Simulated audit failure'; END $$; CREATE TRIGGER fail_audit BEFORE INSERT ON \"AccessAuditEvent\" FOR EACH ROW EXECUTE FUNCTION fail_audit()", null);
        await Assert.ThrowsAnyAsync<Exception>(() => f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, null))));
        Assert.Empty(await f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, new ListGroupMembers(group.Id)));
        await f.Execute("DROP TRIGGER fail_audit ON \"AccessAuditEvent\"", null);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null)));
        // The history cannot be rewritten by the application (IntegrityRules); no server code updates or deletes the audit
        // (IntegrityRulesConventionTests).
        await Platform.IntegrityRefusal.AssertAsync(f.Host, IntegrityRules.HistoryImmutable, async s =>
            (await s.QueryOver<SpaceGrant>().Where(x => x.UserId == f.Member.Id).SingleOrDefaultAsync())!.Role = 3);
        Assert.Equal("Reader", Assert.Single((await f.Audit(f.Admin)).Items, x => x.Action == "grant.added").Role);
    }

    [PostgreSqlFact]
    public async Task Only_current_wiki_admin_can_read_keyset_paginated_audit()
    {
        await using var f = await AccessAuditFixture.CreateAsync();
        for (var i = 0; i < 4; i++)
        {
            await f.As<GroupSummary>(f.Admin, new AddGroup(new(TestLogin.For("Group " + i))));
        }
        var first = await f.Audit(f.Admin, limit: 2);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        var next = await f.Audit(f.Admin, first.NextBeforeId, 2);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(next.Items.Select(x => x.Id)));
        Assert.All(next.Items, x => Assert.True(long.Parse(x.Id, CultureInfo.InvariantCulture) < long.Parse(first.NextBeforeId!, CultureInfo.InvariantCulture)));
        await Denied(403, () => f.Audit(f.Member));
        await Denied(400, () => f.Audit(f.Admin, "9223372036854775808"));
        await Denied(400, () => f.Audit(f.Admin, limit: 101));
        await Denied(400, () => f.Audit(f.Admin, "-1"));
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=@id", f.Admin.Id);
        await Denied(403, () => f.Audit(f.Admin));
    }

    [PostgreSqlFact]
    public async Task Upgrade_does_not_invent_legacy_events_and_unattributed_new_changes_stay_unknown()
    {
        await using var f = await LegacyAuditFixture.CreateAsync();
        // The validity migration adds unbounded columns; every legacy value and the empty new bounds must survive.
        var before = await f.Scalar("SELECT jsonb_agg(to_jsonb(g) || jsonb_build_object('StartAt', NULL, 'ExpiresAt', NULL))::text FROM \"SpaceGrant\" g");
        await AtlasMigrations.MigrateAsync(f.ConnectionString);
        Assert.Equal(before, await f.Scalar("SELECT jsonb_agg(to_jsonb(g))::text FROM \"SpaceGrant\" g"));
        Assert.Empty((await f.Audit(f.Admin)).Items);
        await f.CloseGrantsAsync(f.Member.Id);
        var audit = await f.Audit(f.Admin);
        var item = Assert.Single(audit.Items);
        Assert.Equal("grant.removed", item.Action);
        Assert.Null(item.ActorId);
        Assert.Null(item.ActorName);
        await AtlasMigrations.MigrateAsync(f.ConnectionString);
        Assert.Single((await f.Audit(f.Admin)).Items);
    }

    private static async Task Denied(int status, Func<Task<AccessAuditList>> action)
    {
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
    }
}
