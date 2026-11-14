using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.PersonalItems;
using Atlas.Server.Features.Comments;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.KnowledgeMaintenance;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Atlas.Server.Features.AccessAudit;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.Assistant;
using Atlas.Server.Features.AiIndex;
using Atlas.Server.Features.AiSuggestions;
using Atlas.Server.Features.AiWriting;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Auth;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentImport;
using Atlas.Server.Features.ExternalSharing;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.Hub;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageHistory;
using Atlas.Server.Features.PageOrganization;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalLibrary;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.Relations;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Features.Security;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.YouTrack;
using Atlas.Server.Platform;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Modules;
using Atlas.YouTrack;
using JasperFx;
using JasperFx.CodeGeneration;
using Atlas.Server.Platform.Events;
using Wolverine;

namespace Atlas.Server.Platform.Hosting;

/// <summary>
/// Every service registration of the server, in the order the modules need: the OIDC runtime is read
/// from the database before Build(), so this method is asynchronous.
/// </summary>
public static class AtlasServices
{
    public static async Task AddAtlasServer(this WebApplicationBuilder builder)
    {
        // Callback query strings contain authorization codes and state. Keep them out of request logs.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        // Framework request tracing ("Executing endpoint", "Writing value", "Sending file") costs several log
        // lines per call; warnings and errors still pass. Configuration can opt back in.
        if (builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore"] is null)
        {
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        }
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4_500_000);
        // The modules of the distribution, switched at startup; a disabled one registers nothing below (Atlas 3 plan 5.3).
        var modules = ModuleCatalog.Build(builder.Configuration, AtlasModules.All);
        builder.Services.AddSingleton(modules);
        foreach (var module in modules.Enabled)
        {
            module.AddServices(builder.Services, builder.Configuration);
        }
        // The provider is created by the container so the container disposes it at shutdown; the pipeline
        // and the endpoints both need the same instance (static files and the SPA fallback).
        var webRoot = builder.Environment.WebRootPath;
        builder.Services.AddSingleton(_ => new PhysicalFileProvider(Directory.CreateDirectory(webRoot).FullName));
        builder.Services.AddSingleton(Atlas.Server.Platform.Retention.RetentionOptions.From(builder.Configuration));
        builder.Services.AddSingleton(Atlas.Server.Platform.Jobs.JobQuietOptions.From(builder.Configuration));
        builder.Services.AddAtlasPlatform();
        // The stored log of Správa wiki → Systémový log beside the console: queued by the provider, written by the writer once the
        // database is up.
        var appLog = Atlas.Server.Platform.Logs.AppLogOptions.From(builder.Configuration);
        builder.Services.AddSingleton(appLog);
        builder.Services.AddSingleton<Atlas.Server.Platform.Logs.AppLogQueue>();
        builder.Services.AddSingleton<ILoggerProvider, Atlas.Server.Platform.Logs.AppLogProvider>();
        builder.Services.AddHostedService<Atlas.Server.Platform.Logs.AppLogWriter>();
        // Atlas registers its modules explicitly. Do not scan private data/backup directories for extensions.
        // After AddAtlasPlatform: hosted services start in order, so the first migration (AtlasDatabaseInitializer) has created
        // Wolverine's tables before its durable start, and the gate stops the start when that migration could not run.
        var durable = JobQueue.IsDurable(builder.Configuration);
        if (durable)
        {
            builder.Services.AddHostedService<DurableStartGate>();
        }
        builder.Host.UseWolverine(options =>
        {
            options.ConfigureAtlasWolverine(modules);
            if (durable)
            {
                options.UseAtlasDurability(builder.Configuration.GetConnectionString("atlas-db"));
            }
            options.UseAtlasOutbox(durable);
        }, ExtensionDiscovery.ManualOnly);
        // Production loads handler code generated at build time (`codegen write` in the Dockerfile) instead of
        // compiling each handler with Roslyn on its first message, and refuses to start if any is missing.
        // Development and tests keep generating it at runtime, so no generated source is ever committed.
        builder.Services.CritterStackDefaults(defaults =>
        {
            defaults.Production.GeneratedCodeMode = TypeLoadMode.Static;
            defaults.Production.AssertAllPreGeneratedTypesExist = true;
        });
        builder.Services.AddSingleton<Atlas.Server.Features.TrashCleanup.TrashCleanupRunner>();
        builder.Services.AddJob<Atlas.Server.Features.TrashCleanup.TrashCleanupJob>();
        builder.Services.AddSearch();
        builder.Services.AddAssistantSources();
        builder.Services.AddKnowledgeMaintenance().AddSearchGaps().AddSearchGapBackgroundServices();
        builder.Services.AddPages();
        builder.Services.AddContacts();
        Atlas.Server.Platform.Linking.LinkingServices.AddLinkingBackgroundServices(builder.Services);
        builder.Services.AddComments();
        builder.Services.AddNotifications();
        builder.Services.AddExternalSharing();
        builder.Services.AddPageHistory();
        builder.Services.AddCollaboration();
        builder.Services.AddAtlasYouTrack(builder.Configuration);
        builder.Services.AddAdministration();
        builder.Services.AddHub();
        builder.Services.AddJob<HubIdentityJob>().AddHostedService<HubHealthWatch>();
        builder.Services.AddPageAccess();
        builder.Services.AddPersonalApi();
        builder.Services.AddApiActivity();
        builder.Services.AddMcp();
        builder.Services.AddAttachments();
        builder.Services.AddContentExport();
        Atlas.Server.Features.StorageProfiles.StorageProfileModule.AddStorageProfiles(builder.Services);
        Atlas.Server.Features.PagePdfExports.PagePdfExportModule.AddPagePdfExports(builder.Services);
        Atlas.Server.Features.Backups.BackupModule.AddWikiBackups(builder.Services);
        builder.Services.AddContentImport();
        builder.Services.AddPageOrganization();
        Atlas.Server.Features.TreeReferences.TreeReferencesModule.AddTreeReferences(builder.Services);
        builder.Services.AddPersonalLibrary();
        builder.Services.AddPageTags();
        builder.Services.AddPageLinks();
        builder.Services.AddPageDecorations();
        builder.Services.AddRelations();
        builder.Services.AddYouTrack();
        builder.Services.AddVaults(builder.Configuration);
        // After the vaults: the access kinds are listed in registration order, the personal item last.
        builder.Services.AddPersonalItems();
        // Reads the persisted OIDC revision with Npgsql before the container exists (spec section 8); fail soft,
        // so an unreachable database leaves the wizard and /health/ready to report it over HTTP.
        using var startupLogging = LoggerFactory.Create(logging => logging.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole());
        // The loader below is the first to connect as the runtime role: the owner prepares it first (RuntimeDatabaseRole).
        await RuntimeDatabaseRole.PrepareAsync(builder.Configuration, startupLogging.CreateLogger(typeof(RuntimeDatabaseRole).FullName!));
        var oidcRuntime = await OidcRuntimeLoader.LoadAsync(builder.Configuration, builder.Environment, startupLogging.CreateLogger(typeof(OidcRuntimeLoader).FullName!));
        builder.Services.AddSingleton(oidcRuntime);
        builder.Services.AddModuleSettings(builder.Configuration);
        builder.Services.AddAi().AddAiBackgroundServices();
        builder.Services.AddAiIndex().AddAiIndexBackgroundServices();
        builder.Services.AddAssistant().AddAssistantFeedback();
        builder.Services.AddAiSuggestions();
        builder.Services.AddAiWriting();
        builder.Services.AddOpenIdConnect(builder.Configuration, builder.Environment, oidcRuntime.Options);
        builder.Services.AddSecurityCenter();
        builder.Services.AddAuth();
        builder.Services.AddAuthBackgroundServices();
        builder.Services.AddAuthCookies(builder.Configuration, builder.Environment);
        builder.Services.AddAuthorization();
        builder.Services.AddAtlasHttpHosting(builder.Configuration);
        // A request that does not bind throws to RequestErrorHandling in every environment, which names the field; outside Development
        // the endpoints would otherwise answer a 400 without a body.
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.Name = "ScaleAtlas.Csrf";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            // Every limiter here reports when a permit returns; clients (MCP ones included) back off by Retry-After.
            o.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = BuiltinLoginThrottle.RetryAfterHeader(retryAfter);
                }
                else if (PersonalApiConcurrency.Applies(context.HttpContext))
                {
                    context.HttpContext.Response.Headers.RetryAfter = BuiltinLoginThrottle.RetryAfterHeader(PersonalApiConcurrency.RetryAfter);
                }
                return ValueTask.CompletedTask;
            };
            // Chained in front of each endpoint's policy: how many requests of one personal token and of its account run at once.
            o.GlobalLimiter = PersonalApiConcurrency.Limiter();
            o.AddPolicy("external-sharing", context => RateLimitPartition.GetTokenBucketLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromSeconds(10), AutoReplenishment = true, QueueLimit = 0 }));
            o.AddPolicy("personal-api", context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst("api_token_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("search", context => RateLimitPartition.GetTokenBucketLimiter(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new TokenBucketRateLimiterOptions { TokenLimit = 30, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromSeconds(5), AutoReplenishment = true, QueueLimit = 0 }));
            // Browser typeahead sends a cheap prefix pass per keystroke and a full pass after a pause.
            // Keep it bounded per account without spending the personal API's slower search budget.
            o.AddPolicy("interactive-search", context => RateLimitPartition.GetTokenBucketLimiter(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new TokenBucketRateLimiterOptions { TokenLimit = 30, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true, QueueLimit = 0 }));
            // A board's canvas batches (BoardEndpoints.BoardBatchRateLimit): one in flight per open board, 429 keeps it pending and retried.
            o.AddPolicy("board-batch", context => RateLimitPartition.GetTokenBucketLimiter(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true, QueueLimit = 0 }));
            o.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("activation", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // Atlas 2.0 AI: editor assistance and creation suggestions per account; the admin's connection test spends tokens.
            o.AddPolicy("ai-assist", context => RateLimitPartition.GetTokenBucketLimiter(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new TokenBucketRateLimiterOptions { TokenLimit = 20, TokensPerPeriod = 10, ReplenishmentPeriod = TimeSpan.FromSeconds(5), AutoReplenishment = true, QueueLimit = 0 }));
            // Phase 3: preparing an answer (a question to the chat model) per account, 10 at once, then one every 6 s.
            o.AddPolicy("ai-answer", context => RateLimitPartition.GetTokenBucketLimiter(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new TokenBucketRateLimiterOptions { TokenLimit = 10, TokensPerPeriod = 1, ReplenishmentPeriod = TimeSpan.FromSeconds(6), AutoReplenishment = true, QueueLimit = 0 }));
            o.AddPolicy("ai-admin-test",context => RateLimitPartition.GetFixedWindowLimiter(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }
}
