using System.Globalization;
using NHibernate.Exceptions;
using Npgsql;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Administration.Queries;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Recovery.Domain;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Builtin.Commands;

namespace Atlas.Server.Tests.Administration;

public sealed class AdministrationHandlerTests
{
    private const string AccountContactsRun = "the account contacts job runs over every account of the installation and its queued run is one JobRun row of it";
    private const string LastAdministrator = "the last active wiki administrator is counted over every account of the installation (UserAccount)";

    [PostgreSqlFact]
    public async Task Listing_users_is_wiki_admin_only_and_ordered_by_display_name()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        // The directory lists every account of the host; the test's own two keep their order by display name.
        var users = (await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers())).Where(x => x.Id == f.Admin.Id || x.Id == f.Member.Id).ToArray();
        Assert.Equal([f.Admin.Username, f.Member.Username], users.Select(x => x.Username));
        Assert.All(users, user => Assert.Equal("none", user.CredentialState));
        Assert.All(users, user => Assert.Equal("external", user.Source));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<IReadOnlyList<UserSummary>>(f.Member, new ListUsers()));
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Admin.Id);
        await AdministrationFixture.Denied(401, "Přihlášení vypršelo.", () => f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers()));
    }

    [PostgreSqlFact]
    public async Task Listing_users_searches_by_name_pages_by_offset_finds_one_id_and_escapes_wildcards()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        // Every account of the host is listed: the test's names carry a word of their own, which every search adds.
        var token = Guid.NewGuid().ToString("N")[..8];
        var seeded = new[] { "a_b", "axb", "100%", "1000", "Zebra", "Nováková" };
        var accounts = new List<UserAccount>();
        foreach (var name in seeded) accounts.Add(await f.Host.SeedUserAsync($"{name} {token}"));
        Task<IReadOnlyList<UserSummary>> List(ListUsers query) => f.As<IReadOnlyList<UserSummary>>(f.Admin, query);
        async Task<string[]> Found(string search) => (await List(new($"{search} {token}"))).Select(x => x.DisplayName).ToArray();

        Assert.Equal([$"Zebra {token}"], await Found("ZEB"));
        Assert.Equal([$"a_b {token}"], await Found("a_b"));
        Assert.Equal([$"100% {token}"], await Found("0%"));
        Assert.Equal([f.Admin.Username], (await List(new($"  {f.Admin.Username.ToUpperInvariant()} "))).Select(x => x.Username));
        // Diacritics and case aside, every word somewhere: „novakova“ and „NOVÁK ová“ find Nováková.
        Assert.Equal([$"Nováková {token}"], await Found("novakova"));
        Assert.Equal([$"Nováková {token}"], await Found("NOVÁK ová"));
        var ours = accounts.Select(x => x.Id).Append(f.Admin.Id).Append(f.Member.Id).ToHashSet();
        Assert.Equal(8, (await List(new("  "))).Count(x => ours.Contains(x.Id)));
        Assert.Empty(await Found("nobody"));

        var all = (await List(new(token))).Select(x => x.Id).ToArray();
        Assert.Equal(6, all.Length);
        Assert.Equal(all[3..], (await List(new(token, Offset: 3))).Select(x => x.Id));
        Assert.Equal(all, (await List(new(token, Offset: -5))).Select(x => x.Id));
        Assert.Empty(await List(new(token, Offset: 6)));

        Assert.Equal(accounts[4].Id, Assert.Single(await List(new(Id: accounts[4].Id))).Id);
        Assert.Empty(await List(new(Id: Guid.NewGuid())));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<IReadOnlyList<UserSummary>>(f.Member, new ListUsers("a")));
    }

    [PostgreSqlFact]
    public async Task An_account_made_from_a_contact_is_that_contact_at_once_or_is_not_made()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        async Task<Guid> Contact(string name, string scope) => (await f.As<ObjectDto>(f.Admin,
            new CreateObject(ContactObjectType.Id, scope, null, new JsonObject { ["name"] = name }))).Id;
        var contact = await Contact("Rita Nová", ObjectScopes.Wiki);
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("rita.nova", "Rita Nová", ContactId: contact)));
        Assert.Equal(created.User.Id.ToString(), await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            $"""SELECT "Data"->>'directoryId' FROM "ContentResource" WHERE "Id"='{contact}'"""));
        Assert.Equal(contact, created.User.ContactId);

        // A contact that is someone's already, or private, refuses, and the account is not made either (one transaction).
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("rita.druha", "Rita Nová", ContactId: contact))))).Status);
        var secret = await Contact("Soukromá Osoba", ObjectScopes.Private);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("soukroma", "Soukromá Osoba", ContactId: secret))))).Status);
        Assert.Equal(0L, await OutboxTests.ScalarAsync(f.Host.ConnectionString, """SELECT count(*) FROM "UserAccount" WHERE "Username" IN ('rita.druha','soukroma')"""));
        // Only a wiki administrator makes accounts.
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<CreatedBuiltinUser>(f.Member, new CreateUser(new("jiny", "Jiný", ContactId: contact))));
    }

    [PostgreSqlFact]
    public async Task A_new_account_asks_for_its_contact_and_the_job_gives_it_one()
    {
        await using var f = await AdministrationFixture.CreateAsync(AccountContactsRun);
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("invited", "Invited")));
        // The request is written in the handler's transaction: a queued run of the job waits for the account that is committed with it.
        Assert.Equal(1L, await OutboxTests.ScalarAsync(f.Host.ConnectionString, $"""SELECT count(*) FROM "JobRun" WHERE "JobId"='{AccountContactsJob.JobId}' AND "State"='queued'"""));
        await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(AccountContactsJob.JobId, "Test");
        Assert.Equal(1L, await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            $"""SELECT count(*) FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "Data"->>'directoryId'='{created.User.Id}'"""));
        // Lidé opens that contact as the person's profile.
        var contact = await OutboxTests.ScalarAsync(f.Host.ConnectionString,
            $"""SELECT "Id" FROM "ContentResource" WHERE "TypeId"='atlas.contact' AND "Data"->>'directoryId'='{created.User.Id}'""");
        Assert.Equal(contact, Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: created.User.Id))).ContactId);
    }

    [PostgreSqlFact]
    public async Task Reactivating_an_account_asks_for_its_contact_again_and_deactivating_one_does_not()
    {
        await using var f = await AdministrationFixture.CreateAsync(AccountContactsRun);
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("returning", "Returning")));
        await f.Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(AccountContactsJob.JobId, "Test");
        var queued = $"""SELECT count(*) FROM "JobRun" WHERE "JobId"='{AccountContactsJob.JobId}' AND "State"='queued'""";

        var off = await f.As<UserSummary>(f.Admin, new SaveUser(created.User.Id, new("Returning", false, false, created.User.Version)));
        Assert.Equal(0L, await OutboxTests.ScalarAsync(f.Host.ConnectionString, queued));
        await f.As<UserSummary>(f.Admin, new SaveUser(created.User.Id, new("Returning", true, false, off.Version)));
        Assert.Equal(1L, await OutboxTests.ScalarAsync(f.Host.ConnectionString, queued));
    }

    [PostgreSqlFact]
    public async Task The_user_summary_counts_pending_deactivated_and_admins_for_wiki_admins_only()
    {
        await using var f = await AdministrationFixture.CreateAsync("the summary counts every account of the installation (UserAccount), which other tests change meanwhile");
        var invited = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("invited", "Invited")));
        var leaving = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("leaving", "Leaving")));
        await f.As<UserSummary>(f.Admin, new SaveUser(leaving.User.Id, new("Leaving", false, false, leaving.User.Version)));

        Assert.Equal(new UserDirectorySummary(4, 1, 1, 1), await f.As<UserDirectorySummary>(f.Admin, new GetUserDirectorySummary()));
        Assert.Equal("pending", invited.User.CredentialState);
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<UserDirectorySummary>(f.Member, new GetUserDirectorySummary()));
    }

    [PostgreSqlFact]
    public async Task Listing_group_members_searches_by_name_and_pages_by_offset()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var people = new List<UserAccount>();
        foreach (var name in new[] { "anna", "bruno", "cyril", "50%off", "5000" }) people.Add(await f.Host.SeedUserAsync(name));
        var outsider = await f.Host.SeedUserAsync("annabel");
        var group = await f.Host.SeedGroupAsync("Tým", people.ToArray());
        Task<IReadOnlyList<GroupMemberSummary>> List(ListGroupMembers query) => f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, query);

        Assert.Equal([people[0].Username], (await List(new(group, "ANN"))).Select(x => x.Username));
        Assert.Equal([people[3].Username], (await List(new(group, "0%"))).Select(x => x.Username));
        Assert.Empty(await List(new(group, "annabel")));
        Assert.DoesNotContain(outsider.Id, (await List(new(group))).Select(x => x.Id));
        var all = (await List(new(group))).Select(x => x.Id).ToArray();
        Assert.Equal(5, all.Length);
        Assert.Equal(all[2..], (await List(new(group, Offset: 2))).Select(x => x.Id));
        Assert.Empty(await List(new(group, Offset: 5)));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<IReadOnlyList<GroupMemberSummary>>(f.Member, new ListGroupMembers(group, "a")));
    }

    [PostgreSqlFact]
    public async Task Creating_a_builtin_user_issues_a_pending_invitation_and_rejects_a_duplicate_username()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var login = TestLogin.For("Writer");
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(login, "Writer One")));
        Assert.Equal(login.ToLowerInvariant(), created.User.Username);
        Assert.Equal("pending", created.User.CredentialState);
        Assert.Equal("builtin", created.User.Source);
        Assert.Equal(64, created.Token.Length);
        Assert.DoesNotContain(created.Token, created.ToString(), StringComparison.Ordinal);
        Assert.True(created.ExpiresAt > DateTime.UtcNow);

        using (var session = f.Host.OpenSession())
        {
            Assert.Null((await session.GetAsync<BuiltinCredential>(created.User.Id)).PasswordHash);
            Assert.Equal(1, await session.QueryOver<BuiltinAccountToken>().Where(x => x.UserId == created.User.Id).RowCountAsync());
        }

        await AdministrationFixture.Denied(409, "Uživatelské jméno už existuje.", () => f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(login.ToUpperInvariant(), "Duplicate"))));
        await AdministrationFixture.Denied(400, () => f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("nameless"), "  "))));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<CreatedBuiltinUser>(f.Member, new CreateUser(new(TestLogin.For("nope"), "Nope"))));
    }

    [PostgreSqlFact]
    public async Task Saving_a_user_is_compare_and_swap_rotates_the_stamp_only_on_privilege_change_and_keeps_one_admin()
    {
        await using var f = await AdministrationFixture.CreateAsync(LastAdministrator);
        var member = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: f.Member.Id)));
        var stamp = await Stamp(f, f.Member.Id);

        var renamed = await f.As<UserSummary>(f.Admin, new SaveUser(f.Member.Id, new("Member renamed", true, false, member.Version)));
        Assert.Equal("Member renamed", renamed.DisplayName);
        Assert.NotEqual(member.Version, renamed.Version);
        Assert.Equal(stamp, await Stamp(f, f.Member.Id));

        await AdministrationFixture.Denied(409, "Údaj se mezitím změnil. Obnovte seznam a změnu zopakujte.", () => f.As<UserSummary>(f.Admin, new SaveUser(f.Member.Id, new("Stale", true, false, member.Version))));

        var promoted = await f.As<UserSummary>(f.Admin, new SaveUser(f.Member.Id, new(renamed.DisplayName, true, true, renamed.Version)));
        Assert.True(promoted.IsWikiAdmin);
        Assert.NotEqual(stamp, await Stamp(f, f.Member.Id));

        var admin = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: f.Admin.Id)));
        await f.As<UserSummary>(f.Admin, new SaveUser(f.Admin.Id, new(admin.DisplayName, true, false, admin.Version)));
        // The promotion rotated the member's stamp, so the seeded snapshot is revoked; act with the account as it stands now.
        var survivor = await f.Account(f.Member.Id);
        var last = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(survivor, new ListUsers()), x => x.Id == f.Member.Id);
        await AdministrationFixture.Denied(409, "Nelze odebrat nebo deaktivovat posledního aktivního správce wiki.", () => f.As<UserSummary>(survivor, new SaveUser(f.Member.Id, new(last.DisplayName, true, false, last.Version))));
        await AdministrationFixture.Denied(404, () => f.As<UserSummary>(survivor, new SaveUser(Guid.NewGuid(), new("Ghost", true, false, last.Version))));
    }

    private static async Task<string> Stamp(AdministrationFixture fixture, Guid id)
    {
        using var session = fixture.Host.OpenSession();
        return (await session.GetAsync<UserAccount>(id)).SecurityStamp;
    }

    [PostgreSqlFact]
    public async Task Reset_requires_a_builtin_credential_a_current_version_and_records_a_semantic_audit_event()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("writer"), "Writer One")));
        var current = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: created.User.Id)));
        var reset = await f.As<IssuedAccountToken>(f.Admin, new ResetPassword(created.User.Id, new(current.Version)));
        Assert.NotEqual(created.Token, reset.Token);

        using (var session = f.Host.OpenSession())
        {
            Assert.Equal(1, await session.QueryOver<BuiltinAccountToken>().Where(x => x.UserId == created.User.Id).RowCountAsync());
            Assert.Contains("credential.reset_requested", await AuditedAsync(session, created.User.Id));
        }

        await AdministrationFixture.Denied(409, () => f.As<IssuedAccountToken>(f.Admin, new ResetPassword(created.User.Id, new(current.Version))));
        var memberVersion = await MemberVersion(f);
        await AdministrationFixture.Denied(409, "Účet nemá lokální přihlašovací metodu.", () => f.As<IssuedAccountToken>(f.Admin, new ResetPassword(f.Member.Id, new(memberVersion))));
        await AdministrationFixture.Denied(404, () => f.As<IssuedAccountToken>(f.Admin, new ResetPassword(Guid.NewGuid(), new(current.Version))));
    }

    /// <summary>Owner decision 28. 9.: the invitation of a new account lasts seven days (also when an administrator re-issues it before
    /// the account was ever activated), a password reset of an account that has had a password thirty minutes.</summary>
    [PostgreSqlFact]
    public async Task Invitation_lasts_seven_days_and_a_password_reset_thirty_minutes()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        static void Lasts(TimeSpan expected, DateTime expiresAt) =>
            Assert.InRange(expiresAt - DateTime.UtcNow, expected - TimeSpan.FromMinutes(1), expected);
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("writer"), "Writer One")));
        Assert.Equal("invitation", created.Purpose);
        Lasts(TimeSpan.FromDays(7), created.ExpiresAt);
        async Task<IssuedAccountToken> Reset()
        {
            var current = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: created.User.Id)));
            return await f.As<IssuedAccountToken>(f.Admin, new ResetPassword(created.User.Id, new(current.Version)));
        }
        // Never activated, even after the first code lapsed: a new invitation.
        await f.Host.ExecuteAsync("UPDATE \"BuiltinAccountToken\" SET \"ExpiresAt\"=clock_timestamp()-interval '1 second' WHERE \"UserId\"=:id", created.User.Id);
        var reinvited = await Reset();
        Assert.Equal("invitation", reinvited.Purpose);
        Lasts(TimeSpan.FromDays(7), reinvited.ExpiresAt);

        await f.As(null, new RedeemActivation(new(reinvited.Token, "initial long password")));
        var reset = await Reset();
        Assert.Equal("reset", reset.Purpose);
        Lasts(TimeSpan.FromMinutes(30), reset.ExpiresAt);
        // A reset that was not redeemed stays a reset when issued again; the old code is gone either way.
        var again = await Reset();
        Assert.Equal("reset", again.Purpose);
        Lasts(TimeSpan.FromMinutes(30), again.ExpiresAt);
        await Assert.ThrowsAsync<ApiException>(() => f.As(null, new RedeemActivation(new(reset.Token, "another long password"))));
        await f.As(null, new RedeemActivation(new(again.Token, "another long password")));
    }

    private static async Task<string> MemberVersion(AdministrationFixture fixture)
    {
        var users = await fixture.As<IReadOnlyList<UserSummary>>(fixture.Admin, new ListUsers(Id: fixture.Member.Id));
        return Assert.Single(users, x => x.Id == fixture.Member.Id).Version;
    }

    [PostgreSqlFact]
    public async Task Activation_is_single_use_and_a_second_concurrent_redeem_loses()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("writer"), "Writer One")));
        f.Host.Actor.Current = null;

        async Task<bool> Redeem()
        {
            try
            {
                await f.Host.InvokeAsync(new RedeemActivation(new(created.Token, "initial long password")));
                return true;
            }
            catch (ApiException error) when (error.Status == 400)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Redeem(), Redeem());
        Assert.Single(results, x => x);

        var user = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: created.User.Id)));
        Assert.Equal("active", user.CredentialState);
        await AdministrationFixture.Denied(400, "Aktivační odkaz je neplatný nebo vypršel.", () => f.Host.InvokeAsync(new RedeemActivation(new(new string('a', 64), "another long password"))));
        await AdministrationFixture.Denied(400, "Aktivační odkaz je neplatný nebo vypršel.", () => f.Host.InvokeAsync(new RedeemActivation(new("short", "another long password"))));
        await AdministrationFixture.Denied(400, () => f.Host.InvokeAsync(new RedeemActivation(new(created.Token, "short"))));

        using var session = f.Host.OpenSession();
        Assert.Contains("credential.activated", await AuditedAsync(session, created.User.Id));
    }

    [PostgreSqlFact]
    public async Task Changing_own_password_verifies_the_old_one_rotates_the_stamp_and_kills_pending_invitations()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("writer"), "Writer One")));
        f.Host.Actor.Current = null;
        await f.Host.InvokeAsync(new RedeemActivation(new(created.Token, "initial long password")));
        UserAccount account;
        using (var session = f.Host.OpenSession())
        {
            account = await session.GetAsync<UserAccount>(created.User.Id);
        }

        Assert.Equal("active", (await f.As<ProfileSecurity>(account, new GetProfileSecurity())).CredentialState);
        await AdministrationFixture.Denied(400, "Současné heslo není správné nebo lokální přihlášení není aktivní.", () => f.As<ChangedPassword>(account, new ChangePassword(new("wrong password", "changed long password"))));
        var changed = await f.As<ChangedPassword>(account, new ChangePassword(new("initial long password", "changed long password")));
        Assert.NotEqual(account.SecurityStamp, changed.SecurityStamp);
        Assert.Equal(account.Username, changed.Username);
        // The stale cookie snapshot is refused after the rotation.
        await AdministrationFixture.Denied(401, "Přihlášení vypršelo.", () => f.As<ProfileSecurity>(account, new GetProfileSecurity()));

        using var check = f.Host.OpenSession();
        Assert.Equal(0, await check.QueryOver<BuiltinAccountToken>().Where(x => x.UserId == account.Id).RowCountAsync());
        Assert.Contains("credential.password_changed", await AuditedAsync(check, account.Id));
    }

    [PostgreSqlFact]
    public async Task A_hub_account_sets_a_local_password_and_a_sign_in_name_without_ending_its_sessions()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        // Created by an external sign-in: the placeholder name is replaced, a name in use refused.
        await f.Host.ExecuteAsync($"UPDATE \"UserAccount\" SET \"Username\"='external_{Guid.NewGuid():N}' WHERE \"Id\"=:id", f.Member.Id);
        var member = await f.Account(f.Member.Id);
        var security = await f.As<ProfileSecurity>(member, new GetProfileSecurity());
        Assert.Equal(("none", null), (security.CredentialState, security.Username));
        await AdministrationFixture.Denied(400, () => f.As(member, new SetLocalPassword(new(" ", "first long password"))));
        await AdministrationFixture.Denied(400, () => f.As(member, new SetLocalPassword(new("external_mine", "first long password"))));
        await AdministrationFixture.Denied(409, "Přihlašovací jméno už používá jiný účet.", () => f.As(member, new SetLocalPassword(new(f.Admin.Username.ToUpperInvariant(), "first long password"))));
        var jana = TestLogin.For("Jana");
        await f.As(member, new SetLocalPassword(new(jana, "first long password")));

        // Same stamp: the session that set it, and every other one, stays signed in.
        security = await f.As<ProfileSecurity>(member, new GetProfileSecurity());
        Assert.Equal(("active", jana.ToLowerInvariant()), (security.CredentialState, security.Username));
        f.Host.Actor.Current = null;
        Assert.Equal(member.Id, (await f.Host.InvokeAsync<UserAccount>(new BuiltinLogin(jana.ToLowerInvariant(), "first long password"))).Id);
        // Only once: a further change goes through the current password.
        await AdministrationFixture.Denied(409, "Účet už lokální heslo má. Použijte změnu hesla.", () => f.As(member, new SetLocalPassword(new(null, "other long password"))));
    }

    [PostgreSqlFact]
    public async Task Activation_and_reset_are_one_use_hash_only_and_revoke_previous_sessions()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("writer"), "Writer")));
        Assert.Equal("pending", created.User.CredentialState);
        Assert.DoesNotContain(created.Token, created.ToString());
        Assert.Null(await PasswordHash(f, created.User.Id));

        async Task<bool> Redeem()
        {
            try
            {
                await f.As(null, new RedeemActivation(new(created.Token, "initial long password")));
                return true;
            }
            catch (ApiException e) when (e.Status == 400)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Redeem(), Redeem());
        Assert.Single(results, x => x);
        var user = await f.Account(created.User.Id);
        var current = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: user.Id)));
        Assert.Equal("active", current.CredentialState);
        await Assert.ThrowsAsync<ApiException>(() => f.As<ChangedPassword>(user, new ChangePassword(new("bad", "changed long password"))));
        var changed = await f.As<ChangedPassword>(user, new ChangePassword(new("initial long password", "changed long password")));
        Assert.NotEqual(user.SecurityStamp, changed.SecurityStamp);
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => f.As<ProfileSecurity>(user, new GetProfileSecurity()))).Status);
        current = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: user.Id)));
        var reset = await f.As<IssuedAccountToken>(f.Admin, new ResetPassword(user.Id, new(current.Version)));
        Assert.Null(await PasswordHash(f, user.Id));
        // The cookie the change handed back is itself revoked by the administrator reset.
        var refreshed = new UserAccount
        {
            Id = changed.Id,
            Username = changed.Username,
            DisplayName = changed.DisplayName,
            IsWikiAdmin = changed.IsWikiAdmin,
            IsActive = true,
            SecurityStamp = changed.SecurityStamp
        };
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => f.As<ProfileSecurity>(refreshed, new GetProfileSecurity()))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As<IssuedAccountToken>(f.Admin, new ResetPassword(user.Id, new(current.Version))))).Status);
        current = Assert.Single(await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers(Id: user.Id)));
        var replacement = await f.As<IssuedAccountToken>(f.Admin, new ResetPassword(user.Id, new(current.Version)));
        await Assert.ThrowsAsync<ApiException>(() => f.As(null, new RedeemActivation(new(reset.Token, "another long password"))));
        await f.Host.ExecuteAsync("UPDATE \"BuiltinAccountToken\" SET \"ExpiresAt\"=clock_timestamp()-interval '1 second' WHERE \"UserId\"=:id", user.Id);
        await Assert.ThrowsAsync<ApiException>(() => f.As(null, new RedeemActivation(new(replacement.Token, "another long password"))));
    }

    [PostgreSqlFact]
    public async Task Concurrent_demotion_cannot_remove_last_active_administrator_and_revoked_snapshot_is_denied()
    {
        await using var f = await AdministrationFixture.CreateAsync(LastAdministrator);
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new("second", "Second", true)));
        await f.As(null, new RedeemActivation(new(created.Token, "another long password")));
        var second = await f.Account(created.User.Id);
        var list = await f.As<IReadOnlyList<UserSummary>>(f.Admin, new ListUsers());
        var firstVersion = Assert.Single(list, u => u.Id == f.Admin.Id).Version;
        var secondVersion = Assert.Single(list, u => u.Id == second.Id).Version;

        async Task<bool> Demote(UserAccount actor, string version)
        {
            try
            {
                await f.As<UserSummary>(actor, new SaveUser(actor.Id, new(actor.DisplayName, true, false, version)));
                return true;
            }
            catch (ApiException e) when (e.Status == 409)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Demote(f.Admin, firstVersion), Demote(second, secondVersion));
        Assert.Single(results, x => x);
        Assert.Equal(1L, await Count(f, "SELECT count(*) FROM \"UserAccount\" WHERE \"IsActive\" AND \"IsWikiAdmin\""));
        var revoked = results[0] ? f.Admin : second;
        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => f.As<GroupSummary>(revoked, new AddGroup(new("Forbidden"))))).Status);
        Assert.Equal(0L, await Count(f, "SELECT count(*) FROM \"UserGroup\""));
    }

    /// <summary>The actions audited on the account's revisions (the audit holds every test's events).</summary>
    private static Task<IList<string>> AuditedAsync(NHibernate.ISession session, Guid userId) => session.CreateSQLQuery("""
        SELECT e."Action" FROM "AccessAuditEvent" e JOIN "UserAccountRevision" r ON r."Id"=e."AccountRevisionId" WHERE r."UserId"=:id
        """).SetParameter("id", userId).ListAsync<string>();

    private static async Task<string?> PasswordHash(AdministrationFixture fixture, Guid id)
    {
        using var session = fixture.Host.OpenSession();
        return await session.CreateSQLQuery("SELECT \"PasswordHash\" FROM \"BuiltinCredential\" WHERE \"UserId\"=:id")
            .SetParameter("id", id).UniqueResultAsync<string>();
    }

    private static async Task<long> Count(AdministrationFixture fixture, string sql)
    {
        using var session = fixture.Host.OpenSession();
        return await session.CreateSQLQuery(sql).UniqueResultAsync<long>();
    }

    private static async Task<long> Count(AdministrationFixture fixture, string sql, Guid id)
    {
        using var session = fixture.Host.OpenSession();
        return await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync<long>();
    }

    [PostgreSqlFact]
    public async Task Groups_are_unique_by_case_insensitive_name_and_renaming_is_compare_and_swap()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        // Group names are unique in the whole installation: the test's names carry a suffix of their own.
        var (engineering, development, support) = (TestLogin.For("Engineering"), TestLogin.For("Development"), TestLogin.For("Support"));
        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new($" {engineering} ")));
        Assert.Equal(engineering, group.Name);
        Assert.Equal("local", group.Source);
        await AdministrationFixture.Denied(409, "Skupina s tímto názvem již existuje.", () => f.As<GroupSummary>(f.Admin, new AddGroup(new(engineering.ToLowerInvariant()))));
        await AdministrationFixture.Denied(400, () => f.As<GroupSummary>(f.Admin, new AddGroup(new("   "))));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<GroupSummary>(f.Member, new AddGroup(new("Nope"))));

        var renamed = await f.As<GroupSummary>(f.Admin, new RenameGroup(group.Id, new(development, group.Version)));
        Assert.NotEqual(group.Version, renamed.Version);
        await AdministrationFixture.Denied(409, "Údaj se mezitím změnil. Obnovte seznam a změnu zopakujte.", () => f.As<GroupSummary>(f.Admin, new RenameGroup(group.Id, new("Stale", group.Version))));
        await AdministrationFixture.Denied(404, "Skupina nebyla nalezena.", () => f.As<GroupSummary>(f.Admin, new RenameGroup(Guid.NewGuid(), new("Ghost", renamed.Version))));

        var other = await f.As<GroupSummary>(f.Admin, new AddGroup(new(support)));
        await AdministrationFixture.Denied(409, "Skupina s tímto názvem již existuje.", () => f.As<GroupSummary>(f.Admin, new RenameGroup(other.Id, new(development.ToLowerInvariant(), other.Version))));

        var groups = (await f.As<IReadOnlyList<GroupSummary>>(f.Admin, new ListGroups())).Where(x => x.Id == group.Id || x.Id == other.Id);
        Assert.Equal([development, support], groups.Select(x => x.Name));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<IReadOnlyList<GroupSummary>>(f.Member, new ListGroups()));
    }

    [PostgreSqlFact]
    public async Task Membership_toggles_are_temporal_compare_and_swap_and_a_re_add_chains_on_the_previous_boundary()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new(TestLogin.For("Engineering"))));
        Assert.Empty(await f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, new ListGroupMembers(group.Id)));

        // Removing someone who is not a member passes compare-and-swap on a null version and writes nothing.
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(false, null)));
        Assert.Equal(0L, await Count(f, "SELECT count(*) FROM \"GroupMember\" WHERE \"GroupId\"=:id", group.Id));

        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, null)));
        var member = Assert.Single(await f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, new ListGroupMembers(group.Id)));
        Assert.Equal(f.Member.Id, member.Id);
        Assert.Equal(f.Member.Username, member.Username);

        // Adding an existing member with a null version is a stale write, not an idempotent no-op.
        await AdministrationFixture.Denied(409, () => f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, null))));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, member.Version)));
        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(false, member.Version)));
        Assert.Empty(await f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, new ListGroupMembers(group.Id)));

        await f.As(f.Admin, new SetGroupMembership(group.Id, f.Member.Id, new(true, null)));
        using (var session = f.Host.OpenSession())
        {
            var rows = await session.QueryOver<GroupMember>().Where(x => x.UserId == f.Member.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
            Assert.Equal(2, rows.Count);
            Assert.NotNull(rows[0].ActiveTo);
            Assert.True(rows[1].ActiveFrom >= rows[0].ActiveTo!.Value);
            Assert.Null(rows[1].ActiveTo);
        }

        await AdministrationFixture.Denied(404, "Skupina nebyla nalezena.", () => f.As(f.Admin, new SetGroupMembership(Guid.NewGuid(), f.Member.Id, new(true, null))));
        await AdministrationFixture.Denied(404, "Uživatel nebyl nalezen.", () => f.As(f.Admin, new SetGroupMembership(group.Id, Guid.NewGuid(), new(true, null))));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As(f.Member, new SetGroupMembership(group.Id, f.Member.Id, new(false, null))));
        await AdministrationFixture.Denied(403, "Tuto operaci může provést pouze správce wiki.", () => f.As<IReadOnlyList<GroupMemberSummary>>(f.Member, new ListGroupMembers(group.Id)));
    }

    [PostgreSqlFact]
    public async Task Space_access_lists_the_current_grants_for_space_admins_only()
    {
        // The account and group directory left this response (access audit 2026-09-28): candidates come from /api/access/candidates.
        await using var f = await AdministrationFixture.CreateAsync();
        await f.As<GroupSummary>(f.Admin, new AddGroup(new(TestLogin.For("Engineering"))));
        var access = await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId));
        var grant = Assert.Single(access.Grants);
        Assert.Equal(f.Admin.Id, grant.UserId);
        Assert.Equal("Admin", grant.Role);
        Assert.Equal(f.Admin.DisplayName, grant.Name);

        await AdministrationFixture.Denied(404, "Prostor nebyl nalezen.", () => f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(Guid.NewGuid())));
        await AdministrationFixture.Denied(404, "Prostor nebyl nalezen.", () => f.As<SpaceAccessSummary>(f.Member, new GetSpaceAccess(f.SpaceId)));
    }

    [PostgreSqlFact]
    public async Task Grant_changes_close_and_reopen_on_the_boundary_and_the_last_space_admin_cannot_be_removed()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new(TestLogin.For("Engineering"))));
        await AdministrationFixture.Denied(400, "Vyberte právě jednoho uživatele nebo skupinu.", () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, group.Id, "Reader", null))));
        await AdministrationFixture.Denied(400, "Vyberte právě jednoho uživatele nebo skupinu.", () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(null, null, "Reader", null))));
        await AdministrationFixture.Denied(400, "Neplatná role prostoru.", () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Owner", null))));
        await AdministrationFixture.Denied(404, "Uživatel nebyl nalezen.", () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(Guid.NewGuid(), null, "Reader", null))));
        await AdministrationFixture.Denied(404, "Skupina nebyla nalezena.", () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(null, Guid.NewGuid(), "Reader", null))));

        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Reader", null)));
        var reader = Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Member.Id);
        Assert.Equal("Reader", reader.Role);
        await AdministrationFixture.Denied(409, () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Writer", null))));

        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Writer", reader.Version)));
        var writer = Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Member.Id);
        Assert.Equal("Writer", writer.Role);
        using (var session = f.Host.OpenSession())
        {
            var rows = await session.QueryOver<SpaceGrant>().Where(x => x.UserId == f.Member.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(rows[0].ActiveTo, rows[1].ActiveFrom);
        }

        // Setting the same role again is a no-op that still validates the version.
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Writer", writer.Version)));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, null, writer.Version)));
        Assert.DoesNotContain((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Member.Id);

        var owner = Assert.Single((await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId))).Grants, x => x.UserId == f.Admin.Id);
        await AdministrationFixture.Denied(409, "Prostor musí mít alespoň jedno explicitní přiřazení správce.", () => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Admin.Id, null, "Reader", owner.Version))));
        await AdministrationFixture.Denied(404, "Prostor nebyl nalezen.", () => f.As(f.Member, new SetSpaceGrant(f.SpaceId, new(f.Member.Id, null, "Admin", null))));
    }

    [PostgreSqlFact]
    public async Task Group_and_grant_history_preserves_max_role_and_revocation_without_escalation()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        // The seeded fixture already owns the username "member", so the ported scenario creates its own account.
        var created = await f.As<CreatedBuiltinUser>(f.Admin, new CreateUser(new(TestLogin.For("writer"), "Member")));
        await f.As(null, new RedeemActivation(new(created.Token, "member long password")));
        var user = await f.Account(created.User.Id);
        var group = await f.As<GroupSummary>(f.Admin, new AddGroup(new(TestLogin.For("Engineering"))));
        var renamed = await f.As<GroupSummary>(f.Admin, new RenameGroup(group.Id, new(TestLogin.For("Development"), group.Version)));
        Assert.NotEqual(group.Version, renamed.Version);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As<GroupSummary>(f.Admin, new RenameGroup(group.Id, new("Stale", group.Version))))).Status);
        await f.As(f.Admin, new SetGroupMembership(group.Id, user.Id, new(true, null)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As(f.Admin, new SetGroupMembership(group.Id, user.Id, new(true, null))))).Status);
        var member = Assert.Single(await f.As<IReadOnlyList<GroupMemberSummary>>(f.Admin, new ListGroupMembers(group.Id)));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(null, group.Id, "Writer", null)));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(user.Id, null, "Reader", null)));
        Assert.Equal(SpaceRole.Writer, await f.As<SpaceRole>(user, new ProbeAccess(null, f.SpaceId, SpaceRole.Writer)));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<SpaceAccessSummary>(user, new GetSpaceAccess(f.SpaceId)))).Status);
        await f.As(f.Admin, new SetGroupMembership(group.Id, user.Id, new(false, member.Version)));
        Assert.Equal(SpaceRole.Reader, await f.As<SpaceRole>(user, new ProbeAccess(null, f.SpaceId, SpaceRole.Reader)));
        var access = await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId));
        var grant = Assert.Single(access.Grants, g => g.UserId == user.Id);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(user.Id, null, "Writer", grant.Version)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(user.Id, null, null, grant.Version))))).Status);
        access = await f.As<SpaceAccessSummary>(f.Admin, new GetSpaceAccess(f.SpaceId));
        grant = Assert.Single(access.Grants, g => g.UserId == user.Id);
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(user.Id, null, null, grant.Version)));
        await Assert.ThrowsAsync<ApiException>(() => f.As<SpaceRole>(user, new ProbeAccess(null, f.SpaceId, SpaceRole.Reader)));
        var owner = Assert.Single(access.Grants, g => g.UserId == f.Admin.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(f.Admin.Id, null, "Reader", owner.Version))))).Status);
        Assert.Equal(2L, await Count(f, "SELECT count(*) FROM \"UserGroupRevision\" WHERE \"GroupId\"=:id", group.Id));
        Assert.Equal(0L, await Count(f, "SELECT count(*) FROM \"GroupMember\" WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", user.Id));
        Assert.Equal(2L, await Count(f, "SELECT count(*) FROM \"SpaceGrant\" WHERE \"UserId\"=:id", user.Id));
        await f.As(f.Admin, new SetSpaceGrant(f.SpaceId, new(user.Id, null, "Admin", null)));
        var ownAccess = await f.As<SpaceAccessSummary>(user, new GetSpaceAccess(f.SpaceId));
        var groupGrant = Assert.Single(ownAccess.Grants, g => g.GroupId == group.Id);
        await f.As(user, new SetSpaceGrant(f.SpaceId, new(null, group.Id, "Reader", groupGrant.Version)));
        var otherSpace = Guid.NewGuid();
        await f.Host.SeedAsync(session => session.SaveAsync(new Space { Id = otherSpace, Name = "Other" }));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<SpaceAccessSummary>(user, new GetSpaceAccess(otherSpace)))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<GroupSummary>(user, new AddGroup(new("No global access"))))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<IReadOnlyList<UserSummary>>(user, new ListUsers()))).Status);
        var overlap = await Assert.ThrowsAsync<GenericADOException>(() => f.Host.ExecuteAsync(
            "INSERT INTO \"SpaceGrant\"(\"Id\",\"SpaceId\",\"UserId\",\"Role\",\"ActiveFrom\",\"ActiveTo\") SELECT gen_random_uuid(),\"SpaceId\",\"UserId\",1,\"ActiveFrom\"-interval '1 second',clock_timestamp()+interval '1 second' FROM \"SpaceGrant\" WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL LIMIT 1",
            user.Id));
        Assert.Equal("23P01", Assert.IsType<PostgresException>(overlap.InnerException).SqlState);
    }

    [PostgreSqlFact]
    public async Task Profile_account_reads_claims_from_the_message_and_saves_display_name_and_email_with_CAS()
    {
        await using var f = await AdministrationFixture.CreateAsync();
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var before = await f.As<ProfileAccount>(f.Member, new GetProfileAccount(seconds, "builtin"));
        Assert.Equal("external", before.Source);
        Assert.Null(before.Email);
        Assert.True(before.CanEditDisplayName);
        Assert.True(before.CanEditEmail);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(long.Parse(seconds, CultureInfo.InvariantCulture)), before.CurrentSessionAuthenticatedAt);
        Assert.Equal("builtin", before.CurrentSessionAuthenticationMethod);
        Assert.Null(before.LastSignInAt);
        Assert.Null((await f.As<ProfileAccount>(f.Member, new GetProfileAccount("not-a-number", null))).CurrentSessionAuthenticatedAt);

        var saved = await f.As<ProfileAccount>(f.Member, new SaveProfileAccount(seconds, "builtin",
            new("  Member renamed  ", "  member@example.com  ", before.Version)));
        Assert.Equal("Member renamed", saved.DisplayName);
        Assert.Equal("member@example.com", saved.Email);
        Assert.NotEqual(before.Version, saved.Version);

        await AdministrationFixture.Denied(409, "Údaj se mezitím změnil. Obnovte seznam a změnu zopakujte.", () => f.As<ProfileAccount>(f.Member, new SaveProfileAccount(seconds, "builtin",
            new("Stale", null, before.Version))));
        await AdministrationFixture.Denied(400, "Zadejte platný e-mail.", () => f.As<ProfileAccount>(f.Member, new SaveProfileAccount(seconds, "builtin",
            new("Bad mail", "not-an-email", saved.Version))));
        await AdministrationFixture.Denied(400, () => f.As<ProfileAccount>(f.Member, new SaveProfileAccount(seconds, "builtin",
            new("   ", null, saved.Version))));

        // An empty email clears the value, and a contact edit must not rotate the stamp: the session survives.
        var stampBefore = await Stamp(f, f.Member.Id);
        var cleared = await f.As<ProfileAccount>(f.Member, new SaveProfileAccount(seconds, "builtin",
            new("Member renamed", "   ", saved.Version)));
        Assert.Null(cleared.Email);
        Assert.Equal(stampBefore, await Stamp(f, f.Member.Id));
        Assert.Equal("Member renamed", (await f.As<ProfileAccount>(f.Member, new GetProfileAccount(null, null))).DisplayName);
    }
}
