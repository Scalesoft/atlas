using Microsoft.AspNetCore.Identity;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Recovery;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.Setup.Domain;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Setup.Commands;

public sealed class CompleteSetupHandler
{
    private const long SetupLockKey = 731145011;
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly ServerAccessTokens m_tokens;
    private readonly IPasswordHasher<UserAccount> m_hasher;
    private readonly BuiltinPasswordPolicy m_passwordPolicy;
    private readonly IJobRequests m_jobs;

    public CompleteSetupHandler(ISession session, IAtlasLocks locks, ServerAccessTokens tokens, IPasswordHasher<UserAccount> hasher, BuiltinPasswordPolicy passwordPolicy, IJobRequests jobs)
    {
        m_session = session;
        m_locks = locks;
        m_tokens = tokens;
        m_hasher = hasher;
        m_passwordPolicy = passwordPolicy;
        m_jobs = jobs;
    }

    public async Task<UserAccount> Handle(CompleteSetup command, CancellationToken ct)
    {
        // Validation reads the workspace in its own scope and transaction, and holds the singleton
        // bootstrap gate while it does. That is safe here and only here: this transaction is still
        // open but holds no lock yet, so a second concurrent setup waits on the semaphore rather
        // than on a database lock. The advisory lock below is taken only after the gate is released.
        await m_tokens.ValidateBootstrapAsync(command.BootstrapToken, ct);
        m_passwordPolicy.Validate(command.Password);
        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = Rules.Required(command.Username, "Uživatelské jméno", 100).ToLowerInvariant(),
            DisplayName = Rules.Required(command.DisplayName, "Jméno"),
            IsWikiAdmin = true
        };
        var workspace = new Workspace { Name = Rules.Required(command.WorkspaceName, "Název wiki"), CreatedAt = DateTime.UtcNow };
        await m_locks.AcquireAdvisoryAsync(SetupLockKey, ct);
        if (await m_session.GetAsync<Workspace>(1, ct) is not null)
        {
            throw new ApiException(409, "Wiki již byla nastavena.");
        }
        await m_session.SaveAsync(workspace, ct);
        await m_session.SaveAsync(user, ct);
        // Nobody is signed in yet: the founder's account has no author.
        await AccountHistory.RecordAsync(m_session, user, null, ct);
        await m_session.SaveAsync(new BuiltinCredential { UserId = user.Id, PasswordHash = m_hasher.HashPassword(user, command.Password) }, ct);
        await m_session.SaveAsync(new UserAppearancePreference { UserId = user.Id }, ct);
        await m_session.FlushAsync(ct);
        // The founder is the actor of every row written from here on, including trigger history.
        await m_locks.SetActorAsync(user.Id, ct);
        await m_session.SaveAsync(new ModuleOnboarding { Id = Guid.NewGuid(), WorkspaceId = 1, ActiveFrom = DateTime.UtcNow }, ct);
        // The starter space is where every new reader begins, so it is pinned for everyone (each can still unpin it).
        var space = new Space { Id = Guid.NewGuid(), Name = "Začínáme", Icon = "book-open", Color = "blue", PinPolicy = SpacePinPolicy.Default };
        await m_session.SaveAsync(space, ct);
        var grant = new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space.Id, UserId = user.Id, Role = (int)SpaceRole.Admin, ActiveFrom = DateTime.UtcNow };
        await m_session.SaveAsync(grant, ct);
        await AccessAuditLog.OpenedAsync(m_session, AccessAuditLog.SpaceGrant, [grant.Id], user.Id, ct);
        await DatabaseStatistics.RefreshAccessAsync(m_session, ct);
        await m_jobs.RequestAsync(AccountContactsJob.JobId, "Zakladatel wiki", ct: ct);
        return user;
    }
}
