using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.OpenIdConnect.Commands;

public sealed class ResolveExternalIdentityHandler
{
    private const int DisplayNameMaxLength = 160;
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly AccessChanges m_accessChanges;
    private readonly IJobRequests m_jobs;

    public ResolveExternalIdentityHandler(ISession session, IAtlasLocks locks, AccessChanges accessChanges, IJobRequests jobs)
    {
        m_session = session;
        m_locks = locks;
        m_accessChanges = accessChanges;
        m_jobs = jobs;
    }

    public async Task<UserAccount> Handle(ResolveExternalIdentity command, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        // Serialize first-login/link races for this exact issuer+subject; collision merely serializes
        // unrelated identities. The DB unique index is the final invariant. The key text is the one
        // the legacy service hashed, and IAtlasLocks uses the same seed, so the lock value is unchanged.
        await m_locks.AcquireAdvisoryAsync(command.Issuer + "\n" + command.Subject, ct);
        var hubConnection = await Atlas.Server.Features.Hub.Persistence.HubReads.CurrentAsync(m_session, ct);
        var hubManaged = hubConnection?.Issuer == command.Issuer;
        if (hubManaged && command.LinkUserId is { } hubUserId)
        {
            await m_locks.AcquireAdvisoryAsync($"hub-user:{hubConnection!.ConnectionId:N}:{hubUserId:N}", ct);
            if (await m_session.QueryOver<ExternalIdentity>().Where(x => x.UserId == hubUserId && x.Issuer == command.Issuer && x.ActiveTo == null && x.Subject != command.Subject).RowCountAsync(ct) != 0)
                throw new ApiException(409, "Účet už používá jinou aktivní identitu tohoto Hubu.");
        }
        var identity = await m_session.QueryOver<ExternalIdentity>()
            .Where(x => x.Issuer == command.Issuer && x.Subject == command.Subject && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        UserAccount? user;
        if (identity is not null)
        {
            if (command.LinkUserId is { } owner && identity.UserId != owner)
            {
                throw new ApiException(409, "Externí identita je již propojená s jiným účtem.");
            }
            user = await m_session.GetAsync<UserAccount>(identity.UserId, ct) ?? throw new ApiException(403, "Přihlášení není povoleno.");
        }
        else
        {
            if (command.LinkUserId is null && !command.AllowSignup)
            {
                throw new ApiException(403, "Požádejte správce o zřízení a propojení účtu.");
            }
            user = command.LinkUserId is { } linkId
                ? await m_session.GetAsync<UserAccount>(linkId, ct)
                : NewExternalAccount(command.DisplayName, command.Email);
            // Never match an existing account by name or e-mail: only the subject binds an identity.
            if (user is null || !user.IsActive || command.LinkUserId is not null && user.SecurityStamp != command.LinkSecurityStamp)
            {
                throw new ApiException(403, "Účet již není dostupný.");
            }
            if (command.LinkUserId is null)
            {
                await m_session.SaveAsync(user, ct);
                // Nobody is signed in yet: the identity provider's callback creates the account.
                await AccountHistory.RecordAsync(m_session, user, null, ct);
                await m_jobs.RequestAsync(AccountContactsJob.JobId, "Nový účet", ct: ct);
            }
            await m_session.SaveAsync(new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Issuer = command.Issuer,
                Subject = command.Subject,
                ActiveFrom = DateTime.UtcNow
            }, ct);
        }
        await m_session.FlushAsync(ct);
        await m_locks.LockAccountForUpdateAsync(user.Id, ct);
        await m_session.RefreshAsync(user, ct);
        if (!user.IsActive || command.LinkUserId is not null && user.SecurityStamp != command.LinkSecurityStamp)
        {
            throw new ApiException(403, "Účet není aktivní.");
        }
        var wasAdmin = user.IsWikiAdmin;
        if (!hubManaged && command.GrantsWikiAdmin && (!user.IsWikiAdmin || user.OidcAdminIssuer is not null))
        {
            user.IsWikiAdmin = true;
            user.OidcAdminIssuer = command.Issuer;
        }
        else if (!hubManaged && !command.GrantsWikiAdmin && user.OidcAdminIssuer == command.Issuer)
        {
            user.IsWikiAdmin = false;
            user.OidcAdminIssuer = null;
        }
        if (wasAdmin != user.IsWikiAdmin)
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            m_accessChanges.Changed();
        }
        await AccountHistory.RecordAsync(m_session, user, null, ct);
        return user;
    }

    private static UserAccount NewExternalAccount(string? displayName, string? email)
    {
        // Synced from the identity provider: control and bidirectional characters are removed, not refused (Rules.CleanLine).
        var trimmed = displayName is null ? null : Rules.CleanLine(displayName);
        return new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = UserAccount.ExternalUsernamePrefix + Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(trimmed) ? "Nový uživatel" : trimmed[..Math.Min(trimmed.Length, DisplayNameMaxLength)],
            IsActive = true,
            IsWikiAdmin = false,
            // The profile's rule (SaveProfileAccountHandler): an address that is not one exactly is left out.
            Email = email?.Trim() is { Length: > 0 and <= 254 } address && System.Net.Mail.MailAddress.TryCreate(address, out var parsed) && parsed.Address == address ? address : null
        };
    }
}
