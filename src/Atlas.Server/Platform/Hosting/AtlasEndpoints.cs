using Atlas.Server.Features.Objects;
using Atlas.Server.Features.Comments;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.KnowledgeMaintenance;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.FileProviders;
using Atlas.Server.Features.Access;
using Atlas.Server.Features.AccessAudit;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.Assistant;
using Atlas.Server.Features.AiSuggestions;
using Atlas.Server.Features.AiWriting;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Auth.OpenIdConnect.Queries;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentImport;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.Hub;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageHistory;
using Atlas.Server.Features.PageSource;
using Atlas.Server.Features.PageOrganization;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.ExternalSharing;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalLibrary;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.Skills;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.Relations;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Features.Security;
using Atlas.Server.Features.SpaceAi;
using Atlas.Server.Features.SpaceCovers;
using Atlas.Server.Features.Spaces;
using Atlas.Server.Features.SpaceSettings;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.YouTrack;
using Atlas.Server.Features.PersonalItems;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Modules;
using Wolverine;

namespace Atlas.Server.Platform.Hosting;

/// <summary>Every route of the server. Health, CSRF and the SPA fallback live here; everything else is one MapXxx per feature.</summary>
public static class AtlasEndpoints
{
    public static void MapAtlasEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/health/ready", async (IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<WorkspaceState>(new CheckAuthenticationReady(), ct);
            return Results.Ok(new { status = "healthy" });
        });
        app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            // Antiforgery sets no-cache/no-store itself and logs a warning ("Cache-Control and Pragma overridden") when the /api middleware's no-store is already there.
            context.Response.Headers.Remove("Cache-Control");
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        });
        app.MapBuiltin();
        if (app.Services.GetRequiredService<BuiltinAuthenticationOptions>().Enabled)
        {
            app.MapBuiltinActivation();
        }
        app.MapOpenIdConnect();
        app.MapAdministration();
        Atlas.Server.Platform.Events.OutboxAdministration.MapOutboxAdministration(app);
        Atlas.Server.Features.Jobs.JobEndpoints.MapJobs(app);
        Atlas.Server.Features.AppLogs.AppLogEndpoints.MapAppLog(app);
        app.MapModuleSettings();
        app.MapAi();
        app.MapAiIndex();
        app.MapAssistant();
        app.MapAiSuggestions();
        app.MapAiWriting();
        app.MapHub();
        app.MapPersonalApi();
        app.MapMcpServer();
        app.MapAttachments();
        app.MapPageEmbeds();
        app.MapLiveWrites();
        app.MapContentExport();
        Atlas.Server.Features.StorageProfiles.StorageProfileModule.MapStorageProfiles(app);
        Atlas.Server.Features.PagePdfExports.PagePdfExportModule.MapPagePdfExports(app);
        Atlas.Server.Features.Backups.BackupEndpoints.MapWikiBackups(app);
        app.MapContentImportEndpoints();
        app.MapPageOrganization();
        Atlas.Server.Features.TreeReferences.TreeReferenceEndpoints.MapTreeReferences(app);
        app.MapPersonalLibraryEndpoints();
        app.MapPageTags();
        app.MapSkills();
        app.MapPageLinks();
        app.MapPageDecorations();
        app.MapPagePublication();
        app.MapRelations();
        Atlas.Server.Features.Entities.EntityEndpoints.MapEntities(app);
        app.MapPageTemplates();
        app.MapAccessAudit();
        app.MapSpaceSettings();
        app.MapSpaceCovers();
        Atlas.Server.Features.Linking.Lexicon.LexiconEndpoints.MapLexicon(app);
        Atlas.Server.Platform.Linking.LinkingEndpoints.MapLinking(app);
        app.MapSpaceAi();
        Atlas.Server.Features.TrashCleanup.TrashCleanupEndpoints.MapTrashCleanup(app);
        app.MapAuth();
        app.MapSpaces();
        app.MapPages();
        app.MapPageAccess();
        app.MapAccess();
        app.MapExternalSharing();
        app.MapPageHistory();
        app.MapPageSource();
        app.MapCollaboration();
        app.MapLive();
        app.MapObjects();
        Atlas.Server.Features.Contacts.ContactEndpoints.MapContacts(app);
        app.MapPersonalItems();
        app.MapComments();
        app.MapNotifications();
        app.MapSearch();
        app.MapSearchGaps();
        app.MapKnowledgeMaintenance();
        app.MapYouTrack();
        app.MapVaults();
        app.MapSecurity();
        app.MapModules();
        app.Map("/api/{**path}", () => Results.NotFound());
        app.MapFallbackToFile("index.html", AtlasHttpHosting.CreateStaticFileOptions(app.Services.GetRequiredService<PhysicalFileProvider>()))
            .WithMetadata(new SpaShellMarker());
    }
}
