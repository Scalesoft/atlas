using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Attachments.Domain;

namespace Atlas.Server.Features.Attachments;

public static class AttachmentModule
{
    public static IServiceCollection AddAttachments(this IServiceCollection services)
    {
        services.AddSingleton<AttachmentFiles>();
        services.AddScoped<AttachmentPageGuard>();
        services.AddScoped<AttachmentCatalog>();
        // The download endpoint stamps its authorization with the clock the transfer measures it by.
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
