using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Persistence;
using Atlas.Server.Platform.Jobs;
using Microsoft.AspNetCore.DataProtection;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Commands;

public sealed class HubConfigurationHandler(ISession session, AdministrationAccess access, IAtlasLocks locks,
    ISecurityChangeGuard guard, AtlasOidcOptions oidc, IDataProtectionProvider protection, AccessChanges changes, IJobRequests jobs)
{
    public const string TokenPurpose = "ScaleAtlas.Hub.Token.v1";

    public async Task<PreparedHubSettings> Handle(PrepareHubSettings command, CancellationToken ct)
    {
        var actor = await access.RequireWikiAdminAsync(ct);
        var request = command.Request;
        var current = await HubReads.CurrentAsync(session, ct);
        AdministrationVersion.Expect(current?.ActiveFrom, request.ExpectedVersion);
        var issuer = request.Issuer?.Trim() ?? "";
        var baseUrl = request.BaseUrl?.Trim().TrimEnd('/') ?? "";
        ValidateIssuer(issuer, current);
        ValidateBaseUrl(baseUrl);
        var mode = ParseMode(request.Mode);
        var protectedToken = PrepareToken(request.Token, baseUrl, current);
        return new PreparedHubSettings(actor.Id, actor.SecurityStamp, request.ExpectedVersion, issuer, baseUrl, mode, protectedToken);
    }

    private void ValidateIssuer(string issuer, HubConnectionRevision? current)
    {
        if (oidc.Providers.Count(x => x.Issuer == issuer) != 1) throw new ApiException(400, "Vyberte právě jednoho nakonfigurovaného poskytovatele přihlášení.");
        if (current is not null && issuer != current.Issuer) throw new ApiException(409, "Vydavatele existujícího Hub připojení nelze změnit.");
    }

    private static void ValidateBaseUrl(string baseUrl)
    {
        if (baseUrl.Length > 2048 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ApiException(400, "Adresa Hubu musí být HTTPS bez přihlašovacích údajů, parametrů a fragmentu.");
    }

    private static HubConnectionMode ParseMode(string value)
    {
        if (!Enum.TryParse<HubConnectionMode>(value, false, out var mode) || !Enum.IsDefined(mode) || mode.ToString() != value)
            throw new ApiException(400, "Vyberte režim Disabled, Preview nebo Enforced.");
        return mode;
    }

    private string PrepareToken(string? token, string baseUrl, HubConnectionRevision? current)
    {
        if (token is { Length: > 8192 } || token?.Any(char.IsControl) == true) throw new ApiException(400, "Přístupový token nemá platný formát.");
        var protectedToken = current?.ProtectedToken ?? "";
        if (!string.IsNullOrWhiteSpace(token)) protectedToken = protection.CreateProtector(TokenPurpose).Protect(token.Trim());
        else if (current is null || current.BaseUrl != baseUrl) throw new ApiException(400, "Pro nové připojení nebo změnu adresy zadejte nový čtecí token.");
        return protectedToken;
    }

    public async Task<HubSettingsView> Handle(CommitHubSettings command, CancellationToken ct)
    {
        var actor = await access.LockAdministrationAsync(ct);
        var settings = command.Settings;
        if (actor.Id != settings.ActorId || actor.SecurityStamp != settings.SecurityStamp) throw new ApiException(409, "Přihlášení správce se mezitím změnilo.");
        var previous = await HubReads.CurrentAsync(session, ct);
        await ValidateCommitAsync(settings, previous, ct);
        if (IsUnchanged(settings, previous))
            return await HubSettingsReader.ReadAsync(session, oidc, ct);
        await RequireSecurityChangeAsync(actor, settings, previous, ct);
        await SaveRevisionAsync(actor.Id, settings, previous, ct);
        changes.Changed();
        // A new revision makes every identity due (ListDueHubIdentities); a disabled connection has nothing to verify.
        if (settings.Mode != HubConnectionMode.Disabled) await jobs.RequestAsync(HubIdentityJob.JobId, "Uložené připojení Hubu", ct: ct);
        await session.FlushAsync(ct);
        return await HubSettingsReader.ReadAsync(session, oidc, ct);
    }

    private async Task ValidateCommitAsync(PreparedHubSettings settings, HubConnectionRevision? previous, CancellationToken ct)
    {
        AdministrationVersion.Expect(previous?.ActiveFrom, settings.ExpectedVersion);
        if (oidc.Providers.Count(x => x.Issuer == settings.Issuer) != 1 || previous is not null && previous.Issuer != settings.Issuer)
            throw new ApiException(409, "Poskytovatel přihlášení se mezitím změnil.");
        if (await session.CreateSQLQuery("""
            SELECT EXISTS (SELECT 1 FROM "ExternalIdentity" WHERE "Issuer"=:issuer AND "ActiveTo" IS NULL
                GROUP BY "UserId" HAVING count(*) > 1)
            """).SetParameter("issuer", settings.Issuer).UniqueResultAsync<bool>(ct))
            throw new ApiException(409, "Některý účet má více aktivních identit tohoto Hubu. Nejprve vyřešte jejich propojení.");
    }

    private static bool IsUnchanged(PreparedHubSettings settings, HubConnectionRevision? previous) =>
        previous is not null && previous.BaseUrl == settings.BaseUrl && previous.ProtectedToken == settings.ProtectedToken && previous.Mode == settings.Mode;

    private async Task RequireSecurityChangeAsync(UserAccount actor, PreparedHubSettings settings, HubConnectionRevision? previous, CancellationToken ct)
    {
        if (previous?.Mode == HubConnectionMode.Enforced || settings.Mode == HubConnectionMode.Enforced)
            await guard.RequireAsync(actor, new SecurityChange("hub.connection", previous?.ConnectionId ?? Guid.Empty), ct);
        if (settings.Mode == HubConnectionMode.Enforced) await HubSettingsReader.RequireRecoveryAsync(session, oidc, ct);
    }

    private async Task SaveRevisionAsync(Guid actorId, PreparedHubSettings settings, HubConnectionRevision? previous, CancellationToken ct)
    {
        await locks.SetActorAsync(actorId, ct);
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, await HubReads.NowAsync(session, ct));
        var connectionId = previous?.ConnectionId ?? Guid.NewGuid();
        if (previous is null) await session.SaveAsync(new HubConnection { Id = connectionId }, ct);
        else { previous.ActiveTo = now; await session.FlushAsync(ct); }
        var revision = new HubConnectionRevision
        {
            Id = Guid.NewGuid(), ConnectionId = connectionId, Issuer = settings.Issuer, BaseUrl = settings.BaseUrl,
            ProtectedToken = settings.ProtectedToken, Mode = settings.Mode, ChangedById = actorId, ActiveFrom = now
        };
        await session.SaveAsync(revision, ct);
        await HubHistory.ConnectionAsync(session, revision, ct);
    }
}
