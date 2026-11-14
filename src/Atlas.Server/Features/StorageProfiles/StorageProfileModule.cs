using Atlas.Server.Platform.Localization;
using Atlas.Server.Features.ModuleSettings;
using Wolverine;

namespace Atlas.Server.Features.StorageProfiles;

public static class StorageProfileModule
{
    public static IServiceCollection AddStorageProfiles(this IServiceCollection services)
    {
        services.AddSingleton<StorageProfileService>();
        return services;
    }

    public static void MapStorageProfiles(this WebApplication app)
    {
        var group = app.MapGroup("/api/storage-profiles").RequireAuthorization();
        group.MapGet("", async (IMessageBus bus, StorageProfileService profiles, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireStorageProfileAdministrator(), ct);
            return Results.Ok(await profiles.ListAsync(ct));
        });
        group.MapPost("", async (StorageProfileRequest request, IMessageBus bus, StorageProfileService profiles, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireStorageProfileAdministrator(), ct);
            return Results.Ok(await profiles.SaveAsync(null, request, ct));
        });
        group.MapPut("/{id:guid}", async (Guid id, StorageProfileRequest request, IMessageBus bus, StorageProfileService profiles, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireStorageProfileAdministrator(), ct);
            // The ZIP backups follow their profile: a new root, server or account of it is a new backup destination (SEC3-3).
            var approval = await bus.InvokeAsync<Backups.Commands.BackupDestinationApproval>(new Backups.Commands.AuthorizeBackupProfile(id, request), ct);
            return Results.Ok(await profiles.SaveAsync(id, request, ct, approval));
        });
        group.MapDelete("/{id:guid}", async (Guid id, int? expectedVersion, IMessageBus bus, StorageProfileService profiles, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireStorageProfileAdministrator(), ct);
            await profiles.DeleteAsync(id, expectedVersion, ct);
            return Results.NoContent();
        });
        group.MapPost("/{id:guid}/test", async (Guid id, IMessageBus bus, StorageProfileService profiles, HttpContext http, CancellationToken ct) =>
        {
            await bus.InvokeAsync<Guid>(new RequireStorageProfileAdministrator(), ct);
            try { await profiles.TestAsync(id, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { throw new ApiException(400, "Test přístupu k úložišti selhal. Zkontrolujte umístění a oprávnění."); }
            return Results.Ok(new { message = ErrorTexts.Translate(RequestLanguage.Of(http), "Přístup k úložišti ověřen.") });
        });
    }
}

public sealed record RequireStorageProfileAdministrator;
public static class RequireStorageProfileAdministratorHandler
{
    public static async Task<Guid> Handle(RequireStorageProfileAdministrator message, ModuleSettingsAccess access, CancellationToken ct)
        => (await access.RequireWikiAdminAsync(ct)).Id;
}
