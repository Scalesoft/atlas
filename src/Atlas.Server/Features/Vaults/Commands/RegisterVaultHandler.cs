using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Domain;
using Atlas.Server.Features.Vaults.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Vaults.Commands;

public sealed class RegisterVaultHandler
{
    private readonly ISession m_session;
    private readonly VaultAccess m_access;
    private readonly VaultGrantSnapshots m_grants;

    public RegisterVaultHandler(ISession session, VaultAccess access, VaultGrantSnapshots grants)
    {
        m_session = session;
        m_access = access;
        m_grants = grants;
    }

    public async Task<VaultView> Handle(RegisterVault command, CancellationToken ct)
    {
        var actor = await m_access.RequireSpaceAdminAsync(command.SpaceId, ct);
        var name = VaultRegistrations.RequireName(command.Name);
        var fileName = VaultRegistrations.RequireFileName(command.FileName);
        var location = await VaultRegistrations.RequireLocationAsync(m_session, command.LocationId, ct);
        await VaultRegistrations.EnsureUniqueAsync(m_session, command.SpaceId, location.Id, name, fileName, ct);
        var read = command.Read;
        var now = DateTime.UtcNow;
        var vault = new Vault
        {
            Id = command.VaultId,
            SpaceId = command.SpaceId,
            Name = name,
            LocationId = location.Id,
            FileName = fileName,
            ProtectedKdbxPassword = command.ProtectedKdbxPassword,
            State = VaultStates.Unlocked,
            LastSnapshotSha256 = read.Sha256,
            LastRefreshAt = now,
            CreatedBy = actor.Id,
            CreatedAt = now,
        };
        await m_session.SaveAsync(vault, ct);
        await VaultRegistry.SyncAsync(m_session, vault.Id, read.Entries ?? [], now, ct);
        try
        {
            await m_session.FlushAsync(ct);
        }
        catch (NHibernate.Exceptions.GenericADOException error) when (error.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent registration of the same file or name won between the check above and this insert.
            throw new ApiException(409, "Tento soubor nebo název trezoru mezitím zaregistroval jiný správce.", "vault_file_registered");
        }
        return await VaultReads.ViewAsync(m_session, m_grants, actor, vault, ct);
    }
}
