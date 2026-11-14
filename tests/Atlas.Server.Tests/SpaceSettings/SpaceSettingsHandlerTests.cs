using Atlas.Server.Features.SpaceSettings.Commands;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Domain;
using Atlas.Server.Features.SpaceSettings.Queries;
using Xunit;

namespace Atlas.Server.Tests.SpaceSettings;

public sealed class SpaceSettingsHandlerTests
{
    [PostgreSqlFact]
    public async Task Get_returns_current_metadata_for_space_admin_and_hides_space_from_others()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var dto = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId));
        Assert.Equal("Original", dto.Name);
        Assert.Equal(DateTimeKind.Utc, dto.Version.Kind);
        Assert.Equal(dto, await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId)));
        var outsider = await f.Host.SeedUserAsync("x");
        await Denied(404, () => f.As<SpaceSettingsDto>(outsider, new GetSpaceSettings(f.SpaceId)));
        await Denied(404, () => f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(Guid.NewGuid())));
    }

    [PostgreSqlFact]
    public async Task Save_writes_temporal_revision_with_actor_and_leaves_pages_and_grants_alone()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var original = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId));
        var saved = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new SaveSpaceSettings(f.SpaceId, new(" Engineering ", "compass", "green", " Documentation ", "None", original.Version)));
        Assert.Equal("Engineering", saved.Name);
        Assert.Equal("Documentation", saved.Description);
        Assert.True(saved.Version > original.Version);
        Assert.Equal(DateTimeKind.Utc, saved.Version.Kind);
        using var session = f.Host.OpenSession();
        var revisions = await session.QueryOver<SpaceMetadataRevision>().Where(x => x.SpaceId == f.SpaceId).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(revisions[1].ActiveFrom, revisions[0].ActiveTo);
        Assert.Null(revisions[1].ActiveTo);
        Assert.Equal(f.SpaceAdmin.Id, revisions[1].ChangedBy);
        Assert.Equal("Engineering", (await session.GetAsync<Space>(f.SpaceId)).Name);
        Assert.Equal(3, await session.CreateSQLQuery("SELECT \"Role\" FROM \"SpaceGrant\" WHERE \"UserId\"=:actor AND \"ActiveTo\" IS NULL").SetParameter("actor", f.SpaceAdmin.Id).UniqueResultAsync<int>());
        var unchanged = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new SaveSpaceSettings(f.SpaceId, new(saved.Name, saved.Icon, saved.Color, saved.Description, saved.PinPolicy, saved.Version)));
        Assert.Equal(saved.Version, unchanged.Version);
        // The pin policy is a setting like the name: its own revision and a new CAS version.
        var pinned = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new SaveSpaceSettings(f.SpaceId, new(saved.Name, saved.Icon, saved.Color, saved.Description, "Required", saved.Version)));
        Assert.Equal("Required", pinned.PinPolicy);
        Assert.True(pinned.Version > saved.Version);
        using var check = f.Host.OpenSession();
        Assert.Equal(SpacePinPolicy.Required, (await CurrentRevisionAsync(check, f.SpaceId)).PinPolicy);
        Assert.Equal(SpacePinPolicy.Required, (await check.GetAsync<Space>(f.SpaceId)).PinPolicy);
    }

    [PostgreSqlFact]
    public async Task Save_CAS_allows_one_concurrent_winner_and_rejects_invalid_input_without_partial_updates()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var initial = await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId));
        async Task<bool> Save(string name)
        {
            try
            {
                await f.As<SpaceSettingsDto>(f.Admin, new SaveSpaceSettings(f.SpaceId, new(name, "blocks", "coral", "", "None", initial.Version)));
                return true;
            }
            catch (ApiException e) when (e.Status == 409)
            {
                Assert.Equal("space_settings_conflict", e.Code);
                return false;
            }
        }
        Assert.Single(await Task.WhenAll(Save("First"), Save("Second")), result => result);
        var latest = await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId));
        foreach (var bad in new SaveSpaceSettingsRequest[] { new("", "book-open", "blue", "", "None", latest.Version), new("Name", "script", "blue", "", "None", latest.Version), new("Name", "book-open", "purple", "", "None", latest.Version), new("Name", "book-open", "blue", new string('a', 2001), "None", latest.Version), new("Name", "book-open", "blue", "", "1", latest.Version) })
        {
            await Denied(400, () => f.As<SpaceSettingsDto>(f.Admin, new SaveSpaceSettings(f.SpaceId, bad)));
        }
        Assert.Equal(latest, await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId)));
    }

    [PostgreSqlFact]
    public async Task Save_keeps_the_archive_icon_of_a_space_imported_from_Wiki_js()
    {
        // The Wiki.js import gives its space "archive", an icon no picker offers; its settings must still save.
        await using var f = await SpaceSettingsFixture.CreateAsync();
        // As the import writes it: through the entity, whose metadata history comes with it (ContentRowEffects).
        await f.Host.SeedAsync(async session => (await session.GetAsync<Atlas.Server.Features.Spaces.Domain.Space>(f.SpaceId)).Icon = "archive");
        var imported = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId));
        Assert.Equal("archive", imported.Icon);
        var saved = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new SaveSpaceSettings(f.SpaceId, new(imported.Name, imported.Icon, imported.Color, "Imported from Wiki.js", imported.PinPolicy, imported.Version)));
        Assert.Equal("archive", saved.Icon);
        Assert.Equal("Imported from Wiki.js", saved.Description);
    }

    [PostgreSqlFact]
    public async Task Reader_writer_no_access_and_revoked_admin_cannot_read_or_change_settings()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var original = await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId));
        var save = new SaveSpaceSettings(f.SpaceId, new("Changed", "book-open", "blue", "", "None", original.Version));
        foreach (var role in new[] { 1, 2 })
        {
            await f.Execute("""
                WITH closed AS (
                  UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=:id AND "ActiveTo" IS NULL
                  RETURNING "SpaceId","UserId","ActiveTo")
                INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","GroupId","Role","ActiveFrom","ActiveTo")
                SELECT gen_random_uuid(),"SpaceId","UserId",NULL,
                """ + role + """
                ,"ActiveTo",NULL FROM closed
                """, f.SpaceAdmin.Id);
            await Denied(403, () => f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId)));
            await Denied(403, () => f.As<SpaceSettingsDto>(f.SpaceAdmin, save));
        }
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.SpaceAdmin.Id);
        await Denied(404, () => f.As<SpaceSettingsDto>(f.SpaceAdmin, save));
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=:id", f.Admin.Id);
        await Denied(404, () => f.As<SpaceSettingsDto>(f.Admin, save));
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Admin.Id);
        await Denied(401, () => f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId)));
    }

    [PostgreSqlFact]
    public async Task Create_requires_wiki_admin_normalizes_appearance_and_grants_groups_that_exist()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var groupId = await f.Host.SeedGroupAsync("Team");
        await Denied(403, () => f.As<CreatedSpaceDto>(f.SpaceAdmin, new CreateSpace(new("Nope", null, null, null))));
        var created = await f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new(" Docs ", "script", "purple", [new(groupId, "Writer")])));
        Assert.Equal("Docs", created.Name);
        Assert.Equal("book-open", created.Icon);
        Assert.Equal("blue", created.Color);
        Assert.Equal("Admin", created.Role);
        using var check = f.Host.OpenSession();
        var grants = await check.QueryOver<SpaceGrant>().Where(g => g.SpaceId == created.Id && g.ActiveTo == null).ListAsync();
        Assert.True((await check.QueryOver<SpacePin>().Where(p => p.SpaceId == created.Id && p.UserId == f.Admin.Id && p.ActiveTo == null).SingleOrDefaultAsync())?.Pinned);
        Assert.Equal(2, grants.Count);
        Assert.Contains(grants, g => g.UserId == f.Admin.Id && g.Role == 3);
        Assert.Contains(grants, g => g.GroupId == groupId && g.Role == 2);
        var revision = await CurrentRevisionAsync(check, created.Id);
        Assert.Equal("Docs", revision.Name);
        Assert.Equal(f.Admin.Id, revision.ChangedBy);
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, [new(Guid.NewGuid(), "Reader")]))));
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, [new(groupId, "Owner")]))));
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, [new(groupId, "Reader"), new(groupId, "Writer")]))));
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, [new(Guid.Empty, "Reader")]))));
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, [null]))));
        var tooMany = Enumerable.Range(0, 101).Select(_ => new CreateSpaceGroupGrant(Guid.NewGuid(), "Reader")).ToArray();
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, tooMany))));
    }

    [PostgreSqlFact]
    public async Task Create_takes_the_description_and_pin_policy_of_the_settings_form_with_its_rules()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var created = await f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Provoz", null, null, null, " Runbooky ", "Default")));
        Assert.Equal(("Runbooky", "Default"), (created.Description, created.PinPolicy));
        var settings = await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(created.Id));
        Assert.Equal("Runbooky", settings.Description);
        Assert.Equal("Default", settings.PinPolicy);
        var plain = await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings((await f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Prázdný", null, null, null)))).Id));
        Assert.Equal(("", "None"), (plain.Description, plain.PinPolicy));
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, null, null, "Always"))));
        await Denied(400, () => f.As<CreatedSpaceDto>(f.Admin, new CreateSpace(new("Bad", null, null, null, new string('x', 2001)))));
    }

    private static Task<SpaceMetadataRevision> CurrentRevisionAsync(NHibernate.ISession session, Guid spaceId)
        => session.QueryOver<SpaceMetadataRevision>().Where(x => x.SpaceId == spaceId && x.ActiveTo == null).SingleOrDefaultAsync();

    private static async Task Denied(int status, Func<Task> action)
        => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
}
