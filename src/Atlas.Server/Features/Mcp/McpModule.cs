using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Atlas.Server.Features.ApiActivity.Commands;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.Mcp.Publishing;
using Atlas.Server.Features.Mcp.Tools;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.Notifications;
using Atlas.Server.Features.Objects;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// The MCP server module (plan 2026-10-05 MCP §10 C1): a second client of the same server operations as the web, at /mcp, for
/// personal tokens with the channel mcp. Atlas calls no model here; MCP depends neither on Features/Ai nor on the AI switch.
/// <list type="bullet">
/// <item>Stateless Streamable HTTP (D9): no server session, so revocation, the ceiling, the module switch and G5 apply to the next
/// request. GET and DELETE answer 405.</item>
/// <item>/mcp is always mapped, before the SPA fallback; the gate in front of authentication answers 404 mcp_disabled while the module
/// is off and 403 to a browser origin other than Atlas's own (the SDK checks neither Origin nor Host: DNS rebinding, S0 point 7).</item>
/// <item>Authentication only by the personal API scheme (channel mcp, never the cookie), the McpRead policy on the endpoint, the
/// shared "personal-api" budget per token, a bound on the requests running at once (<see cref="PersonalApiConcurrency"/>), and one
/// request limit of 3 000 000 B (<see cref="MaximumRequestBytes"/>).</item>
/// </list>
/// </summary>
public static class McpModule
{
    public static readonly string Instructions = "Atlas je firemní wiki. Nástroje čtou stránky, ke kterým má přístup vlastník tokenu, a jen z prostorů, " +
        "které nejsou vyloučené z AI a nástrojů MCP. " + McpText.TrustNotice + " Zástupné značky ⟦c:N⟧ označují komponenty (odkazy na hesla, " +
        "přílohy…), jejichž obsah nástroje nevracejí a které z Markdownu nevytvoříte. Text stránek píšete Markdownem editoru; komponenty, které lze psát: " +
        MarkdownSyntaxReference.WritableSummary + ". Než komponentu poprvé napíšete, zavolejte " + McpSyntaxTools.ToolName + " (bez argumentu přehled, component = přesná syntaxe, volby, příklady). " +
        "Informační box je citace se značkou na vlastním řádku: „> [!WARNING]“ a pod ní „> text“. Kontakt je jedna entita. Vždy odkazuj na existující, nikdy nevytvářej kopii ani textovou náhradu. Časovou odrážku (záznamy s datem a časem) zapište jako odrážky „- 3. 11. 2026 17:19 — text“ (čas lze vynechat). Token má rozsah Čtení, nebo Zápis; zápisové nástroje " +
        "(rozsah Zápis) mění stránku živě, jako by ji upravoval vlastník tokenu, a nikdy víc, než smí on. " + McpText.Publication + " Do stránky, kterou má někdo otevřenou, píšete živě jako další spolupracovník. Nástroje organizace stránek (přesun, řazení, koš) mění strom " +
        "hned a jde je vrátit. Nahrání přílohy (atlas_upload_attachment) stránku nemění, jen k ní přidá soubor připravený k vložení.";

    /// <summary>
    /// The request limit of every /mcp call: the largest Markdown write (1 000 000 characters) with its envelope, and the base64 of the
    /// largest upload (<see cref="McpAttachmentTools.MaximumUploadBase64Length"/>, 2 796 204 characters for 2 MiB) with 64 KiB for the
    /// JSON-RPC envelope and the other arguments (plain ASCII base64 needs no JSON escaping; a serializer that escapes <c>+</c> adds about
    /// 8 % and gets about 1.9 MiB through). A larger body is refused before the SDK reads it (<see cref="UseMcpGate"/> for a declared
    /// length, Kestrel's endpoint limit otherwise).
    /// </summary>
    public const long MaximumRequestBytes = 3_000_000;

    /// <summary>The module switch, the endpoint address and the services the token profile needs (also in handler test hosts).</summary>
    public static IServiceCollection AddMcpModuleState(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<McpModuleState>();
        // A factory registration: Wolverine resolves it from the container instead of inlining a constructor with optional arguments.
        services.TryAddSingleton(provider => new McpAddress(provider.GetService<IHttpContextAccessor>(), provider.GetService<OidcRuntimeConfiguration>()));
        return services;
    }

    /// <summary>What the MCP queries and draft commands resolve (also in handler test hosts): G5, paths, page links, heading ids, and the
    /// shared page handlers the draft commands run in their own transaction (create, save, rename, the attachment store).</summary>
    public static IServiceCollection AddMcpContent(this IServiceCollection services)
    {
        services.TryAddScoped<McpVisibility>();
        services.TryAddScoped<McpPagePaths>();
        services.TryAddScoped<MarkdownLinkResolver>();
        services.TryAddSingleton<McpHeadingIds>();
        services.TryAddScoped<CreatePageHandler>();
        services.TryAddScoped<SavePageHandler>();
        services.TryAddScoped<RenamePageHandler>();
        // Page organization (spec section 4): the shared handlers the organization commands run in their own transaction.
        services.TryAddScoped<OrganizationLocks>();
        services.TryAddScoped<MovePageHandler>();
        services.TryAddScoped<SortTreeLevelHandler>();
        services.TryAddScoped<MovePageToSpaceHandler>();
        services.TryAddScoped<TrashPageHandler>();
        services.TryAddScoped<UntrashPageHandler>();
        services.TryAddScoped<ListPageTrashHandler>();
        services.TryAddScoped<McpOrganizationGuard>();
        // atlas_get_space_tree: the web's tree read, run inside GetMcpSpaceTree's transaction.
        services.TryAddScoped<GetPageTreeHandler>();
        // atlas_get_labels, atlas_set_decoration, atlas_set_tags, atlas_apply_labels: the shared decoration and tag handlers behind G5.
        services.TryAddScoped<PageDecorationReader>();
        services.TryAddScoped<PageDecorationHandler>();
        services.TryAddScoped<PageTagStore>();
        services.TryAddScoped<PageTagsHandler>();
        // atlas_apply_labels runs the MCP decoration and tag handlers per change inside its own transaction.
        services.TryAddScoped<SetMcpDecorationHandler>();
        // atlas_get_publication, atlas_set_publication (3.6-I): the shared publication setting behind G5.
        services.TryAddScoped<Features.Pages.Queries.PagePublicationSettingHandler>();
        services.TryAddScoped<ChangeMcpTagsHandler>();
        // atlas_upload_attachment: the shared attachment store, run inside StoreMcpAttachment's transaction.
        services.TryAddScoped<StoreAttachmentHandler>();
        // atlas_update_embed runs the shared embed change, which runs the shared save.
        services.TryAddScoped<EditPageEmbedHandler>();
        // atlas_publish: the summary of the assistant's publication card.
        services.TryAddScoped<McpPublishSummaries>();
        services.TryAddScoped<PublishPageHandler>();
        services.TryAddScoped<RecordApiActivityHandler>();
        return services;
    }

