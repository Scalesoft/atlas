using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Features.Hub.Queries;
using Atlas.Server.Features.Hub.Contracts;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Wolverine;

namespace Atlas.Server.Features.Hub;

public sealed class HubSynchronizer(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, IDataProtectionProvider protection, TimeProvider clock) : IDisposable
{
    /// <summary>The identities one <see cref="DueAsync"/> lists at most.</summary>
    public const int DueBatch = 256;

    private readonly SemaphoreSlim m_upstream = new(4, 4);
    private readonly IDataProtector m_protector = protection.CreateProtector(HubConfigurationHandler.TokenPurpose);

    public async Task<HubSyncResult?> VerifyAsync(Guid identityId, bool force, CancellationToken ct)
    {
        await m_upstream.WaitAsync(ct);
        try
        {
            var attempt = await InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identityId, force), ct);
            if (attempt is null) return null;
            ApplyHubSync result;
            try
            {
                var token = m_protector.Unprotect(attempt.ProtectedToken);
                using var http = httpFactory.CreateClient("hub-directory");
                var observation = await new HubDirectoryClient(http, clock).ReadUserAsync(new Uri(attempt.BaseUrl), token, attempt.ExternalUserId, ct);
                result = new ApplyHubSync(attempt, observation);
            }
            catch (HubDirectoryException ex)
            {
                result = new ApplyHubSync(attempt, null, ex.Code, ex.RetryAfter);
            }
            catch (CryptographicException)
            {
                result = new ApplyHubSync(attempt, null, "token-unavailable");
            }
            // Each invocation owns a fresh scope and transaction. Upstream never runs within either.
            return await InvokeAsync<HubSyncResult>(result, ct);
        }
        finally
        {
            m_upstream.Release();
        }
    }

    public async Task<HubSyncResult?> VerifyLoginAsync(Guid userId, string issuer, string subject, CancellationToken ct)
    {
        var identity = await InvokeAsync<Guid?>(new FindHubLoginIdentity(userId, issuer, subject), ct);
        return identity is null ? null : await VerifyAsync(identity.Value, true, ct);
    }

    public Task<IReadOnlyList<Guid>> DueAsync(CancellationToken ct) => InvokeAsync<IReadOnlyList<Guid>>(new ListDueHubIdentities(), ct);
    public Task<DateTime?> NextDueAsync(CancellationToken ct) => InvokeAsync<DateTime?>(new NextHubIdentityDue(), ct);
    public Task<HubHealthView?> HealthAsync(CancellationToken ct) => InvokeAsync<HubHealthView?>(new GetHubHealth(), ct);

    private async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }

    public void Dispose() => m_upstream.Dispose();
}
