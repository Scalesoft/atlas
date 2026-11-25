using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Contacts.Commands;
using Atlas.Server.Features.Jobs.Commands;
using Atlas.Server.Features.Notifications.Commands;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.PersonalItems.Commands;
using Atlas.Server.Features.Projector.Commands;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Modules;

/// <summary>
/// Owner's rule (Projector v1 deployments spec §4): everything a user can do with the objects of a V3 module, AI can do over MCP and the
/// public API. Every command record of a listed command namespace has an MCP tool and an /api/v1 route, or is a security change in
/// <see cref="Exclusions"/> with the reason. Listed, not derived: a new command fails here until someone names its tool and route.
/// </summary>
[Trait("Category", "Guard")]
public sealed class ModuleCommandCoverageTests
{
    /// <summary>The command namespaces of V3 modules; add one when a module gets user commands.</summary>
    private static readonly string[] CommandNamespaces = [typeof(SetCatalogPage).Namespace!, typeof(AddTreeReference).Namespace!, typeof(ApplyBoardBatch).Namespace!,
        typeof(CreateObject).Namespace!, typeof(SetItemGrant).Namespace!, typeof(MarkNotificationsRead).Namespace!, typeof(MergeContacts).Namespace!, typeof(RunJobNow).Namespace!, typeof(NameVersion).Namespace!,
        typeof(Atlas.Server.Features.Skills.Commands.RateSkill).Namespace!, typeof(Atlas.Server.Features.PageVerifications.Commands.SetPageVerification).Namespace!,
        typeof(Atlas.Server.Features.Comments.Commands.PostComment).Namespace!];

    /// <summary>3.6 commands of a page from namespaces that also hold the platform's own records (the collaboration relay, the page save).</summary>
    private static readonly Type[] PageCommands = [typeof(UndoRevisionChange), typeof(SetPagePublication), typeof(SortTreeLevel)];

