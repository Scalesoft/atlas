using Npgsql;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.PageAccess.ItemAccessSeed;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>WP3 S5: the access tab's management API. The overview agrees with the policy (counts of distinct active people per
/// effective role, sources, groups, last activity), readers learn only their own row, the mode switch is CAS-protected and copies
/// the space defaults (D6), direct grants are bounded by the manager's own authority, and "Zobrazit jako" is the policy decision.</summary>
public sealed class PageAccessManagementTests
{
    [PostgreSqlFact]
    public async Task Overview_counts_people_per_effective_role_with_sources_groups_and_last_activity()
    {
        await using var f = await Fixture.CreateAsync(isolated: true);
        var page = await f.SharedPageAsync("Plán");
        await f.Grant(page.Id, f.PageWriter, 2, "now() + interval '30 days'");
        await f.GroupGrantAsync(page.Id, f.SupportGroup, 1);
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsActive\"=false WHERE \"Id\"=:id", f.InactiveMember.Id);
        await f.As<PageDto>(f.Reader, new GetPage(page.Id));
        await f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Plán", Fixture.Rich("Nový"), "Nový", page.Version)));

        var access = await f.As<PageAccessState>(f.Admin, new GetPageAccess(page.Id));

        Assert.True(access.CanManage);
        Assert.Equal(("inherit", "Plán"), (access.Mode, access.PageTitle));
        Assert.NotNull(access.ModeVersion);
        // Admin manages; Writer and the page writer edit; Reader and the active group member read. The inactive member is not counted.
        Assert.Equal((1, 2, 2), (access.Counts!.Manage, access.Counts.Edit, access.Counts.Read));
        Assert.Equal(0, access.Counts.Hidden);
        Assert.Equal(2, access.Counts.DefaultGrants); // Writer and Reader space defaults; the Admin grant is not copied (D4).
        Assert.Equal(new[] { "Admin", "Writer", "Writer", "Reader", "Reader" }, access.Items.Select(p => p.Role).ToArray());
        var admin = Assert.Single(access.Items, p => p.UserId == f.Admin.Id);
        Assert.Equal("spaceAdmin", Assert.Single(admin.Sources).Kind);
        Assert.NotNull(admin.LastEditedAt);
        var writer = Assert.Single(access.Items, p => p.UserId == f.Writer.Id);
        Assert.Equal(("space", "Writer"), (Assert.Single(writer.Sources).Kind, writer.Role));
        Assert.NotNull(writer.LastEditedAt);
        Assert.Null(writer.LastViewedAt);
        var reader = Assert.Single(access.Items, p => p.UserId == f.Reader.Id);
        Assert.NotNull(reader.LastViewedAt);
        Assert.Null(reader.LastEditedAt);
        var pageWriter = Assert.Single(access.Items, p => p.UserId == f.PageWriter.Id);
        var direct = Assert.Single(pageWriter.Sources);
        Assert.Equal(("page", "Writer", true), (direct.Kind, direct.Role, direct.Effective));
        Assert.NotNull(direct.ExpiresAt);
        Assert.NotNull(direct.Version);
        var member = Assert.Single(access.Items, p => p.UserId == f.Member.Id);
        Assert.Equal(("page", f.SupportGroupName), (Assert.Single(member.Sources).Kind, member.Sources[0].Name));
        Assert.DoesNotContain(access.Items, p => p.UserId == f.InactiveMember.Id || p.UserId == f.Outsider.Id);
        var group = Assert.Single(access.Groups!);
        Assert.Equal((f.SupportGroupName, 1, "Reader"), (group.Name, group.MemberCount, group.Role));
        Assert.Equal(0, access.ShareLinks!.Active);

