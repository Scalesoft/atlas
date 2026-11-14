using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Administration.Commands;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Persistence;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Commands;

public sealed class HubBindingHandler(ISession session, AdministrationAccess access, IAtlasLocks locks,
    ISecurityChangeGuard guard, AtlasOidcOptions oidc, AccessChanges changes)
{
    public async Task<HubSettingsView> Handle(CommitHubGroup command, CancellationToken ct)
    {
        var actor = await access.LockAdministrationAsync(ct);
        var current = await HubReads.CurrentAsync(session, ct) ?? throw new ApiException(409, "Připojení se mezitím změnilo.");
        AdministrationVersion.Expect(current.ActiveFrom, command.ExpectedVersion);
        if (current.Id != command.ConfigurationId) throw new ApiException(409, "Připojení se mezitím změnilo.");
        if (await session.QueryOver<HubGroupBinding>().Where(x => x.ConnectionId == current.ConnectionId && x.ExternalGroupId == command.Group.Id).RowCountAsync(ct) != 0)
            throw new ApiException(409, "Tato skupina Hubu je již připojená.");
        await locks.SetActorAsync(actor.Id, ct);
        var id = Guid.NewGuid();
        var name = await HubGroupNames.AvailableAsync(session, command.Group.Name, id, ct);
        var group = new UserGroup { Id = id, Name = name };
        await session.SaveAsync(group, ct);
        await GroupHistory.RecordAsync(session, group, actor.Id, ct);
        var binding = new HubGroupBinding { GroupId = id, ConnectionId = current.ConnectionId, ExternalGroupId = command.Group.Id };
        await session.SaveAsync(binding, ct);
        await HubHistory.BindingAsync(session, binding, actor.Id, ct);
        changes.Changed();
        await session.FlushAsync(ct);
        return await HubSettingsReader.ReadAsync(session, oidc, ct);
    }

    public async Task<HubSettingsView> Handle(ChangeHubBinding command, CancellationToken ct)
    {
        var actor = await access.LockAdministrationAsync(ct);
        var row = await session.GetAsync<HubGroupBinding>(command.GroupId, ct) ?? throw new ApiException(404, "Skupina není připojená k Hubu.");
        var version = await HubSettingsReader.BindingVersionAsync(session, row.GroupId, ct);
        AdministrationVersion.Expect(version, command.Request.ExpectedVersion);
        if (row.Enabled == command.Request.Enabled && row.GrantsWikiAdmin == command.Request.GrantsWikiAdmin)
            return await HubSettingsReader.ReadAsync(session, oidc, ct);
        if (row.GrantsWikiAdmin != command.Request.GrantsWikiAdmin || command.Request.Enabled && row.GrantsWikiAdmin)
        {
            await guard.RequireAsync(actor, new SecurityChange("hub.group_admin", row.GroupId), ct);
            await HubSettingsReader.RequireRecoveryAsync(session, oidc, ct);
        }
        await locks.SetActorAsync(actor.Id, ct);
        row.Enabled = command.Request.Enabled;
        row.GrantsWikiAdmin = command.Request.GrantsWikiAdmin;
        await HubHistory.BindingAsync(session, row, actor.Id, ct);
        changes.Changed();
        await session.FlushAsync(ct);
        return await HubSettingsReader.ReadAsync(session, oidc, ct);
    }
}

internal static class HubGroupNames
{
    public static async Task<string> AvailableAsync(ISession session, string externalName, Guid groupId, CancellationToken ct)
    {
        var name = Rules.Required(Rules.CleanLine(externalName), "Název skupiny Hubu");
        name = name[..Math.Min(name.Length, 100)];
        if (!await GroupNames.ExistsAsync(session, name, groupId, ct)) return name;
        // Same display names never merge local or upstream identities.
        var suffix = " · Hub " + groupId.ToString("N");
        return name[..Math.Min(name.Length, 100 - suffix.Length)] + suffix;
    }
}