    private static readonly Dictionary<Type, (string Tool, string Route)> Covered = new()
    {
        [typeof(SetCatalogPage)] = ("projector_set_catalog_page", "PUT /api/v1/projector/pages/{pageId:guid}/catalog"),
        [typeof(CreateDeploymentFromPage)] = ("projector_create_deployment", "POST /api/v1/projector/pages/{pageId:guid}/deployments"),
        [typeof(UpdateRequirement)] = ("projector_update_requirement", "PATCH /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}"),
        [typeof(UpdateRequirements)] = ("projector_update_requirements", "PATCH /api/v1/projector/pages/{pageId:guid}/requirements"),
        [typeof(InsertRequirements)] = ("projector_insert_requirements", "POST /api/v1/projector/pages/{pageId:guid}/requirements"),
        [typeof(CreateRequirement)] = ("projector_create_requirement", "POST /api/v1/projector/pages/{pageId:guid}/requirements/new"),
        [typeof(InsertFromDeployment)] = ("projector_insert_from_deployment", "POST /api/v1/projector/pages/{pageId:guid}/requirements/from-deployment"),
        [typeof(MoveRequirement)] = ("projector_move_requirement", "POST /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}/move"),
        [typeof(RemoveRequirement)] = ("projector_remove_requirement", "DELETE /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}"),
        [typeof(AddTreeReference)] = ("atlas_add_tree_reference", "POST /api/v1/spaces/{spaceId:guid}/tree/references"),
        [typeof(RemoveTreeReference)] = ("atlas_remove_tree_reference", "DELETE /api/v1/tree/references/{id:guid}"),
        [typeof(MoveTreeReference)] = ("atlas_move_tree_reference", "POST /api/v1/tree/references/{id:guid}/move"),
        [typeof(CreateBoard)] = ("atlas_create_board", "POST /api/v1/spaces/{spaceId:guid}/boards"),
        [typeof(ApplyBoardBatch)] = ("atlas_edit_board", "POST /api/v1/boards/{boardId:guid}/operations"),
        [typeof(DeleteBoard)] = ("atlas_delete_board", "DELETE /api/v1/boards/{boardId:guid}"),
        [typeof(MoveBoard)] = ("atlas_move_board", "POST /api/v1/boards/{boardId:guid}/move"),
        [typeof(UntrashBoard)] = ("atlas_untrash_board", "POST /api/v1/boards/{boardId:guid}/untrash"),
        // The object path; the personal note is its type with tools of its own (spec 2026-10-29 R5).
        [typeof(CreateObject)] = ("atlas_create_note", "POST /api/v1/objects"),
        [typeof(ApplyObjectChange)] = ("atlas_edit_note", "POST /api/v1/objects/{objectId:guid}/changes"),
        [typeof(TrashObject)] = ("atlas_trash_note", "DELETE /api/v1/objects/{objectId:guid}"),
        [typeof(RestoreObject)] = ("atlas_restore_object", "POST /api/v1/objects/{objectId:guid}/restore"),
        [typeof(MarkNotificationsRead)] = ("atlas_mark_notifications_read", "POST /api/v1/notifications/{id:guid}/read"),
        // The contact as one entity (contacts spec 2026-10-30): the link by identity is the upsert, the merge waits for a person.
        [typeof(ResolveContact)] = ("atlas_upsert_contact", "POST /api/v1/contacts/resolve"),
        [typeof(MergeContacts)] = ("atlas_merge_contacts", "POST /api/v1/contacts/merge"),
        // The logo or photo of a contact (3.5 package G): the upsert's photoBase64.
        [typeof(SetContactPhoto)] = ("atlas_upsert_contact", "PUT /api/v1/contacts/{id:guid}/photo"),
        // Background jobs (3.3 plan §4.7): run now and retry are one tool, atlas_run_job with retryRunId.
        [typeof(RunJobNow)] = ("atlas_run_job", "POST /api/v1/jobs/{jobId}/runs"),
        [typeof(RetryJobRun)] = ("atlas_run_job", "POST /api/v1/jobs/{jobId}/runs/{runId:long}/retry"),
        [typeof(CancelJobRun)] = ("atlas_cancel_job_run", "POST /api/v1/jobs/{jobId}/runs/{runId:long}/cancel"),
        // Every account's contact (R3): the admin's link or dismissal of a suggested pair is the merge tool with accountId.
        [typeof(ResolveAccountContact)] = ("atlas_merge_contacts", "POST /api/v1/contacts/accounts"),
        // A page's history (3.6-C, 3.6-H): restore a version, name one, undo one change; and its „Publikace“ (3.6-I).
        [typeof(RestorePage)] = ("atlas_restore_version", "POST /api/v1/pages/{id:guid}/restore"),
        [typeof(NameVersion)] = ("atlas_name_version", "POST /api/v1/pages/{id:guid}/versions"),
        // Mapa znalostí (skills spec §6): the rating, the merge of two topics and their links.
        [typeof(Atlas.Server.Features.Skills.Commands.RateSkill)] = ("atlas_rate_skill", "PUT /api/v1/contacts/{id:guid}/skills"),
        [typeof(Atlas.Server.Features.Skills.Commands.MergeTopics)] = ("atlas_merge_topics", "POST /api/v1/skills/topics/{id:guid}/merge"),
        [typeof(Atlas.Server.Features.Skills.Commands.LinkTopics)] = ("atlas_link_topics", "PUT /api/v1/skills/topics/{id:guid}/parents"),
        [typeof(UndoRevisionChange)] = ("atlas_undo_change", "POST /api/v1/pages/{id:guid}/history/{revisionId:guid}/undo"),
        [typeof(SetPagePublication)] = ("atlas_set_publication", "PUT /api/v1/pages/{id:guid}/publication"),
        // 5.2 Ověřený obsah (plan §7).
        [typeof(Atlas.Server.Features.PageVerifications.Commands.SetPageVerification)] = ("atlas_set_verification", "PUT /api/v1/pages/{id:guid}/verification"),
        [typeof(Atlas.Server.Features.PageVerifications.Commands.SetSpaceVerification)] = ("atlas_set_verification", "POST /api/v1/spaces/{spaceId:guid}/content-health/bulk"),
        [typeof(Atlas.Server.Features.PageVerifications.Commands.ReportPageOutdated)] = ("atlas_report_outdated", "POST /api/v1/pages/{id:guid}/verification/report"),
        // 5.2: discussions are the collaboration channel (the page chat): an agent reads them (atlas_list_comments), answers, corrects
        // its own comment and deletes one as the web lets its owner (the author, or the object's manager).
        [typeof(Atlas.Server.Features.Comments.Commands.PostComment)] = ("atlas_post_comment", "POST /api/v1/objects/{objectId:guid}/comments"),
        [typeof(Atlas.Server.Features.Comments.Commands.EditComment)] = ("atlas_edit_comment", "PUT /api/v1/objects/{objectId:guid}/comments/{commentId:guid}"),
        [typeof(Atlas.Server.Features.Comments.Commands.DeleteComment)] = ("atlas_delete_comment", "DELETE /api/v1/objects/{objectId:guid}/comments/{commentId:guid}"),
        // „Řadit abecedně“ of a tree level (3.6).
        [typeof(SortTreeLevel)] = ("atlas_sort_pages", "POST /api/v1/spaces/{spaceId:guid}/tree/sort"),
    };