        var filtered = await f.As<PageAccessState>(f.Admin, new GetPageAccess(page.Id, 0, 50, "podp"));
        Assert.Empty(filtered.Items);
        Assert.Single(filtered.Groups!);
        var paged = await f.As<PageAccessState>(f.Admin, new GetPageAccess(page.Id, 0, 2));
        Assert.True(paged.HasMore);
        Assert.Equal(5, paged.Counts!.Manage + paged.Counts.Edit + paged.Counts.Read);
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Readers_and_editors_learn_only_their_own_row_and_cannot_use_management()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Tajné");
        foreach (var account in new[] { f.PageReader, f.PageWriter })
        {
            var own = await f.As<PageAccessState>(account, new GetPageAccess(page.Id, 0, 50, "page"));
            Assert.False(own.CanManage);
            Assert.Empty(own.Items);
            Assert.Null(own.Counts);
            Assert.Null(own.Groups);
            Assert.Null(own.Mode);
            Assert.Null(own.ShareLinks);
            Assert.Equal(account.Id, own.MyAccess.UserId);
            Assert.Equal("page", Assert.Single(own.MyAccess.Sources).Kind);
            Assert.Equal(403, await f.StatusAsync<PageAccessExplanation>(account, new ExplainPageAccess(page.Id, f.PageManager.Id)));
            Assert.Equal(403, await f.StatusAsync<IReadOnlyList<AccessCandidate>>(account, new GetPageAccessCandidates(page.Id, "user", "")));
            Assert.Equal(403, await f.StatusAsync<PageAccessModeResult>(account, new SetPageAccessMode(page.Id, new(false, null))));
            // Reader escalation: a reader or editor may not grant itself (or anyone) more.
            Assert.Equal(403, await f.StatusAsync(account, new SetPageGrant(page.Id, new(account.Id, null, "Admin", await f.GrantVersionAsync(page.Id, account)))));
        }
        // A space Writer without a page grant does not even learn that the own-access page exists.
        Assert.Equal(404, await f.StatusAsync<PageAccessState>(f.Writer, new GetPageAccess(page.Id)));
        Assert.Equal(404, await f.StatusAsync(f.Writer, new SetPageGrant(page.Id, new(f.Writer.Id, null, "Reader", null))));
        Assert.Equal(1, await f.CurrentGrantRoleAsync(page.Id, f.PageReader));
        var managed = await f.As<PageAccessState>(f.PageManager, new GetPageAccess(page.Id));
        Assert.True(managed.CanManage);
        Assert.Equal("custom", managed.Mode);
    }

    [PostgreSqlFact]
    public async Task Turning_own_access_on_copies_the_space_defaults_and_back_closes_the_page_grants_with_cas()
    {
        await using var f = await Fixture.CreateAsync(isolated: true);
        var page = await f.SharedPageAsync("Plán");
        await f.Grant(page.Id, f.Reader, 1);        // as high as the space Reader default: kept as it is
        await f.Grant(page.Id, f.Writer, 1);        // lower than the space Writer default: replaced by the copy
        await f.Grant(page.Id, f.PageReader, 1);    // page only: kept
        var temporary = await f.Host.SeedUserAsync("temporary");
        await f.Host.ExecuteAsync($"""
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ExpiresAt","ActiveFrom")
            VALUES (gen_random_uuid(),'{f.SpaceId}',:id,2,now() + interval '10 days',now())
            """, temporary.Id);
        var before = await f.As<PageAccessState>(f.Admin, new GetPageAccess(page.Id));

        Assert.Equal(409, await f.StatusAsync<PageAccessModeResult>(f.Admin, new SetPageAccessMode(page.Id, new(true, "2020-01-01T00:00:00.0000000Z"))));
        var on = await f.As<PageAccessModeResult>(f.Admin, new SetPageAccessMode(page.Id, new(true, before.ModeVersion)));
        Assert.Equal("custom", on.Mode);
        Assert.NotEqual(before.ModeVersion, on.Version);
        // Same request again with the old version is refused; with the new one it is a no-op.
        Assert.Equal(409, await f.StatusAsync<PageAccessModeResult>(f.Admin, new SetPageAccessMode(page.Id, new(true, before.ModeVersion))));
        Assert.Equal(on.Version, (await f.As<PageAccessModeResult>(f.Admin, new SetPageAccessMode(page.Id, new(true, on.Version)))).Version);

        var after = await f.As<PageAccessState>(f.Admin, new GetPageAccess(page.Id));
        // D6: nobody who read or edited the page lost it, with the same roles.
        Assert.Equal((before.Counts!.Manage, before.Counts.Edit, before.Counts.Read), (after.Counts!.Manage, after.Counts.Edit, after.Counts.Read));
        Assert.Equal(2, await f.CurrentGrantRoleAsync(page.Id, f.Writer));
        Assert.Equal(1, await f.CurrentGrantRoleAsync(page.Id, f.Reader));
        Assert.Equal(1, await f.CurrentGrantRoleAsync(page.Id, f.PageReader));
        Assert.Null(await f.CurrentGrantRoleAsync(page.Id, f.Admin));
        Assert.Equal("page", Assert.Single(Assert.Single(after.Items, p => p.UserId == f.Reader.Id).Sources).Kind);
        // The copy keeps the validity of the space default.
        var copied = Assert.Single(Assert.Single(after.Items, p => p.UserId == temporary.Id).Sources);
        Assert.Equal(("page", "Writer"), (copied.Kind, copied.Role));
        Assert.NotNull(copied.ExpiresAt);

        // A new space member does not see the own-access page; the overview counts it as hidden and explains why.
        var newcomer = await f.Host.SeedUserAsync("newcomer");
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = newcomer.Id, Role = 1 }));
        var hidden = await f.As<PageAccessState>(f.Admin, new GetPageAccess(page.Id));
        Assert.Equal(1, hidden.Counts!.Hidden);
        var explained = await f.As<PageAccessExplanation>(f.Admin, new ExplainPageAccess(page.Id, newcomer.Id));
        Assert.Equal((false, false, "ownAccess"), (explained.CanView, explained.CanEdit, explained.Denial));
        // Returning to the space defaults: the page-only reader loses the page, the newcomer gains it.
        Assert.Equal(1, hidden.Counts.LoseOnReset);
        Assert.Equal(new[] { f.PageReader.DisplayName }, hidden.Counts.Losing);

        var off = await f.As<PageAccessModeResult>(f.Admin, new SetPageAccessMode(page.Id, new(false, on.Version)));
        Assert.Equal("inherit", off.Mode);
        Assert.Equal(0, await f.CurrentGrantCountAsync(page.Id));
        Assert.Equal(404, await f.StatusAsync<PageDto>(f.PageReader, new GetPage(page.Id)));
        Assert.True((await f.As<PageAccessExplanation>(f.Admin, new ExplainPageAccess(page.Id, newcomer.Id))).CanView);
        Assert.True((await f.As<PageDto>(f.Writer, new GetPage(page.Id))).CanEdit);
        // The handler's audit: two copies added; the replaced grant and the four current ones removed (the fixture's three bare SQL seeds
        // are not audited: no trigger since 3.6).
        Assert.Equal((2L, 5L),(await f.AuditCountAsync(page.Id, "resource_grant.added"), await f.AuditCountAsync(page.Id, "resource_grant.removed")));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Direct_grants_follow_cas_the_no_op_rule_and_are_capped_by_the_managers_own_authority()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Tajné", grants: false);
        await f.Grant(page.Id, f.PageManager, 3, "now() + interval '10 days'");
        var inFive = DateTimeOffset.UtcNow.AddDays(5);
        var inTwenty = DateTimeOffset.UtcNow.AddDays(20);

        // Bounded delegation: never unbounded and never beyond the manager's own expiry.
        Assert.Equal(403, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Reader", null))));
        Assert.Equal(403, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Admin", null, null, inTwenty))));
        await f.As(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Reader", null, null, inFive)));
        Assert.Equal(1, await f.CurrentGrantRoleAsync(page.Id, f.PageReader));
        var version = await f.GrantVersionAsync(page.Id, f.PageReader);

        // CAS and the no-op rule.
        Assert.Equal(409, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Writer", null, null, inFive))));
        await f.As(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Reader", version, null, inFive)));
        Assert.Equal(version, await f.GrantVersionAsync(page.Id, f.PageReader));
        await f.As(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Writer", version, null, inFive)));
        var changed = await f.GrantVersionAsync(page.Id, f.PageReader);
        Assert.NotEqual(version, changed);
        Assert.Equal(2, await f.CurrentGrantRoleAsync(page.Id, f.PageReader));
        Assert.True((await f.As<PageDto>(f.PageReader, new GetPage(page.Id))).CanEdit);

        // Groups, validation, unknown subjects.
        await f.As(f.PageManager, new SetPageGrant(page.Id, new(null, f.SupportGroup, "Reader", null, DateTimeOffset.UtcNow.AddDays(1), inFive)));
        var pendingGroup = Assert.Single((await f.As<PageAccessState>(f.PageManager, new GetPageAccess(page.Id))).Groups!, g => g.GroupId == f.SupportGroup);
        Assert.False(Assert.Single(pendingGroup.Sources).Effective);
        Assert.Null(pendingGroup.Role);
        Assert.Equal(400, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, f.SupportGroup, "Reader", null))));
        Assert.Equal(400, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Owner", changed))));
        Assert.Equal(400, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Reader", changed, inFive, inFive))));
        Assert.Equal(400, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Writer", changed, null, DateTimeOffset.UtcNow.AddMinutes(-1)))));
        Assert.Equal(404, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(Guid.NewGuid(), null, "Reader", null, null, inFive))));
        Assert.Equal(404, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(null, Guid.NewGuid(), "Reader", null, null, inFive))));

        // Revocation needs only current authority; removing a missing grant is idempotent.
        await f.As(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, null, changed)));
        Assert.Null(await f.CurrentGrantRoleAsync(page.Id, f.PageReader));
        await f.As(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, null, null)));
        Assert.Equal(404, await f.StatusAsync<PageDto>(f.PageReader, new GetPage(page.Id)));
        // Re-adding after the closed interval starts at the next boundary (no overlap with the history row).
        await f.As(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, "Reader", null, null, inFive)));
        Assert.Equal(1, await f.CurrentGrantRoleAsync(page.Id, f.PageReader));

        // The space Admin manages every page unbounded (D4); an expired manager grant manages nothing.
        await f.As(f.Admin, new SetPageGrant(page.Id, new(f.Writer.Id, null, "Admin", null)));
        await f.CloseGrantsAsync(page.Id, f.PageManager);
        Assert.Equal(404, await f.StatusAsync(f.PageManager, new SetPageGrant(page.Id, new(f.PageReader.Id, null, null, null))));
        // Every grant a handler added; the fixture's bare SQL seed is not audited (no trigger since 3.6).
        Assert.Equal(5L, await f.AuditCountAsync(page.Id, "resource_grant.added"));
    }

    [PostgreSqlFact]
    public async Task View_as_is_the_policy_decision_for_that_account()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Tajné");
        var wikiAdmin = await f.Host.SeedUserAsync("wiki", true);

        var reader = await f.As<PageAccessExplanation>(f.Admin, new ExplainPageAccess(page.Id, f.PageReader.Id));
        Assert.Equal((true, false, "Reader", (string?)null), (reader.CanView, reader.CanEdit, reader.Role, reader.Denial));
        Assert.Equal("page", Assert.Single(reader.Sources).Kind);
        var writer = await f.As<PageAccessExplanation>(f.PageManager, new ExplainPageAccess(page.Id, f.PageWriter.Id));
        Assert.Equal((true, true), (writer.CanView, writer.CanEdit));
        var admin = await f.As<PageAccessExplanation>(f.PageManager, new ExplainPageAccess(page.Id, f.Admin.Id));
        Assert.Equal(("Admin", "spaceAdmin"), (admin.Role, Assert.Single(admin.Sources).Kind));
        var global = await f.As<PageAccessExplanation>(f.PageManager, new ExplainPageAccess(page.Id, wikiAdmin.Id));
        Assert.Equal((true, true, "global"), (global.CanView, global.CanEdit, Assert.Single(global.Sources).Kind));
        var spaceReader = await f.As<PageAccessExplanation>(f.PageManager, new ExplainPageAccess(page.Id, f.Reader.Id));
        Assert.Equal((false, "ownAccess"), (spaceReader.CanView, spaceReader.Denial));
        var outsider = await f.As<PageAccessExplanation>(f.PageManager, new ExplainPageAccess(page.Id, f.Outsider.Id));
        Assert.Equal((false, "none"), (outsider.CanView, outsider.Denial));
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsActive\"=false WHERE \"Id\"=:id", f.PageWriter.Id);
        var inactive = await f.As<PageAccessExplanation>(f.PageManager, new ExplainPageAccess(page.Id, f.PageWriter.Id));
        Assert.Equal((false, "inactive"), (inactive.CanView, inactive.Denial));
        Assert.Empty(inactive.Sources);
        Assert.Equal(404, await f.StatusAsync<PageAccessExplanation>(f.PageManager, new ExplainPageAccess(page.Id, Guid.NewGuid())));

        var draft = await f.As<PageDto>(f.Admin, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Koncept", Fixture.Rich("K"), "K")));
        var privateDraft = await f.As<PageAccessExplanation>(f.Admin, new ExplainPageAccess(draft.Id, f.Writer.Id));
        Assert.Equal((false, "privateDraft"), (privateDraft.CanView, privateDraft.Denial));

        // Searched by the whole login: on the shared database "page" also finds other tests' users beyond the directory's bound.
        var found = new List<AccessCandidate>();
        foreach (var account in new[] { f.PageManager, f.PageReader })
            found.AddRange(await f.As<IReadOnlyList<AccessCandidate>>(f.PageManager, new GetPageAccessCandidates(page.Id, "user", account.Username)));
        Assert.Equal(new[] { f.PageManager.Username, f.PageReader.Username }.Order().ToArray(), found.Select(u => u.Detail).Order().ToArray());
        var groups = await f.As<IReadOnlyList<AccessCandidate>>(f.PageManager, new GetPageAccessCandidates(page.Id, "group", f.SupportGroupName[3..].ToUpperInvariant()));
        var support = Assert.Single(groups, g => g.Id == f.SupportGroup);
        Assert.Equal((f.SupportGroupName, "2"), (support.Name, support.Detail));
        Assert.Equal(400, await f.StatusAsync<IReadOnlyList<AccessCandidate>>(f.PageManager, new GetPageAccessCandidates(page.Id, "space", "")));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlConnection m_connection;

        private Fixture(AtlasTestHost host, NpgsqlConnection connection)
        {
            Host = host;
            m_connection = connection;
        }

        public AtlasTestHost Host { get; }
        public UserAccount Admin { get; private set; } = null!;
        public UserAccount Writer { get; private set; } = null!;
        public UserAccount Reader { get; private set; } = null!;
        public UserAccount PageReader { get; private set; } = null!;
        public UserAccount PageWriter { get; private set; } = null!;
        public UserAccount PageManager { get; private set; } = null!;
        public UserAccount Member { get; private set; } = null!;
        public UserAccount InactiveMember { get; private set; } = null!;
        public UserAccount Outsider { get; private set; } = null!;
        public Guid SpaceId { get; private set; }
        public Guid SupportGroup { get; private set; }
        public string SupportGroupName { get; private set; } = null!;

        public static string Rich(string text) =>
            "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

        /// <param name="isolated">A test that reads the page's whole people list or role counts needs its own database: they include every WikiAdmin of the installation.</param>
        public static async Task<Fixture> CreateAsync(bool isolated = false)
        {
            var host = isolated ? await AtlasTestHost.CreateIsolatedAsync("atlas_page_access_mgmt_") /* own database: the people list and role counts include every WikiAdmin of the installation */ : await AtlasTestHost.CreateAsync("atlas_page_access_mgmt_");
            var f = new Fixture(host, await OpenAsync(host))
            {
                Admin = await host.SeedUserAsync("admin"),
                Writer = await host.SeedUserAsync("writer"),
                Reader = await host.SeedUserAsync("reader"),
                PageReader = await host.SeedUserAsync("pagereader"),
                PageWriter = await host.SeedUserAsync("pagewriter"),
                PageManager = await host.SeedUserAsync("pagemanager"),
                Member = await host.SeedUserAsync("member"),
                InactiveMember = await host.SeedUserAsync("inactive"),
                Outsider = await host.SeedUserAsync("outsider"),
            };
            f.SpaceId = await host.SeedSpaceAsync("space", (f.Admin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
            // Unique: the group directory is the installation's and shows at most 20 names.
            f.SupportGroupName = TestLogin.For("Podpora");
            f.SupportGroup = await host.SeedGroupAsync(f.SupportGroupName, f.Member, f.InactiveMember);
            return f;
        }

        public async Task<PageDto> SharedPageAsync(string title)
        {
            var page = await As<PageDto>(Admin, new CreatePage(new CreatePageRequest(SpaceId, null, title, Rich(title), title)));
            return await As<PageDto>(Admin, new PublishPage(page.Id, new(page.Version)));
        }

        public async Task<PageDto> OwnPageAsync(string title, bool grants = true)
        {
            var page = await SharedPageAsync(title);
            await Host.ExecuteAsync("UPDATE \"Page\" SET \"OwnAccess\"=true WHERE \"Id\"=:id", page.Id);
            if (grants)
            {
                await Grant(page.Id, PageReader, 1);
                await Grant(page.Id, PageWriter, 2);
                await Grant(page.Id, PageManager, 3);
            }
            return page;
        }

        public Task Grant(Guid pageId, UserAccount user, int role, string expiresAt = "NULL") =>
            GrantAsync(m_connection, pageId, user.Id, null, role, "NULL", expiresAt);

        public Task GroupGrantAsync(Guid pageId, Guid groupId, int role) =>
            GrantAsync(m_connection, pageId, null, groupId, role);

        public Task CloseGrantsAsync(Guid pageId, UserAccount user) =>
            ExecuteAsync(m_connection, $"UPDATE \"ResourceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"PageId\"='{pageId}' AND \"UserId\"='{user.Id}' AND \"ActiveTo\" IS NULL");

        public async Task<int?> CurrentGrantRoleAsync(Guid pageId, UserAccount user) =>
            await ExecuteAsync(m_connection, $"SELECT \"Role\" FROM \"ResourceGrant\" WHERE \"PageId\"='{pageId}' AND \"UserId\"='{user.Id}' AND \"ActiveTo\" IS NULL") as int?;

        public async Task<long> CurrentGrantCountAsync(Guid pageId) =>
            (long)(await ExecuteAsync(m_connection, $"SELECT count(*) FROM \"ResourceGrant\" WHERE \"PageId\"='{pageId}' AND \"ActiveTo\" IS NULL"))!;

        public async Task<long> AuditCountAsync(Guid pageId, string action) =>
            (long)(await ExecuteAsync(m_connection, $"""
                SELECT count(*) FROM "AccessAuditEvent" e JOIN "ResourceGrant" g ON g."Id"=e."ResourceGrantId"
                WHERE g."PageId"='{pageId}' AND e."Action"='{action}'
                """))!;

        public async Task<string?> GrantVersionAsync(Guid pageId, UserAccount user) =>
            await ExecuteAsync(m_connection, $"SELECT \"ActiveFrom\" FROM \"ResourceGrant\" WHERE \"PageId\"='{pageId}' AND \"UserId\"='{user.Id}' AND \"ActiveTo\" IS NULL")
                is DateTime value ? AdministrationVersion.Format(value) : null;

        public Task<T> As<T>(UserAccount actor, object message)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync<T>(message);
        }

        public Task As(UserAccount actor, object message)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync(message);
        }

        public async Task<int> StatusAsync<T>(UserAccount actor, object message)
        {
            try
            {
                await As<T>(actor, message);
                return 200;
            }
            catch (ApiException error)
            {
                return error.Status;
            }
        }

        public async Task<int> StatusAsync(UserAccount actor, object message)
        {
            try
            {
                await As(actor, message);
                return 200;
            }
            catch (ApiException error)
            {
                return error.Status;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await m_connection.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}
