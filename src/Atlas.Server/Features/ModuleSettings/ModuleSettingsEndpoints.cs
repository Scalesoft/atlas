using Atlas.Server.Features.ModuleSettings.Commands;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Queries;
using Wolverine;

namespace Atlas.Server.Features.ModuleSettings;

public static class ModuleSettingsEndpoints
{
    public static void MapModuleSettings(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/modules").RequireAuthorization();
        group.MapGet("", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(new { items = await bus.InvokeAsync<IReadOnlyList<ModuleSettingsDescriptor>>(new ListModules(), ct) }));
        group.MapGet("/editor-assist", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<EditorAssistSettingsView>(new GetEditorAssistSettings(), ct)));
        group.MapPut("/editor-assist", async (SaveEditorAssistRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<EditorAssistSettingsView>(new SaveEditorAssistSettings(request), ct)));
        group.MapGet("/oidc", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<OidcSettingsView>(new GetOidcSettings(), ct)));
        group.MapPut("/oidc", async (SaveOidcSettings request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<OidcSettingsView>(new SaveOidcConfiguration(request), ct)));
        group.MapPost("/oidc/test", async (TestOidcProvider request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<OidcConnectionTest>(new TestOidcConnection(request), ct)));
        app.MapGet("/api/admin/setup", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(new { pending = await bus.InvokeAsync<bool>(new GetSetupState(), ct) })).RequireAuthorization();
        app.MapPost("/api/admin/setup/complete", async (IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new CompleteModuleSetup(), ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
