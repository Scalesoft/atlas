using System.Reflection;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// 3.6-H (AI parity): every MCP tool with the write scope that writes a page's content runs as a live write (<see cref="McpLiveWrites"/>),
/// so a page open in live editing gets the change as a collaborator's update. The others write nothing of an existing page's document;
/// each says why. Listed, not derived: a new write tool fails here until it is classified.
/// </summary>
[Trait("Category", "Guard")]
public sealed class McpLiveWriteCoverageTests
{
    private static readonly Dictionary<string, string> NotPageContent = new(StringComparer.Ordinal)
    {
        ["atlas_create_draft"] = "a new page has no live document yet",
        ["projector_create_deployment"] = "a new page has no live document yet",
        ["atlas_rename_page"] = "the title is not in the live document (its own revision, no new generation)",
        ["atlas_apply_labels"] = "tags and decoration live beside the document",
        ["atlas_set_tags"] = "tags live beside the document",
        ["atlas_set_decoration"] = "the icon and cover live beside the document",
        ["atlas_upload_attachment"] = "adds a file to the page; inserting it is a content write",
        ["projector_set_catalog_page"] = "a setting of the page, not its content",
        ["atlas_mark_notifications_read"] = "no page",
        ["atlas_restore_version"] = "a restore stays a write without a generation (3.6-H), refused while anyone has unsaved edits",
        ["atlas_name_version"] = "names a point of the history; the content does not change",
        // The former publish and direct tools, write since 3.6: they change the tree, a publication, objects or boards, not a page's document.
        ["atlas_publish"] = "publishes a version; the document does not change",
        ["atlas_set_publication"] = "a setting of the page, not its content",
        ["atlas_set_verification"] = "a setting of the page (its steward and verification), not its content",
        ["atlas_report_outdated"] = "a notification to the steward; the page does not change",
        ["atlas_move_page"] = "the tree, not the document",
        ["atlas_arrange_pages"] = "the tree, not the document",
        ["atlas_sort_pages"] = "the tree, not the document",
        ["atlas_move_page_to_space"] = "the tree, not the document",
        ["atlas_trash_page"] = "the tree, not the document",
        ["atlas_restore_page"] = "the tree, not the document",
        ["atlas_add_tree_reference"] = "the tree, not the document",
        ["atlas_remove_tree_reference"] = "the tree, not the document",
        ["atlas_move_tree_reference"] = "the tree, not the document",
        ["atlas_create_note"] = "a personal note is an object, not a page",
        ["atlas_edit_note"] = "a personal note is an object, not a page",
        ["atlas_trash_note"] = "a personal note is an object, not a page",
        ["atlas_restore_object"] = "an object from the trash, not a page",
        ["atlas_upsert_contact"] = "a contact is an object, not a page",
        ["atlas_merge_contacts"] = "a contact is an object, not a page",
        ["atlas_rate_skill"] = "a person's skill is a rating of a contact, not a page",
        ["atlas_merge_topics"] = "the topic catalog (tags), not a page document",
        ["atlas_link_topics"] = "the topic catalog (tags), not a page document",
        ["atlas_create_board"] = "a board has its own operations, not a page document",
        ["atlas_edit_board"] = "a board has its own operations, not a page document",
        ["atlas_delete_board"] = "a board has its own operations, not a page document",
        ["atlas_move_board"] = "a board has its own operations, not a page document",
        ["atlas_untrash_board"] = "a board has its own operations, not a page document",
        ["projector_compose_board"] = "creates a board, not a page document",
        ["atlas_run_job"] = "no page",
        ["atlas_cancel_job_run"] = "no page",
    };

    [Fact]
    public void Every_write_tool_is_a_live_write_or_writes_no_page_content()
    {
        var draft = typeof(McpLiveWrites).Assembly.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpToolScopeAttribute>()?.Scope == ApiScopes.Write)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name!).ToHashSet(StringComparer.Ordinal);

        var unclassified = draft.Where(name => !McpLiveWrites.Tools.Contains(name) && !NotPageContent.ContainsKey(name)).ToArray();
        Assert.True(unclassified.Length == 0, "Write tools neither live writes nor listed as writing no page content: " + string.Join(", ", unclassified));
        var stale = McpLiveWrites.Tools.Concat(NotPageContent.Keys).Where(name => !draft.Contains(name)).ToArray();
        Assert.True(stale.Length == 0, "Listed tools that are no write tools: " + string.Join(", ", stale));
        Assert.DoesNotContain(NotPageContent.Keys, McpLiveWrites.Tools.Contains);
    }
}
