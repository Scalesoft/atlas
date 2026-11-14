namespace Atlas.Server.Features.Mcp;

/// <summary>
/// DNS-rebinding protection for /mcp, which the MCP specification requires and SDK 2.2.0 does not provide (S0 point 7). MCP clients
/// (Claude Code, IDEs) send no Origin; a browser sends one with every POST. No browser client of /mcp exists: CORS is not enabled and
/// Atlas's own web never calls it. So a request with an Origin passes only when that origin is Atlas's configured public origin and
/// the Host is that origin's authority; anything else (a foreign page, a rebinding host name that makes Origin and Host agree) is 403.
/// </summary>
public static class McpOriginGuard
{
    public static bool IsAllowed(HttpRequest request, McpAddress address)
    {
        var origins = request.Headers.Origin;
        if (origins.Count == 0)
        {
            return true;
        }
        if (origins.Count != 1 || address.ConfiguredOrigin is not { } configured
            || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) || !Uri.TryCreate(configured, UriKind.Absolute, out var own))
        {
            return false;
        }
        return string.Equals(origin.GetLeftPart(UriPartial.Authority), own.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            && origin.AbsolutePath == "/" && origin.UserInfo.Length == 0
            && string.Equals(request.Host.Value, own.Authority, StringComparison.OrdinalIgnoreCase);
    }
}
