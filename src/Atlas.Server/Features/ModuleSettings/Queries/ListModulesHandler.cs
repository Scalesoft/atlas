using Atlas.Server.Features.ModuleSettings.Contracts;

namespace Atlas.Server.Features.ModuleSettings.Queries;

public sealed class ListModulesHandler
{
    private readonly ModuleSettingsCatalog m_catalog;
    private readonly ModuleSettingsAccess m_access;

    public ListModulesHandler(ModuleSettingsCatalog catalog, ModuleSettingsAccess access)
    {
        m_catalog = catalog;
        m_access = access;
    }

    public async Task<IReadOnlyList<ModuleSettingsDescriptor>> Handle(ListModules query, CancellationToken ct)
    {
        await m_access.RequireWikiAdminAsync(ct);
        return m_catalog.Items;
    }
}
