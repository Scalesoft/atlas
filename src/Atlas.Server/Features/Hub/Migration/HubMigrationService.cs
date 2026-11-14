using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Directory;
using Microsoft.AspNetCore.DataProtection;
using Wolverine;

namespace Atlas.Server.Features.Hub.Migration;

public sealed class HubMigrationService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, IDataProtectionProvider protection, TimeProvider clock)
{
    public async Task<HubMigrationPreview> PreviewAsync(PreviewHubMigration request, CancellationToken ct)
    {
        PreparedHubMigration prepared;
        using (var scope = scopes.CreateScope())
            prepared = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<PreparedHubMigration>(new PrepareHubMigration(request), ct);
        // The preparation scope and all its database locks are already disposed before HTTP starts.
        try
        {
            using var http = httpFactory.CreateClient("hub-directory");
            var directory = new HubDirectoryClient(http, clock);
            var token = protection.CreateProtector(HubConfigurationHandler.TokenPurpose).Unprotect(prepared.ProtectedToken);
            var baseUrl = new Uri(prepared.BaseUrl);
            foreach (var id in prepared.Inventory.Bindings.Select(x => x.ExternalGroupId).Concat(request.Groups.Select(x => x.ExternalGroupId)).Distinct(StringComparer.Ordinal))
            {
                if (clock.GetUtcNow().UtcDateTime >= prepared.Ticket.StartedAt.AddMinutes(5)) throw Expired();
                await directory.ReadGroupAsync(baseUrl, token, id, ct);
            }
            using var concurrency = new SemaphoreSlim(4);
            var observations = await Task.WhenAll(HubMigrationSnapshot.ActiveIdentities(prepared.Inventory).Select(async identity =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    if (clock.GetUtcNow().UtcDateTime >= prepared.Ticket.StartedAt.AddMinutes(5)) throw Expired();
                    var user = await directory.ReadUserAsync(baseUrl, token, identity.Subject, ct);
                    return new HubMigrationObservation(identity.Id, identity.UserId, user);
                }
                finally { concurrency.Release(); }
            }));
            if (clock.GetUtcNow().UtcDateTime >= prepared.Ticket.StartedAt.AddMinutes(5)) throw Expired();
            var ticket = prepared.Ticket with { Observations = observations };
            var serialized = JsonSerializer.Serialize(ticket);
            if (Encoding.UTF8.GetByteCount(serialized) > 4 * 1024 * 1024)
                throw new ApiException(409, "Pozorování jsou příliš velká pro bezpečný interaktivní náhled.", "hub_migration_too_large");
            var protectedTicket = protection.CreateProtector(HubMigrationHandler.TicketPurpose).Protect(serialized);
            var inventory = prepared.Inventory;
            var pageIds = request.PageBoundaries.Select(x => x.PageId).ToHashSet();
            var removed = inventory.Grants.Where(x => x.ActiveTo is null && (request.RemoveSpaceGrantIds.Contains(x.Id) || request.RemovePageGrantIds.Contains(x.Id) ||
                x.PageId is { } page && pageIds.Contains(page))).ToArray();
            var copied = HubMigrationSnapshot.CopiedDefaults(inventory, ticket);
            return new(protectedTicket, ticket.StartedAt.AddMinutes(5), request.ExpectedVersion, inventory,
                HubMigrationSnapshot.MembershipDiffs(inventory, ticket), removed, request.PageBoundaries, copied, request.RemoveWikiAdminUserIds,
                observations.Select(x => new HubMigrationObservedIdentity(x.IdentityId, x.UserId, x.User.State.ToString())).ToArray(),
                HubMigrationSnapshot.Routes(inventory, ticket, false), HubMigrationSnapshot.Routes(inventory, ticket, true),
                ["Náhled po migraci předpokládá Enforced. V Preview jsou externí práva vypnutá; přepnutí režimu vyžaduje nové ověření identit.",
                 "Existující připojené skupiny se aktualizují podle stejného úplného pozorování. Nezávislé osobní, místní a administrátorské cesty zůstávají uvedené samostatně.",
                 "Uživatelé Hubu bez účtu Atlasu mají přístup připravený pro první přihlášení. Přímé členství týmu YouTracku nestačí.",
                 "Vlastní přístupy se mění pouze na vyjmenovaných stránkách; obsahové hranice, nové stránky a přesuny ověřte před rolloutem."]);
        }
        catch (HubDirectoryException ex)
        {
            throw new ApiException(409, "Hub náhled nebyl úplně ověřen. Zkontrolujte připojení a opakujte náhled.", "hub_" + ex.Code);
        }
        catch (CryptographicException)
        {
            throw new ApiException(409, "Přihlašovací údaj Hubu nelze načíst.", "hub_token_unavailable");
        }
    }

    private static ApiException Expired() => new(409, "Pozorování migrace vypršelo; vytvořte nový náhled.", "hub_migration_expired");
}
