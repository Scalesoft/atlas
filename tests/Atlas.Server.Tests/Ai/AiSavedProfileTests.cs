using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Ai.Settings.SavedProfiles;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiSavedProfileTests
{
    [PostgreSqlFact]
    public async Task Library_edits_stay_offline_until_explicit_activation_with_both_versions()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Chat("saved-test-credential");
        var saved = await f.As<SavedProfileView>(f.Admin, new SaveSavedProfile("chat", null, new("Primary", request, null)));
        Assert.False((await f.As<AiProfileView>(f.Admin, new GetAiProfile("chat"))).Configured);
        Assert.Equal(0, f.Changes.Count);
        var active = await f.As<AiProfileView>(f.Admin, new ActivateSavedProfile("chat", saved.Id, new(null, saved.Version)));
        var edited = await f.As<SavedProfileView>(f.Admin, new SaveSavedProfile("chat", saved.Id,
            new("Primary", request with { Model = "next-model", ApiKey = null }, saved.Version)));
        Assert.Equal("gpt-test", (await f.As<AiProfileView>(f.Admin, new GetAiProfile("chat"))).Model);
        Assert.False(edited.IsActive);
        await AiSettingsFixture.Denied(409, () => f.As<AiProfileView>(f.Admin,
            new ActivateSavedProfile("chat", saved.Id, new(active.Version, saved.Version))));
        await AiSettingsFixture.Denied(409, () => f.As<AiProfileView>(f.Admin,
            new ActivateSavedProfile("chat", saved.Id, new(null, edited.Version))));
        await AiSettingsFixture.Denied(409, () => f.As<bool>(f.Admin,
            new DeleteSavedProfile("chat", saved.Id, edited.Version)));
        var applied = await f.As<AiProfileView>(f.Admin, new ActivateSavedProfile("chat", saved.Id, new(active.Version, edited.Version)));
        Assert.Equal("next-model", applied.Model);
        Assert.True(applied.HasApiKey);
        Assert.Equal(2, f.Changes.Count);
        Assert.Equal(applied.Version, (await f.As<SavedProfileList>(f.Admin, new ListSavedProfiles("chat"))).ActiveVersion);
        using var session = f.Host.OpenSession();
        var history = await session.QueryOver<AiSavedProfile>().OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(history[0].ActiveTo, history[1].ActiveFrom);
    }

    [PostgreSqlFact]
    public async Task Saved_discovery_and_tests_pin_the_selected_secret_and_reject_stale_edits()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Chat("active-test-credential");
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile("chat", request));
        var saved = await f.As<SavedProfileView>(f.Admin, new SaveSavedProfile("chat", null,
            new("Spare", request with { ApiKey = "spare-test-credential" }, null)));
        var discovery = new SavedModelDiscoveryRequest(request.Provider, request.Location, request.BaseUrl, null, false, saved.Version);
        var selected = await f.As<AiProfileSnapshot>(f.Admin, new PrepareSavedModelDiscovery("chat", saved.Id, discovery));
        Assert.Equal("spare-test-credential", f.Unprotect(selected.ProtectedApiKey));
        var fresh = await f.As<AiProfileSnapshot>(f.Admin, new PrepareSavedModelDiscovery("chat", null, discovery with { ExpectedSavedVersion = null }));
        Assert.Equal("", fresh.ProtectedApiKey);
        var moved = await f.As<AiProfileSnapshot>(f.Admin, new PrepareSavedModelDiscovery("chat", saved.Id,
            discovery with { BaseUrl = "https://different.example/v1" }));
        Assert.Equal("", moved.ProtectedApiKey);
        var test = await f.As<AiProfileSnapshot>(f.Admin, new BeginSavedProfileTest("chat", saved.Id, saved.Version));
        Assert.Equal("spare-test-credential", f.Unprotect(test.ProtectedApiKey));
        await f.As<SavedProfileView>(f.Admin, new SaveSavedProfile("chat", saved.Id,
            new("Spare", request with { Model = "edited", ApiKey = null }, saved.Version)));
        await AiSettingsFixture.Denied(409, () => f.As<bool>(f.Admin, new CompleteSavedProfileTest("chat", saved.Id, test.RevisionId)));
        await AiSettingsFixture.Denied(409, () => f.As<AiProfileSnapshot>(f.Admin, new PrepareSavedModelDiscovery("chat", saved.Id, discovery)));
        Assert.Equal("gpt-test", (await f.As<AiProfileView>(f.Admin, new GetAiProfile("chat"))).Model);
    }

    [PostgreSqlFact]
    public async Task Snapshot_preserves_current_key_and_names_are_unique_per_role()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var request = AiSettingsFixture.Chat("snapshot-fixture-credential");
        var active = await f.As<AiProfileView>(f.Admin, new SaveAiProfile("chat", request));
        var saved = await f.As<SavedProfileView>(f.Admin, new SnapshotCurrentProfile("chat", new("  Main  ", active.Version)));
        Assert.Equal("Main", saved.Name);
        Assert.True(saved.IsActive);
        Assert.Single(await f.RevisionsAsync("chat"));
        await AiSettingsFixture.Denied(409, () => f.As<SavedProfileView>(f.Admin,
            new SaveSavedProfile("chat", null, new("main", request, null))));
        await AiSettingsFixture.Denied(403, () => f.As<SavedProfileList>(f.Reader, new ListSavedProfiles("chat")));
        await AiSettingsFixture.Denied(403, () => f.As<SavedProfileView>(f.Reader,
            new SaveSavedProfile("chat", saved.Id, new("Main", request, saved.Version))));
        await f.As<AiProfileView>(f.Admin, new SaveAiProfile("chat", request with { Model = "elsewhere", ExpectedVersion = active.Version }));
        Assert.True(await f.As<bool>(f.Admin, new DeleteSavedProfile("chat", saved.Id, saved.Version)));
        Assert.Empty((await f.As<SavedProfileList>(f.Admin, new ListSavedProfiles("chat"))).Items);
        Assert.Equal(2, (await f.RevisionsAsync("chat")).Count);
    }

    [PostgreSqlFact]
    public async Task Upgrade_backfills_the_current_revision_without_changing_assignment_or_ciphertext()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var active = await f.As<AiProfileView>(f.Admin, new SaveAiProfile("chat", AiSettingsFixture.Chat("upgrade-fixture-credential")));
        var revision = Assert.Single(await f.RevisionsAsync("chat"));
        // Remove only the new schema addition in this disposable fixture to model the immediately preceding release.
        await using (var sql = new Npgsql.NpgsqlConnection(f.Host.ConnectionString))
        {
            await sql.OpenAsync();
            await using var remove = new Npgsql.NpgsqlCommand("""
                DROP TABLE "AiSavedProfile";
                DELETE FROM "SchemaVersion" WHERE "Version"=2026100870;
                """, sql);
            await remove.ExecuteNonQueryAsync();
        }
        await Atlas.Server.Platform.Persistence.AtlasMigrations.MigrateAsync(f.Host.ConnectionString);
        var saved = Assert.Single((await f.As<SavedProfileList>(f.Admin, new ListSavedProfiles("chat"))).Items);
        Assert.True(saved.IsActive);
        Assert.Equal("Výchozí Chat", saved.Name);
        Assert.Equal(active, await f.As<AiProfileView>(f.Admin, new GetAiProfile("chat")));
        var retained = Assert.Single(await f.RevisionsAsync("chat"));
        Assert.Equal(revision.Id, retained.Id);
        Assert.Equal(revision.ProtectedApiKey, retained.ProtectedApiKey);
    }

}
