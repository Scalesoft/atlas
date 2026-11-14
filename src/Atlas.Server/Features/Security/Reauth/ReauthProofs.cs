using System.Security.Claims;
using NHibernate;
using Atlas.Server.Features.Auth.Persistence;
using Atlas.Server.Features.Security.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Security.Reauth;

/// <summary>
/// Consumes re-authentication proofs: the challenge row FOR UPDATE, then the binding (account, stamp, Atlas session, operation,
/// target), the verification and its two-minute lifetime at the database clock, then ConsumedAt and the reauth.consumed audit event in
/// the caller's transaction. The change or release the proof authorizes commits with it or not at all: a rolled-back change leaves
/// the proof unconsumed. The same proof never succeeds twice, not even concurrently (the row lock serializes both, the second sees
/// ConsumedAt). Security changes present the proof in the <see cref="Header"/> header.
/// <para>
/// Security changes also pass inside the re-authentication window (owner decision 28. 9.): up to ten minutes after any verified
/// re-authentication (password or provider, not a self-test) of the same account, session and security stamp, found on the server.
/// A new sign-in, a sign-out or a stamp rotation (password change, reset, deactivation, privilege change) ends it. Such a change
/// records reauth.window_used with the challenge that opened the window, so the audit names the fresh sign-in that authorized it.
/// The window never serves a value release: <see cref="TryConsumeAsync"/> still wants its one proof.
/// </para>
/// </summary>
public sealed class ReauthProofs
{
    public const string Header = "X-Atlas-Reauth";

    private readonly ISession m_session;
    private readonly IHttpContextAccessor m_http;
    private readonly FreshAuthMethods m_methods;

    public ReauthProofs(ISession session, IHttpContextAccessor http, FreshAuthMethods methods)
    {
        m_session = session;
        m_http = http;
        m_methods = methods;
    }

    /// <summary>The Atlas session of the request (atlas_session claim); null without a cookie sign-in.</summary>
    public string? SessionId => SessionOf(m_http.HttpContext);

    public static string? SessionOf(HttpContext? http) => http?.User.FindFirstValue("atlas_session");

    /// <summary>Lets a security change proceed: a presented proof for exactly this target is consumed (reauth.consumed); otherwise
    /// an open window of the session is used (reauth.window_used). Throws <see cref="ReauthRefusal"/> reauth_required, or
    /// fresh_auth_unsupported when the account has no method that can prove freshness.</summary>
    public async Task RequireSecurityAsync(UserAccount actor, ReauthTarget target, CancellationToken ct)
    {
        var presented = m_http.HttpContext?.Request.Headers[Header].ToString();
        if (Guid.TryParse(presented, out var id) && await TryConsumeAsync(actor, SessionId, ReauthChallenge.SecurityOperation, target, id, ct))
        {
            return;
        }
        var window = await WindowAsync(actor, SessionId, ct);
        if (window is null)
        {
            var method = await m_methods.ChooseAsync(actor, false, ct);
            throw ReauthRefusal.For(method is not null && method.Support != FreshAuthSupport.Unsupported);
        }
        // A verified, non-selftest proof (WindowAsync) whose open window this change used; was atlas_record_reauth_window_audit.
        await AccessAuditLog.RecordAsync(m_session, actor.Id, "reauth.window_used", AccessAuditLog.ReauthChallenge, window.Id, ct);
    }

    /// <summary>Consumes the proof when it is valid for exactly this account, session, operation and target; false otherwise.</summary>
    public async Task<bool> TryConsumeAsync(UserAccount actor, string? sessionId, string operation, ReauthTarget target, Guid proofId, CancellationToken ct)
    {
        if (sessionId is null)
        {
            return false;
        }
        var challenge = await m_session.GetAsync<ReauthChallenge>(proofId, LockMode.Upgrade, ct);
        var now = await DatabaseClock.NowAsync(m_session, ct);
        if (challenge is null || !challenge.BelongsTo(actor, sessionId) || !challenge.Proves(operation, target, now))
        {
            return false;
        }
        challenge.ConsumedAt = now;
        // The proof and the change it authorizes share the transaction; was atlas_record_reauth_audit.
        await AccessAuditLog.RecordAsync(m_session, actor.Id, "reauth.consumed", AccessAuditLog.ReauthChallenge, challenge.Id, ct);
        return true;
    }

    /// <summary>When the open window of the caller's session ends (GET /api/security/me); null when none is open.</summary>
    public async Task<DateTime?> WindowEndAsync(UserAccount actor, CancellationToken ct)
    {
        return (await WindowAsync(actor, SessionId, ct))?.WindowEnd();
    }

    /// <summary>The latest verification that keeps a window open now: same account, session and stamp, verified no later than the
    /// database clock (a change whose transaction began before the verification committed is not covered) and less than
    /// <see cref="ReauthChallenge.WindowLifetime"/> ago.</summary>
    private async Task<ReauthChallenge?> WindowAsync(UserAccount actor, string? sessionId, CancellationToken ct)
    {
        if (sessionId is null)
        {
            return null;
        }
        var now = await DatabaseClock.NowAsync(m_session, ct);
        var since = now - ReauthChallenge.WindowLifetime;
        var verified = await m_session.QueryOver<ReauthChallenge>()
            .Where(c => c.UserId == actor.Id && c.SessionId == sessionId && c.Operation != ReauthChallenge.SelfTestOperation && c.WindowClosedAt == null)
            .And(c => c.VerifiedAt > since && c.VerifiedAt <= now)
            .OrderBy(c => c.VerifiedAt).Desc
            .ListAsync(ct);
        return verified.FirstOrDefault(c => c.BelongsTo(actor, sessionId) && c.WindowEnd() > now);
    }
}