    /// <summary>Security changes (rights, sharing, module and security settings, secrets, backup keys) with the reason.</summary>
    private static readonly Dictionary<Type, string> Exclusions = new()
    {
        [typeof(SetItemGrant)] = "Sharing a personal item is a grant: a security change AI does not make (owner, 27. 10. 2026); the web shares it.",
        [typeof(PurgeObject)] = "A permanent delete is the web's only (unified trash spec, rule 6), as access changes are.",
        [typeof(SetJobPaused)] = "Pausing every trigger of a job is an operational setting, like the module settings: the web's and the API's (3.3 plan §4.7, decision O5).",
        [typeof(SetNotificationPreference)] = "An account's own setting, not an object: the web's only (inbox spec §9, decision 7).",
        [typeof(NamedVersion)] = "The answer of NameVersion, not a command.",
        // 5.2: the MCP tools' own messages, G5 first and then the covered command.
        [typeof(Atlas.Server.Features.PageVerifications.Commands.SetMcpVerification)] = "atlas_set_verification's G5 wrapper of SetPageVerification, not a user command.",
        [typeof(Atlas.Server.Features.PageVerifications.Commands.SetMcpSpaceVerification)] = "atlas_set_verification's G5 wrapper of SetSpaceVerification, not a user command.",
        [typeof(Atlas.Server.Features.PageVerifications.Commands.ReportMcpOutdated)] = "atlas_report_outdated's G5 wrapper of ReportPageOutdated, not a user command.",
    };

    [PostgreSqlFact]
    public async Task Every_module_command_has_an_mcp_tool_and_an_api_route_or_a_reasoned_exclusion()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_coverage_", settings =>
        {
            foreach (var (key, value) in TestModules.AllEnabledSettings)
            {
                settings[key] = value;
            }
        });
        var commands = typeof(SetCatalogPage).Assembly.GetTypes()
            // Records only: the namespace also holds the handlers and their helpers.
            .Where(type => CommandNamespaces.Contains(type.Namespace) && type.GetProperty("EqualityContract", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is not null)
            .Concat(PageCommands).ToArray();
        var tools = site.Services.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);
        var routes = site.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => string.Join(",", e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? []) + " " + e.RoutePattern.RawText)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(commands.Select(x => x.Name).Order(StringComparer.Ordinal), Covered.Keys.Concat(Exclusions.Keys).Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.All(Exclusions.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
        Assert.All(Covered, entry => Assert.True(tools.Contains(entry.Value.Tool), $"{entry.Key.Name}: no MCP tool {entry.Value.Tool}"));
        Assert.All(Covered, entry => Assert.True(routes.Contains(entry.Value.Route), $"{entry.Key.Name}: no route {entry.Value.Route}"));
        // The overview's kinds and actions are the web's (overviewKinds.ts): overview.test.ts fails for one without an entry in the
        // fixture, this one for an entry whose MCP tool the server does not register.
        var overview = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "content", "overview-mcp.json")))!;
        Assert.All(new[] { "kinds", "actions", "insert" }.SelectMany(part => overview[part]!.AsObject()),
            entry => Assert.True(tools.Contains(entry.Value!.GetValue<string>()), $"overview {entry.Key}: no MCP tool {entry.Value}"));
    }
}
