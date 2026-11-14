namespace Atlas.Server.Platform.Modules;

public static class ModuleEndpoints
{
    /// <summary>GET /api/modules for a signed-in account, and the routes of every enabled module. Pure: the catalog is fixed at
    /// startup, so no handler and no session.</summary>
    public static void MapModules(this IEndpointRouteBuilder app)
    {
        var modules = app.ServiceProvider.GetRequiredService<ModuleCatalog>();
        app.MapGet("/api/modules", () => Results.Ok(modules.Describe()))
            .RequireAuthorization()
            .Produces<ModuleList>();
        foreach (var module in modules.Enabled)
        {
            module.MapEndpoints(app);
        }
    }
}
