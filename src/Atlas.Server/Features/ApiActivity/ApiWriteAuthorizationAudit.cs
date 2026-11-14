using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Atlas.Server.Features.ApiActivity;

/// <summary>
/// The refused scope of an audited write: authorization answers 403 scope_missing before any endpoint filter runs, so the attempt is
/// recorded here, then the default handler writes the response exactly as before. Only routes marked <see cref="ApiWriteAudit"/>, and
/// only an authenticated token (a challenge, 401, has no token to attribute). The rate limiter runs before authorization, so refusals
/// spend the token's personal-api budget and a flood of them leaves at most one window of rows, then 429s that write nothing.
/// </summary>
public sealed class ApiWriteAuthorizationAudit : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler m_default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.GetEndpoint()?.Metadata.GetMetadata<ApiWriteAudit>() is { } audit)
        {
            await context.RequestServices.GetRequiredService<ApiActivityAudit>()
                .RecordAsync(audit.Operation, ApiWriteAuditFilter.RoutePage(context), null, ApiActivityOutcomes.ScopeMissing, null);
        }
        await m_default.HandleAsync(next, context, policy, authorizeResult);
    }
}
