namespace Atlas.Server.Features.ModuleSettings;

/// <summary>Module settings are wiki-administrator surface. The decision uses the account read back under the actor lock.</summary>
public sealed class ModuleSettingsAccess
{
    private readonly IContentAccess m_access;
    private readonly SpaceRoles m_roles;

    public ModuleSettingsAccess(IContentAccess access, SpaceRoles roles)
    {
        m_access = access;
        m_roles = roles;
    }

    public async Task<UserAccount> RequireWikiAdminAsync(CancellationToken ct)
        => await m_roles.RequireWikiAdminAsync(await m_access.LockActorAsync(null, ct), ct, "Nastavení modulů spravuje správce wiki.");
}
