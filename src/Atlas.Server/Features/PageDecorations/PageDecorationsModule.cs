namespace Atlas.Server.Features.PageDecorations;

public static class PageDecorationsModule
{
    public static IServiceCollection AddPageDecorations(this IServiceCollection services) =>
        services.AddScoped<PageDecorationReader>();
}
