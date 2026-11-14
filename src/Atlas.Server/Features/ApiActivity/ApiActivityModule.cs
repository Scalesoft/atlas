using Microsoft.AspNetCore.Authorization;

namespace Atlas.Server.Features.ApiActivity;

public static class ApiActivityModule
{
    public static IServiceCollection AddApiActivity(this IServiceCollection services)
    {
        services.AddScoped<ApiActivityAudit>();
        // Wraps the default handler; the framework registers its own with TryAdd, so this one wins in either order.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiWriteAuthorizationAudit>();
        return services;
    }

    /// <summary>Audits every attempt of this /api/v1 write route (plan D5), refused scopes included. One line per route.</summary>
    public static RouteHandlerBuilder AuditApiWrite(this RouteHandlerBuilder builder, string operation)
        => builder.WithMetadata(new ApiWriteAudit(operation)).AddEndpointFilter<ApiWriteAuditFilter>();
}
