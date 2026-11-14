using System.Security.Cryptography;
using System.Text;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;
using Atlas.Server.Features.Auth.Recovery.Domain;
using Atlas.Server.Features.Auth.Domain;

namespace Atlas.Server.Features.Administration.Commands;

/// <summary>
/// One-time codes for the local password (owner decision 28. 9.): an invitation for a new account that has never set a password
/// lasts <see cref="InvitationLifetime"/>, a password reset <see cref="ResetLifetime"/>. Both are single-use, replace any pending code,
/// clear the password and rotate the stamp.
/// </summary>
internal static class AccountTokens
{
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromMinutes(30);

    /// <summary>The invitation of a new account.</summary>
    public static Task<IssuedAccountToken> InviteAsync(ISession session, Guid userId, Guid actorId, CancellationToken ct) =>
        IssueAsync(session, userId, BuiltinAccountToken.InvitationPurpose, actorId, ct);

    /// <summary>A reset by an administrator. An account that never activated (its pending code is still an unused invitation, even
    /// an expired one) gets a new invitation instead; any account that has had a password gets a reset.</summary>
    public static async Task<IssuedAccountToken> ResetAsync(ISession session, Guid userId, Guid actorId, CancellationToken ct)
    {
        var invited = await session.QueryOver<BuiltinAccountToken>()
            .Where(t => t.UserId == userId && t.ConsumedAt == null && t.Purpose == BuiltinAccountToken.InvitationPurpose)
            .RowCountAsync(ct) > 0;
        return await IssueAsync(session, userId, invited ? BuiltinAccountToken.InvitationPurpose : BuiltinAccountToken.ResetPurpose, actorId, ct);
    }

    /// <summary>Replaces any pending code, clears the password and rotates the stamp: the old link and the old sessions die together.</summary>
    private static async Task<IssuedAccountToken> IssueAsync(ISession session, Guid userId, string purpose, Guid actorId, CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expires = DateTime.UtcNow + (purpose == BuiltinAccountToken.InvitationPurpose ? InvitationLifetime : ResetLifetime);
        // Bulk delete runs immediately, so the partial unique index never sees two pending tokens.
        await session.CreateQuery("delete BuiltinAccountToken where UserId = :user")
            .SetParameter("user", userId)
            .ExecuteUpdateAsync(ct);
        var credential = await session.GetAsync<BuiltinCredential>(userId, ct);
        if (credential is not null)
        {
            credential.PasswordHash = null;
        }
        var user = await session.GetAsync<UserAccount>(userId, ct)
            ?? throw new ApiException(404, "Uživatel nebyl nalezen.");
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await AccountHistory.RecordAsync(session, user, actorId, ct);
        await session.SaveAsync(new BuiltinAccountToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            Purpose = purpose,
            ExpiresAt = expires
        }, ct);
        await session.FlushAsync(ct);
        return new IssuedAccountToken(token, expires, purpose);
    }
}
