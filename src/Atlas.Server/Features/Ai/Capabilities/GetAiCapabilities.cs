namespace Atlas.Server.Features.Ai.Capabilities;

/// <summary>
/// <c>GET /api/ai/capabilities</c> for a signed-in session (cookie), <c>Cache-Control: no-store</c>. Answered from memory, not
/// through Wolverine: every Wolverine message opens a database transaction, and the client asks every minute. Not mapped for
/// shared pages (<c>/s/…</c> visitors have no session) nor in the personal API.
/// </summary>
public static class GetAiCapabilities
{
    public const string Route = "/api/ai/capabilities";

    public static async Task<IResult> HandleAsync(HttpContext context, AiCapabilities capabilities, Access.AiUserAccess access, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var policy = await access.PolicyAsync(ct);
        if (!policy.Enabled) return Results.Ok(new AiCapabilitiesDto(false, false, false, false, false, false));
        var available = await capabilities.GetAsync(ct);
        // D17: the agent needs the task and the "AI s nástroji" right.
        return Results.Ok(available with { Agent = available.Agent && policy.ToolsEnabled });
    }
}
