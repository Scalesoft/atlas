using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.PageLinks;

public static class PageLinksModule
{
    public static IServiceCollection AddPageLinks(this IServiceCollection services) => services.AddScoped<PageLinkStore>().AddScoped<MarkdownLinkResolver>()
        .AddScoped<IObjectRelationSource, PageLinkRelations>().AddScoped<PageLinkProjection>().AddJob<PageLinkRebuildJob>();
}
