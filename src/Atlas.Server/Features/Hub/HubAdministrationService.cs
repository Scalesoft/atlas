using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Queries;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Wolverine;

namespace Atlas.Server.Features.Hub;

public sealed class HubAdministrationService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory,
    IDataProtectionProvider protection, TimeProvider clock, HubSynchronizer synchronizer)
{
    private readonly IDataProtector m_protector = protection.CreateProtector(HubConfigurationHandler.TokenPurpose);

    public async Task<HubSettingsView> SaveAsync(SaveHubSettings request, CancellationToken ct)
    {
        var prepared = await InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(request), ct);
        if (prepared.Mode != HubConnectionMode.Disabled)
            await ReadAsync(prepared.BaseUrl, prepared.ProtectedToken, (client, uri, token) => client.SearchGroupsAsync(uri, token, "", ct));
        return await InvokeAsync<HubSettingsView>(new CommitHubSettings(prepared), ct);
    }

    public async Task TestAsync(SaveHubSettings request, CancellationToken ct)
    {
        var prepared = await InvokeAsync<PreparedHubSettings>(new PrepareHubSettings(request), ct);
        await ReadAsync(prepared.BaseUrl, prepared.ProtectedToken, (client, uri, token) => client.SearchGroupsAsync(uri, token, "", ct));
    }

    public async Task<HubGroupSearchResult> SearchAsync(string? query, CancellationToken ct)
    {
        if (query?.Length > 256) throw new ApiException(400, "Hledaný výraz může mít nejvýše 256 znaků.");
        var config = await InvokeAsync<HubDirectoryConfiguration>(new GetHubDirectoryConfiguration(), ct);
        return await ReadAsync(config.BaseUrl, config.ProtectedToken, (client, uri, token) => client.SearchGroupsAsync(uri, token, query ?? "", ct));
    }

    public async Task<HubSettingsView> AttachAsync(AttachHubGroup request, CancellationToken ct)
    {
        var config = await InvokeAsync<HubDirectoryConfiguration>(new GetHubDirectoryConfiguration(), ct);
        if (config.Version != request.ExpectedVersion) throw new ApiException(409, "Připojení se mezitím změnilo. Načtěte nastavení znovu.");
        if (string.IsNullOrWhiteSpace(request.ExternalGroupId)) throw new ApiException(400, "Vyberte skupinu Hubu.");
        var group = await ReadAsync(config.BaseUrl, config.ProtectedToken, (client, uri, token) => client.ReadGroupAsync(uri, token, request.ExternalGroupId, ct));
        return await InvokeAsync<HubSettingsView>(new CommitHubGroup(config.RevisionId, request.ExpectedVersion, group), ct);
    }

    public async Task<HubVerificationResult> VerifyAsync(Guid? userId, bool selfOnly, CancellationToken ct)
    {
        var identities = await InvokeAsync<IReadOnlyList<Guid>>(new GetHubVerificationIdentities(userId, selfOnly), ct);
        var verified = 0;
        var failed = 0;
        var deferred = 0;
        await Parallel.ForEachAsync(identities, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (id, token) =>
        {
            var result = await synchronizer.VerifyAsync(id, true, token);
            if (result is null || !result.Applied) Interlocked.Increment(ref deferred);
            else if (result.Status is "verified" or "preview") Interlocked.Increment(ref verified);
            else Interlocked.Increment(ref failed);
        });
        return new(verified, failed, deferred);
    }

    private async Task<T> ReadAsync<T>(string baseUrl, string protectedToken, Func<HubDirectoryClient, Uri, string, Task<T>> read)
    {
        try
        {
            using var http = httpFactory.CreateClient("hub-directory");
            return await read(new HubDirectoryClient(http, clock), new Uri(baseUrl), m_protector.Unprotect(protectedToken));
        }
        catch (HubDirectoryException ex)
        {
            throw new ApiException(503, "Hub nelze ověřit. Zkontrolujte spojení a oprávnění čtecího účtu.", "hub_" + ex.Code);
        }
        catch (CryptographicException)
        {
            throw new ApiException(503, "Uložený token nelze přečíst. Zadejte nový čtecí token.", "hub_token-unavailable");
        }
    }

    private async Task<T> InvokeAsync<T>(object command, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, ct);
    }
}

public sealed record HubVerificationResult(int Verified, int Failed, int Deferred);
