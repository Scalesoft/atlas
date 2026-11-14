using Atlas.Server.Features.Ai.Access;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;

namespace Atlas.Server.Features.Ai.Capabilities;

/// <summary>
/// The door of every interactive AI endpoint: current personal/group access and the global task capabilities; an
/// unavailable task answers <c>503</c> Problem Details with <c>code: "ai_unavailable"</c>, and the client refreshes its
/// capabilities at once. A model call that fails mid-request with an <see cref="AiCallException"/> ends the same way
/// (<c>RequestErrorHandling</c>), never as a 500. Exceptions: the admin's connection test (it must diagnose an unavailable
/// profile) and endpoints with a rule-based part (E2, P3, V5), which leave the vector part out instead.
/// </summary>
public sealed class AiGate(AiCapabilities capabilities, Access.AiUserAccess access)
{
    public const string UnavailableCode = "ai_unavailable";
    public const string UnavailableTitle = "Tahle funkce teď není k dispozici.";

    public const string ToolsForbiddenCode = "ai_tools_forbidden";

    public static ApiException Unavailable() => new(StatusCodes.Status503ServiceUnavailable, UnavailableTitle, UnavailableCode);

    public static ApiException ToolsForbidden()
        => new(StatusCodes.Status403Forbidden, "Asistenta s nástroji vám správce nepovolil.", ToolsForbiddenCode);

    /// <summary>D17: a turn of <c>assistant.agent</c> needs the "AI s nástroji" right, whatever the client sent; every other task
    /// needs access only. For a check inside a transaction that already resolved the policy (the preparation of a turn, the
    /// confirmation of a proposal).</summary>
    /// <exception cref="ApiException">403 <see cref="ToolsForbiddenCode"/> without the right.</exception>
    public static void RequireTools(AiResolvedPolicy policy, string task)
    {
        if (task == AiTaskCatalog.AssistantAgent && !policy.ToolsEnabled) throw ToolsForbidden();
    }

    /// <exception cref="ApiException">503 <see cref="UnavailableCode"/> when the task may not run now, 403
    /// <see cref="ToolsForbiddenCode"/> for a turn of <c>assistant.agent</c> without the right.</exception>
    public async ValueTask RequireAsync(string task, CancellationToken ct)
    {
        var policy = await access.PolicyAsync(ct);
        if (!policy.Enabled) throw Unavailable();
        RequireTools(policy, task);
        await RequireGlobalAsync(task, ct);
    }

    public async ValueTask RequireGlobalAsync(string task, CancellationToken ct)
    {
        if (!await capabilities.IsAvailableAsync(task, ct))
        {
            throw Unavailable();
        }
    }
}
