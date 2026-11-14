using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Features.Mcp;

/// <summary>The token scope a tool needs (read, draft, publish). A tool without it is never listed and never runs.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolScopeAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}

/// <summary>
/// Scopes of tools, decided per request on the principal of that request (the effective scopes within the owner's current level,
/// T1), so a lowered ceiling applies to the very next call without restarting the client.
/// <para>
/// Not the SDK's [Authorize] filters (S0 2c): they hide a tool from tools/list too, but a direct call then fails with the JSON-RPC
/// error -32600 instead of a tool result, so the model never learns why. Here a tool outside the token's scopes is left out of
/// tools/list, and calling it anyway returns the tool error scope_missing worded as the REST 403 (missing scope vs. lowered ceiling).
/// The decision uses the MCP policies (McpRead/McpWrite: scope + channel mcp), the same ones the endpoint requires.
/// </para>
/// <para>Every call of a tool with the write scope is audited after its result (<see cref="McpWriteAudit"/>, D5).</para>
/// </summary>
public sealed class McpToolScopes
{
    private readonly Dictionary<string, string?> m_scopes;
    private readonly Dictionary<string, JsonElement> m_schemas;

    public McpToolScopes(IEnumerable<McpServerTool> tools)
    {
        m_scopes = tools.ToDictionary(tool => tool.ProtocolTool.Name,
            tool => tool.Metadata.OfType<McpToolScopeAttribute>().FirstOrDefault()?.Scope is { } scope && ApiScopes.All.Contains(scope) ? scope : null,
            StringComparer.Ordinal);
        m_schemas = tools.ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema, StringComparer.Ordinal);
    }

    /// <summary>The input schema of a tool (default for an unknown one), used to name a wrongly typed argument.</summary>
    public JsonElement SchemaOf(string name) => m_schemas.GetValueOrDefault(name);

    /// <summary>The scope the tool needs; null for an unknown tool or one that declares none (never allowed).</summary>
    public string? ScopeOf(string name) => m_scopes.GetValueOrDefault(name);

    public IReadOnlyCollection<string> Names => m_scopes.Keys;

    public static async Task<bool> AllowsAsync(IAuthorizationService authorization, ClaimsPrincipal? user, string? scope)
    {
        return scope is not null && user is not null
            && (await authorization.AuthorizeAsync(user, PersonalApiEndpoints.McpPolicyFor(scope))).Succeeded;
    }

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListFilter(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next)
        => async (request, ct) =>
        {
            var result = await next(request, ct);
            var scopes = request.Services!.GetRequiredService<McpToolScopes>();
            var authorization = request.Services!.GetRequiredService<IAuthorizationService>();
            var allowed = new List<Tool>();
            foreach (var tool in result.Tools)
            {
                if (await AllowsAsync(authorization, request.User, scopes.ScopeOf(tool.Name)))
                {
                    allowed.Add(tool);
                }
            }
            result.Tools = allowed;
            return result;
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallFilter(McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => async (request, ct) =>
        {
            var name = request.Params?.Name ?? "";
            var scopes = request.Services!.GetRequiredService<McpToolScopes>();
            if (!scopes.Names.Contains(name) || !McpWriteAudit.Audits(scopes.ScopeOf(name)))
            {
                return await CallAsync(next, request, scopes, name, ct);
            }
            // A write tool: every attempt is audited after its result (D5), the refused scope and a cancelled call included.
            CallToolResult result;
            try
            {
                result = await CallAsync(next, request, scopes, name, ct);
            }
            catch (OperationCanceledException)
            {
                await McpWriteAudit.RecordAsync(request.Services!, name, request.Params, null, ApiActivityOutcomes.Cancelled);
                throw;
            }
            await McpWriteAudit.RecordAsync(request.Services!, name, request.Params, result);
            return result;
        };

    private static async Task<CallToolResult> CallAsync(McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> request, McpToolScopes scopes, string name, CancellationToken ct)
    {
        if (scopes.Names.Contains(name))
        {
            var scope = scopes.ScopeOf(name);
            var authorization = request.Services!.GetRequiredService<IAuthorizationService>();
            if (!await AllowsAsync(authorization, request.User, scope))
            {
                return McpErrors.Error(403, "scope_missing", request.User is null
                    ? "Token nemá požadovaný rozsah oprávnění."
                    : PersonalApiAuthentication.RefusalTitle(request.User, scope));
            }
        }
        try
        {
            return await McpLiveWrites.CallAsync(request, () => next(request, ct).AsTask(), ct);
        }
        catch (ApiException error)
        {
            return McpErrors.From(error);
        }
        // The SDK binds the arguments before the tool runs: a missing required one or one of a wrong type is the caller's input, not a failure of ours.
        catch (Exception error) when (McpArguments.IsBindingFailure(error))
        {
            return McpErrors.InvalidArgument(McpArguments.BindingMessage(error, scopes.SchemaOf(name), request.Params?.Arguments, name));
        }
        catch (Exception error) when ((error is not OperationCanceledException || !ct.IsCancellationRequested))
        {
            // Never the exception text: an ORM or driver message can quote statement parameters, which hold tool arguments.
            var context = request.Services!.GetService<IHttpContextAccessor>()?.HttpContext;
            request.Services!.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpToolScopes).FullName!)
                .LogError("MCP tool {Tool} failed in request {TraceId} ({ErrorType})", scopes.Names.Contains(name) ? name : "(unknown)",
                    context?.TraceIdentifier, error.GetType().Name);
            return McpErrors.Error(500, "internal_error", "Nástroj se nepodařilo dokončit.");
        }
    }
}
