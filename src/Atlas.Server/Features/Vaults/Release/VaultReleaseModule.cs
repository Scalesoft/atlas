using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Security.Reauth;
using Atlas.Server.Features.Vaults.Audit;
using Atlas.Server.Features.Vaults.Audit.Queries;
using Atlas.Server.Features.Vaults.Commands;
using Atlas.Server.Features.Vaults.Requests;
using Atlas.Server.Features.Vaults.Requests.Queries;
using Wolverine;

namespace Atlas.Server.Features.Vaults.Release;

/// <summary>Access requests, audited release, the vault audit and live revocation (WP4 V5).</summary>
public static class VaultReleaseModule
{
    public static IServiceCollection AddVaultRelease(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(SecretReleaseOptions.Read(configuration));
        services.AddSingleton<SecretReleaseThrottle>();
        // WP4 V4 registers the real policy; until then nothing is ever released.
        services.TryAddScoped<IReleasePolicy, FailClosedReleasePolicy>();
        services.AddSignalR();
        services.TryAddSingleton<ISecurityEvents, LiveSecurityEvents>();
        services.AddSingleton<SecretReleaseWatch>();
        services.AddSingleton<IAccessChangeObserver>(provider => provider.GetRequiredService<SecretReleaseWatch>());
        services.AddHostedService(provider => provider.GetRequiredService<SecretReleaseWatch>());
        services.AddScoped<SecretReleaseService>();
        // The approval of an access request writes its grant through the V3 grant path.
        services.AddScoped<VaultGrantHandler>();
        return services;
    }

    public static void MapVaultRelease(this IEndpointRouteBuilder app)
    {
        var secrets = app.MapGroup("/api/secrets").RequireAuthorization();
        secrets.MapPost("/{id:guid}/release", async (Guid id, ReleaseSecretRequest request, SecretReleaseService release, CancellationToken ct) =>
        {
            var (values, reauth) = await release.ReleaseAsync(id, request, ct);
            if (values is null)
            {
                // The one re-authentication refusal of every endpoint (403 reauth_required / fresh_auth_unsupported), with the mode.
                throw ReauthRefusal.Release(reauth!.Mode, reauth.ModeSource, reauth.Supported);
            }
            // A single field without fields[] also answers in the single shape { value, expiresInSeconds }.
            return request is { Fields: null, Field: { } field }
                ? Results.Ok(new SecretValueView(values.Values[field], values.ExpiresInSeconds))
                : Results.Ok(values);
        }).Produces<SecretValuesView>().ProducesProblem(ReauthRefusal.Status);
        secrets.MapPost("/{id:guid}/release/report", async (Guid id, ReleaseReportRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new ReportSecretRelease(id, ReleaseFields.Parse(request.Fields, request.Field), request.Operation ?? ""), ct);
            return Results.NoContent();
        });
        secrets.MapPost("/{id:guid}/access-requests", async (Guid id, CreateAccessRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessRequestView>(new RequestSecretAccess(id, request), ct)))
            .Produces<AccessRequestView>();
        secrets.MapGet("/{id:guid}/audit", async (Guid id, Guid? actorId, string? kind, string? result, DateTimeOffset? from, DateTimeOffset? to,
                string? beforeId, int? limit, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<VaultAuditList>(new GetVaultAudit(null, id, actorId, kind, result, from, to, beforeId, limit ?? 50), ct)))
            .Produces<VaultAuditList>();

        var vaults = app.MapGroup("/api/vaults").RequireAuthorization();
        vaults.MapPost("/{id:guid}/access-requests", async (Guid id, CreateAccessRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessRequestView>(new RequestVaultAccess(id, request), ct)))
            .Produces<AccessRequestView>();
        vaults.MapGet("/{id:guid}/audit", async (Guid id, Guid? actorId, string? kind, string? result, DateTimeOffset? from, DateTimeOffset? to,
                string? beforeId, int? limit, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<VaultAuditList>(new GetVaultAudit(id, null, actorId, kind, result, from, to, beforeId, limit ?? 50), ct)))
            .Produces<VaultAuditList>();

        var requests = app.MapGroup("/api/access-requests").RequireAuthorization();
        requests.MapGet("", async (string? state, Guid? vaultId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessRequestList>(new ListAccessRequests(state, vaultId), ct)))
            .Produces<AccessRequestList>();
        requests.MapGet("/mine", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessRequestList>(new ListMyAccessRequests(), ct)))
            .Produces<AccessRequestList>();
        requests.MapPost("/{id:guid}/approve", async (Guid id, ApproveAccessRequestBody? body, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessRequestView>(new ApproveAccessRequest(id, body?.ExpiresAt), ct)))
            .Produces<AccessRequestView>();
        requests.MapPost("/{id:guid}/reject", async (Guid id, RejectAccessRequestBody? body, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessRequestView>(new RejectAccessRequest(id, body?.Note), ct)))
            .Produces<AccessRequestView>();
        requests.MapPost("/{id:guid}/cancel", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AccessRequestView>(new CancelAccessRequest(id), ct)))
            .Produces<AccessRequestView>();

    }
}
