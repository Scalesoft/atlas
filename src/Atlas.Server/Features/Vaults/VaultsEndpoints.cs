using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Contracts;
using Atlas.Server.Features.Vaults.Queries;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.Vaults.Release;
using Wolverine;

namespace Atlas.Server.Features.Vaults;

/// <summary>
/// Vault administration and access rights. Locations are WikiAdmin surface; vault registrations are managed by the space Admin
/// (and WikiAdmin); access to vaults and secrets follows their access level (WP4 V3). Every response is no-store and
/// uncompressed; no response ever carries a secret value, a stored password or a hash of one. The only password in a response
/// is the generated master password of a new vault, returned once.
/// </summary>
public static class VaultsEndpoints
{
    public static void MapVaults(this IEndpointRouteBuilder app)
    {
        var locations = app.MapGroup("/api/vault-locations").RequireAuthorization();
        locations.MapGet("", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<VaultLocationView>>(new ListVaultLocations(), ct)))
            .Produces<IReadOnlyList<VaultLocationView>>();
        locations.MapPost("", async (SaveVaultLocationRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<VaultLocationView>(new SaveVaultLocation(null, request), ct)))
            .Produces<VaultLocationView>();
        locations.MapPut("/{id:guid}", async (Guid id, SaveVaultLocationRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<VaultLocationView>(new SaveVaultLocation(id, request), ct)))
            .Produces<VaultLocationView>();
        locations.MapDelete("/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new ArchiveVaultLocation(id), ct);
            return Results.NoContent();
        });
        locations.MapGet("/{id:guid}/files", async (Guid id, Guid? spaceId, string? q, VaultOperations operations, CancellationToken ct)
                => Results.Ok(await operations.ListLocationFilesAsync(id, spaceId, q, ct)))
            .Produces<VaultLocationFiles>();
        locations.MapPost("/test", async (TestVaultLocationRequest request, VaultOperations operations, CancellationToken ct)
                => Results.Ok(await operations.TestLocationAsync(request, ct)))
            .Produces<VaultLocationTestResult>();

        var vaults = app.MapGroup("/api/vaults").RequireAuthorization();
        vaults.MapGet("", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<VaultView>>(new ListVaults(), ct)))
            .Produces<IReadOnlyList<VaultView>>();
        vaults.MapPost("/new", async (CreateVaultRequest request, VaultOperations operations, CancellationToken ct)
                => Results.Ok(await operations.CreateAsync(request, ct)))
            .Produces<CreatedVault>();
        vaults.MapPost("/attach", async (AttachVaultRequest request, VaultOperations operations, CancellationToken ct)
                => Results.Ok(await operations.AttachAsync(request, ct)))
            .Produces<VaultView>();
        vaults.MapPut("/{id:guid}/password", async (Guid id, VaultPasswordRequest request, VaultOperations operations, CancellationToken ct)
                => Results.Ok(await operations.ReplacePasswordAsync(id, request, ct)))
            .Produces<VaultView>();
        vaults.MapPost("/{id:guid}/archive", async (Guid id, VaultOperations operations, CancellationToken ct) =>
        {
            await operations.ArchiveAsync(id, ct);
            return Results.NoContent();
        });
        vaults.MapPost("/{id:guid}/verify", async (Guid id, VaultOperations operations, CancellationToken ct)
                => Results.Ok(await operations.VerifyAsync(id, ct)))
            .Produces<VaultVerification>();
        vaults.MapGet("/{id:guid}/secrets", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<VaultSecretView>>(new ListVaultSecrets(id), ct)))
            .Produces<IReadOnlyList<VaultSecretView>>();
        vaults.MapGet("/{id:guid}/access", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<VaultAccessState>(new GetVaultAccess(id), ct)))
            .Produces<VaultAccessState>();
        vaults.MapGet("/{id:guid}/grant-candidates", async (Guid id, string? q, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<AccessCandidate>>(new GetVaultGrantCandidates(id, q), ct)))
            .Produces<IReadOnlyList<AccessCandidate>>();
        vaults.MapPut("/{id:guid}/grants", async (Guid id, UpdateVaultGrant request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetVaultGrant(id, request), ct);
            return Results.NoContent();
        });
        vaults.MapDelete("/{id:guid}/grants", async (Guid id, Guid? userId, Guid? groupId, string? expectedVersion, IMessageBus bus,
            CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeVaultGrant(id, userId, groupId, expectedVersion), ct);
            return Results.NoContent();
        });

        var secrets = app.MapGroup("/api/secrets").RequireAuthorization();
        secrets.MapGet("/{id:guid}/access", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<VaultAccessState>(new GetSecretAccess(id), ct)))
            .Produces<VaultAccessState>();
        secrets.MapGet("/{id:guid}/grant-candidates", async (Guid id, string? q, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<IReadOnlyList<AccessCandidate>>(new GetSecretGrantCandidates(id, q), ct)))
            .Produces<IReadOnlyList<AccessCandidate>>();
        secrets.MapPut("/{id:guid}/grants", async (Guid id, UpdateVaultGrant request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetSecretGrant(id, request), ct);
            return Results.NoContent();
        });
        secrets.MapDelete("/{id:guid}/grants", async (Guid id, Guid? userId, Guid? groupId, string? expectedVersion, IMessageBus bus,
            CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeSecretGrant(id, userId, groupId, expectedVersion), ct);
            return Results.NoContent();
        });

        app.MapSecretReferences();

        app.MapGet("/api/security/me", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SecurityCapabilitiesView>(new GetSecurityCapabilities(), ct)))
            .RequireAuthorization()
            .Produces<SecurityCapabilitiesView>();
        app.MapVaultRelease();
    }
}
