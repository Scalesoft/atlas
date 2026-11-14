using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Queries;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiSettingsAccessTests
{
    [PostgreSqlFact]
    public async Task Only_wiki_admins_read_or_change_ai_settings()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        foreach (var actor in new[] { f.SpaceAdmin, f.Reader })
        {
            await AiSettingsFixture.Denied(403, () => f.As<AiSwitchView>(actor, new GetAiSwitch()));
            await AiSettingsFixture.Denied(403, () => f.As<AiSwitchView>(actor, new SaveAiSwitch(new(true, null))));
            // Authorization comes before the kind: an unknown kind tells a non-admin nothing either.
            await AiSettingsFixture.Denied(403, () => f.As<AiProfileView>(actor, new GetAiProfile("unknown")));
            await AiSettingsFixture.Denied(403, () => f.As<AiProfileView>(actor, new SaveAiProfile(AiProfileKind.Embeddings, AiSettingsFixture.Embeddings())));
            await AiSettingsFixture.Denied(403, () => f.As<AiFeaturesView>(actor, new GetAiFeatures()));
            await AiSettingsFixture.Denied(403, () => f.As<AiFeaturesView>(actor, new SaveAiFeatures(new([], null))));
        }
        Assert.Equal(0, f.Changes.Count);

        // The wiki admin: AI is off until switched on (AI-24), the kind is checked, and the module is listed outside setup.
        var off = await f.As<AiSwitchView>(f.Admin, new GetAiSwitch());
        Assert.Equal(new AiSwitchView(false, null), off);
        var on = await f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(true, null)));
        Assert.True(on.Enabled);
        await AiSettingsFixture.Denied(409, () => f.As<AiSwitchView>(f.Admin, new SaveAiSwitch(new(false, null))));
        Assert.Equal(on, await f.As<AiSwitchView>(f.Admin, new GetAiSwitch()));
        await AiSettingsFixture.Denied(404, () => f.As<AiProfileView>(f.Admin, new GetAiProfile("unknown")));
        var module = Assert.Single(await f.As<IReadOnlyList<ModuleSettingsDescriptor>>(f.Admin, new ListModules()), x => x.Id == "ai");
        Assert.Equal(("AI", "ai", false), (module.Title, module.SettingsUi, module.ShowInSetup));
    }
}
