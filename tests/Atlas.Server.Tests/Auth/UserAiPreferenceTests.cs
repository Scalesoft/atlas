using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Tests.Platform;
using NHibernate;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class UserAiPreferenceTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Preferences_are_per_user_server_side_and_turning_suggestions_off_clears_editor_assist()
    {
        // Own database: the test switches editor assist off for the whole installation (the current EditorAssistSetting row), which
        // every GetCurrentUser on a shared host would then report (CurrentUserQueryTests failed beside it).
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_user_ai_pref_");
        var alice = await host.SeedUserAsync("alice", isWikiAdmin: true);
        var bob = await host.SeedUserAsync("bob");

        host.Actor.Current = alice;
        var initial = await host.InvokeAsync<AiPreferencesView>(new GetAiPreferences());
        Assert.Equal("on", initial.EditorSuggestions);
        Assert.Empty(initial.DisabledSuggestionTypes);
        Assert.Null(initial.Version);
        var me = await host.InvokeAsync<UserDto>(new GetCurrentUser());
        Assert.True(me.Features.EditorAssist);
        Assert.Null(me.Features.DisabledSuggestionTypes);

        var saved = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("off", ["issue", "contact"], null)));
        Assert.Equal("off", saved.EditorSuggestions);
        Assert.Equal(["contact", "issue"], saved.DisabledSuggestionTypes);
        Assert.NotNull(saved.Version);
        me = await host.InvokeAsync<UserDto>(new GetCurrentUser());
        Assert.False(me.Features.EditorAssist);
        Assert.Equal(["contact", "issue"], me.Features.DisabledSuggestionTypes!);

        // Another account is untouched.
        host.Actor.Current = bob;
        Assert.Equal("on", (await host.InvokeAsync<AiPreferencesView>(new GetAiPreferences())).EditorSuggestions);
        Assert.True((await host.InvokeAsync<UserDto>(new GetCurrentUser())).Features.EditorAssist);

        // 'rules' behaves as 'on' in phase 2; the per-user choice can only narrow the wiki-level switch.
        await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("rules", [], null)));
        Assert.True((await host.InvokeAsync<UserDto>(new GetCurrentUser())).Features.EditorAssist);
        host.Actor.Current = alice;
        await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", [], saved.Version)));
        Assert.True((await host.InvokeAsync<UserDto>(new GetCurrentUser())).Features.EditorAssist);
        await host.SeedAsync(session => session.SaveAsync(new EditorAssistSetting { Id = Guid.NewGuid(), Enabled = false, ChangedById = alice.Id, ActiveFrom = DateTime.UtcNow }));
        Assert.False((await host.InvokeAsync<UserDto>(new GetCurrentUser())).Features.EditorAssist);
        Assert.Equal("on", (await host.InvokeAsync<AiPreferencesView>(new GetAiPreferences())).EditorSuggestions);
    }

    [PostgreSqlFact]
    public async Task Saving_requires_the_current_version_validates_values_and_keeps_history()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_user_ai_pref_cas_");
        var user = await host.SeedUserAsync("reader");
        host.Actor.Current = user;
        var first = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("rules", ["issue"], null)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", [], null))))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", [], "2000-01-01T00:00:00.0000000Z"))))).Status);
        foreach (var bad in new SaveAiPreferencesRequest[] { new("maybe", [], first.Version), new("on", ["nope"], first.Version), new("on", ["issue", "issue"], first.Version) })
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(bad)))).Status);
        var second = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", ["structure", "link-mention"], first.Version)));
        Assert.NotEqual(first.Version, second.Version);
        // Saving the same values again is a no-op that keeps the version.
        var same = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", ["link-mention", "structure"], second.Version)));
        Assert.Equal(second.Version, same.Version);
        using var session = host.OpenSession();
        Assert.Equal(2, session.CreateSQLQuery("""SELECT CAST(count(*) AS int) FROM "UserAiPreference" WHERE "UserId"=:id""").SetParameter("id", user.Id).UniqueResult<int>());
        Assert.Equal(1, session.CreateSQLQuery("""SELECT CAST(count(*) AS int) FROM "UserAiPreference" WHERE "UserId"=:id AND "ActiveTo" IS NULL""").SetParameter("id", user.Id).UniqueResult<int>());
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Answer_preferences_default_to_auto_and_keep_explicit_choices_on_partial_updates()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_user_ai_pref_answer_");
        var user = await host.SeedUserAsync("reader");
        var other = await host.SeedUserAsync("other");
        host.Actor.Current = user;
        var initial = await host.InvokeAsync<AiPreferencesView>(new GetAiPreferences());
        Assert.Equal(("auto", "context"), (initial.SearchAnswer, initial.AssistantScope));

        // A request without the new fields (an old client) stores the defaults.
        var first = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("rules", ["issue"], null)));
        Assert.Equal(("auto", "context"), (first.SearchAnswer, first.AssistantScope));

        var second = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("rules", ["issue"], first.Version, "ask", "wiki")));
        Assert.Equal(("ask", "wiki"), (second.SearchAnswer, second.AssistantScope));
        Assert.NotEqual(first.Version, second.Version);

        // An old client changing only editor suggestions keeps the answer preferences.
        var third = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", [], second.Version)));
        Assert.Equal(("ask", "wiki"), (third.SearchAnswer, third.AssistantScope));
        // Partial update: only the scope.
        var fourth = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", [], third.Version, null, "context")));
        Assert.Equal(("ask", "context"), (fourth.SearchAnswer, fourth.AssistantScope));
        // The same values are a no-op that keeps the version.
        var same = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", [], fourth.Version, "ask", null)));
        Assert.Equal(fourth.Version, same.Version);
        var loaded = await host.InvokeAsync<AiPreferencesView>(new GetAiPreferences());
        Assert.Equal(("ask", "context"), (loaded.SearchAnswer, loaded.AssistantScope));

        foreach (var bad in new SaveAiPreferencesRequest[] { new("on", [], fourth.Version, "sometimes"), new("on", [], fourth.Version, null, "everywhere") })
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(bad)))).Status);

        var never = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("on", [], fourth.Version, "never")));
        var partial = await host.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(new("rules", [], never.Version)));
        Assert.Equal("never", partial.SearchAnswer);

        host.Actor.Current = other;
        var untouched = await host.InvokeAsync<AiPreferencesView>(new GetAiPreferences());
        Assert.Equal(("auto", "context"), (untouched.SearchAnswer, untouched.AssistantScope));
    }
}