    public static IServiceCollection AddMcp(this IServiceCollection services)
    {
        services.AddSingleton(new ModuleSettingsDescriptor("mcp", "MCP server",
            "Připojte Claude Code a další nástroje s podporou MCP k obsahu wiki přes osobní token. Atlas tu žádný model nevolá.", "mcp", 27, false));
        services.AddMcpModuleState();
        services.AddMcpContent();
        services.TryAddSingleton<McpSearchLimiter>();
        services.AddSingleton<McpToolScopes>();
        // Tool arguments and results never reach the log; the SDK's own categories keep only warnings and errors.
        services.Configure<LoggerFilterOptions>(options => options.AddFilter("ModelContextProtocol", LogLevel.Warning));
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "atlas", Title = "Atlas", Version = AtlasVersion.Current };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(McpToolScopes.ListFilter);
                filters.AddCallToolFilter(McpToolScopes.CallFilter);
            })
            .WithTools<McpReadTools>()
            .WithTools<McpDraftTools>()
            .WithTools<McpOrganizationTools>()
            .WithTools<McpLabelTools>()
            .WithTools<McpPublicationTools>()
            .WithTools<McpOverviewTools>()
            .WithTools<McpPublishTools>()
            .WithTools<McpAttachmentTools>()
            .WithTools<McpRelationTools>()
            .WithTools<McpSyntaxTools>()
            .WithTools<McpLinkingTools>()
            .WithTools<ObjectTrashMcpTools>()
            .WithTools<Atlas.Server.Features.Jobs.JobMcpTools>()
            .WithTools<Atlas.Server.Features.AppLogs.AppLogMcpTools>()
            .WithTools<Atlas.Server.Features.Entities.EntityMcpTools>()
            .WithTools<Atlas.Server.Features.Contacts.ContactMcpTools>()
            .WithTools<Atlas.Server.Features.Skills.SkillMcpTools>()
            .WithTools<NotificationMcpTools>()
            .WithTools<Atlas.Server.Features.PageVerifications.PageVerificationMcpTools>()
            .WithTools<Atlas.Server.Features.Comments.CommentMcpTools>();
        return services;
    }

    /// <summary>In front of authentication: the origin guard and the module gate for /mcp (AtlasPipeline).</summary>
    public static void UseMcpGate(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(McpAddress.Path))
            {
                await next();
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (!McpOriginGuard.IsAllowed(context.Request, context.RequestServices.GetRequiredService<McpAddress>()))
            {
                await ProblemResponse.WriteAsync(context, StatusCodes.Status403Forbidden, "Požadavek z prohlížeče na /mcp není povolený.", "origin_forbidden");
                return;
            }
            if (!await context.RequestServices.GetRequiredService<McpModuleState>().IsEnabledAsync(context.RequestAborted))
            {
                // 404 rather than spec §8's 503: a switched-off module does not announce an endpoint (spec §4, §10).
                await ProblemResponse.WriteAsync(context, StatusCodes.Status404NotFound, "MCP server není zapnutý.", "mcp_disabled");
                return;
            }
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                // Stateless (D9): no event stream (GET) and no session to end (DELETE). The SDK maps neither in this mode, so
                // without this the SPA fallback would answer a GET with index.html.
                context.Response.Headers.Allow = "POST";
                await ProblemResponse.WriteAsync(context, StatusCodes.Status405MethodNotAllowed, "MCP server přijímá jen POST.", "method_not_allowed");
                return;
            }
            if (context.Request.ContentLength > MaximumRequestBytes)
            {
                // Refused unread and worded for the person behind the client: the usual cause is an upload over the MCP limit.
                await ProblemResponse.WriteAsync(context, StatusCodes.Status413PayloadTooLarge,
                    "Požadavek MCP je větší než 3 000 000 B. Přes MCP lze nahrát soubor nejvýše 2 MiB; obrázky do 10 MiB a přílohy do 25 MiB " +
                    "nahrajte přes REST: " +
                    "POST /api/v1/pages/{pageId}/attachments (multipart/form-data).", "request_too_large");
                return;
            }
            await next();
        });
    }

    public static void MapMcpServer(this IEndpointRouteBuilder app)
    {
        app.MapMcp(McpAddress.Path)
            .RequireAuthorization(PersonalApiEndpoints.McpReadPolicy)
            .RequireRateLimiting("personal-api")
            .WithMetadata(new RequestSizeLimitAttribute(MaximumRequestBytes), PersonalApiConcurrency.Mcp);
        McpEndpoints.MapMcpAdministration(app);
    }
}
