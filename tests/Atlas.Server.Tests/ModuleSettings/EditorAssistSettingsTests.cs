using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.ModuleSettings.Commands;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.ModuleSettings;

/// <summary>S7b: the wiki-level module "Návrhy při psaní". Rule-based editor suggestions are on until a WikiAdmin
/// turns them off; every signed-in user reads the result as features.editorAssist on the current-user query.</summary>
public sealed class EditorAssistSettingsTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Editor_assist_is_on_by_default_and_a_wiki_admin_switches_it_with_compare_and_swap()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync(privateBecause: "switches the module of the installation (EditorAssistSetting)");
        var initial = await f.As<EditorAssistSettingsView>(f.Admin, new GetEditorAssistSettings());
        Assert.True(initial.Enabled);
        Assert.Null(initial.Version);
        Assert.True((await f.As<UserDto>(f.Member, new GetCurrentUser())).Features.EditorAssist);

        var off = await f.As<EditorAssistSettingsView>(f.Admin, new SaveEditorAssistSettings(new(false, null)));
        Assert.False(off.Enabled);
        Assert.NotNull(off.Version);
        Assert.False((await f.As<UserDto>(f.Member, new GetCurrentUser())).Features.EditorAssist);
        Assert.False((await f.As<UserDto>(f.Admin, new GetCurrentUser())).Features.EditorAssist);
        await ModuleSettingsFixture.Denied(409, () => f.As<EditorAssistSettingsView>(f.Admin, new SaveEditorAssistSettings(new(true, null))));

        var on = await f.As<EditorAssistSettingsView>(f.Admin, new SaveEditorAssistSettings(new(true, off.Version)));
        Assert.True(on.Enabled);
        Assert.NotEqual(off.Version, on.Version);
        Assert.True((await f.As<UserDto>(f.Member, new GetCurrentUser())).Features.EditorAssist);
        using var session = f.Host.OpenSession();
        // Temporal: every change is kept as a closed interval, exactly one revision is current.
        Assert.Equal(2, await session.QueryOver<Atlas.Server.Features.ModuleSettings.Domain.EditorAssistSetting>().RowCountAsync());
        Assert.Equal(1, await session.QueryOver<Atlas.Server.Features.ModuleSettings.Domain.EditorAssistSetting>().Where(x => x.ActiveTo == null).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Only_a_wiki_admin_reads_or_changes_the_module_and_the_module_is_listed()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync();
        await ModuleSettingsFixture.Denied(403, () => f.As<EditorAssistSettingsView>(f.Member, new GetEditorAssistSettings()));
        await ModuleSettingsFixture.Denied(403, () => f.As<EditorAssistSettingsView>(f.Member, new SaveEditorAssistSettings(new(false, null))));
        Assert.True((await f.As<UserDto>(f.Member, new GetCurrentUser())).Features.EditorAssist);

        var module = Assert.Single(await f.As<IReadOnlyList<ModuleSettingsDescriptor>>(f.Admin, new ListModules()), x => x.Id == "editor-assist");
        Assert.Equal(("Návrhy při psaní", "editor-assist", false), (module.Title, module.SettingsUi, module.ShowInSetup));
    }
}
