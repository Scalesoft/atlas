namespace Atlas.Server.Features.PageTags;

public static class PageTagsModule
{
    public static IServiceCollection AddPageTags(this IServiceCollection services) => services.AddScoped<PageTagStore>();
}
