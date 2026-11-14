using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Features.Pages.Objects;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Objects;

namespace Atlas.Server.Features.Pages;

public static class PagesModule
{
    public static IServiceCollection AddPages(this IServiceCollection services)
    {
        services.AddScoped<PageReader>();
        services.AddScoped<RequirementPageConversion>();
        services.AddSingleton<RequirementRepairMemory>();
        services.AddScoped<PageMarkdownSaveRunner>();
        services.AddScoped<PageMarkdownCreate>();
        // The linking writes save and publish through the shared handlers, as MCP does (here, not in the platform: they need the
        // pages and collaboration modules, which a platform-only host such as the OIDC fixture leaves out).
        services.TryAddScoped<SavePageHandler>();
        services.TryAddScoped<PublishPageHandler>();
        // The page bundle reads these parts under the page it opened (GetPageWithBundleHandler).
        services.TryAddScoped<PageLinks.Queries.GetPageConnectionsHandler>();
        services.TryAddScoped<PageLinks.Queries.ResolvePageReferencesHandler>();
        services.TryAddScoped<Collaboration.Queries.WatchCollaborationHandler>();
        services.AddSecretReferences();
        // The page as an object type and as a view (the mandatory pages module's; here so every host that has pages has them).
        services.AddScoped<PageObjectType>();
        services.AddScoped<IObjectType>(provider => provider.GetRequiredService<PageObjectType>());
        services.AddScoped<IObjectView, PageObjectView>();
        services.AddScoped<Atlas.Server.Platform.Resources.IObjectRelationSource, PageTemplateRelations>();
        services.AddJob<RequirementsOverviewJob>();
        services.AddJob<RequirementsHeadingsJob>();
        services.AddJob<RequirementSystemsJob>();
        return services;
    }
}
