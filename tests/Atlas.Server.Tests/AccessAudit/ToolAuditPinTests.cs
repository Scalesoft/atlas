using Atlas.Server.Features.Ai.Access;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.ApiActivity.Commands;
using Atlas.Server.Features.Mcp.Commands;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// The audit of API, MCP and assistant writes, the MCP switch and the AI access policies, pinned while the database triggers wrote it
/// (the migrations of API activity, AI access policies and the assistant channel). C# writes it since 3.6; the output must not change.
/// </summary>
public sealed class ToolAuditPinTests
{
    [PostgreSqlFact]
    public async Task Api_mcp_assistant_writes_mcp_switch_and_ai_policies_audit_as_the_triggers_did()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_tool_pin_") /* own database: flips the MCP switch and the AI access policies (one row each for the installation) and pins the whole audit log after a mark */;
        var admin = await host.SeedUserAsync("admin", true);
        var owner = await host.SeedUserAsync("owner");
        var group = await host.SeedGroupAsync("Asistenti", owner);
        var tokenId = await TestApiTokens.SeedAsync(host, owner, "Claude Code");
        var mark = await AuditPin.MarkAsync(host.ConnectionString);

        var pageId = Guid.NewGuid();
        await host.InvokeAsync(new RecordApiActivity(owner.Id, tokenId, "api", "pages.update", pageId, null, ApiActivityOutcomes.Ok, 2, "t1"));
        await host.InvokeAsync(new RecordApiActivity(owner.Id, tokenId, "mcp", "atlas_update_section", pageId, null, ApiActivityOutcomes.Ok, 3, "t2"));
        await host.InvokeAsync(new RecordApiActivity(owner.Id, tokenId, "mcp", "atlas_publish", pageId, null, "publish_confirmation_required", null, "t3"));
        await host.InvokeAsync(new RecordApiActivity(owner.Id, null, "assistant", "atlas_update_section", pageId, null, ApiActivityOutcomes.Ok, 4, "t4"));
        await host.InvokeAsync(new RecordApiActivity(owner.Id, null, "assistant", "atlas_publish", pageId, null, ApiActivityOutcomes.Ok, 5, "t5"));
        host.Actor.Current = admin;
        var mcp = await host.InvokeAsync<McpSettingsView>(new SaveMcpSettings(new SaveMcpRequest(true, null)));
        await host.InvokeAsync<McpSettingsView>(new SaveMcpSettings(new SaveMcpRequest(false, mcp.Version)));
        var access = await host.InvokeAsync<AiAccessView>(new GetAiAccess());
        access = await host.InvokeAsync<AiAccessView>(new SaveAiAccess(new([new(group, null, null, null)], access.Version)));
        await host.InvokeAsync<AiAccessView>(new SaveAiAccess(new([new(group, 3, null, null)], access.Version)));
        await host.InvokeAsync<UserAiPolicyView>(new SaveUserAiPolicy(owner.Id, new(null, 1, null, null, null)));

        Assert.Equal(
        [
            "api.write owner ApiActivityId",
            "mcp.write owner ApiActivityId",
            "mcp.publish owner ApiActivityId",
            "assistant.write owner ApiActivityId",
            "assistant.publish owner ApiActivityId",
            "mcp.enabled admin McpSettingId",
            "mcp.disabled admin McpSettingId",
            "ai.group_policy.changed admin GroupAiPolicyId",
            "ai.group_policy.changed admin GroupAiPolicyId",
            "ai.user_policy.changed admin UserAiPolicyId",
        ], await AuditPin.EventsAsync(host.ConnectionString, mark));
    }
}
