using System.Globalization;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration.Queries;

internal static class ProfileAccountReader
{
    /// <summary>The cookie owns the current session facts; the projection owns the stored ones. Claims arrive as raw
    /// strings on the message so the handler stays free of HttpContext.</summary>
    public static async Task<ProfileAccount> ReadAsync(ISession session, Guid id, string? authTime, string? authenticationMethod, CancellationToken ct)
    {
        var row = await session.QueryOver<ProfileAccountView>().Where(x => x.Id == id).SingleOrDefaultAsync(ct)
            ?? throw new ApiException(401, "Přihlášení vypršelo.");
        var source = row.HasBuiltinCredential ? row.HasExternalIdentity ? "linked" : "builtin" : "external";
        DateTimeOffset? authenticatedAt = null;
        if (long.TryParse(authTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            authenticatedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return new ProfileAccount(row.DisplayName, row.Email, AdministrationVersion.Format(row.ActiveFrom), source, true, true,
            authenticatedAt, authenticationMethod, row.LastSignInAt, row.LastSignInMethod);
    }
}
