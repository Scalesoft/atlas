using Atlas.Server.Features.ModuleSettings.Domain;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// The public address of the MCP endpoint and the one browser origin /mcp accepts. Atlas has no general public-address setting; the
/// only configured one is the OIDC public origin (Administration → Moduly → Firemní přihlášení), which the identity provider already
/// calls back. Without it the address is the origin the request came to (the administrator's own browser for the module page).
/// </summary>
public sealed class McpAddress
{
    public const string Path = "/mcp";

    private readonly IHttpContextAccessor? m_http;
    private readonly OidcRuntimeConfiguration? m_oidc;

    public McpAddress(IHttpContextAccessor? http, OidcRuntimeConfiguration? oidc)
    {
        m_http = http;
        m_oidc = oidc;
    }

    /// <summary>The configured public origin ("https://wiki.example.com"), or null when none is configured.</summary>
    public string? ConfiguredOrigin => m_oidc?.PublicOrigin;

    public string EndpointUrl() => Origin() + Path;

    /// <summary>Where a person opens the page in Atlas (the result of a draft tool); relative without a known origin.</summary>
    public string PageUrl(Guid pageId) => Origin() + "/#/pages/" + pageId;

    /// <summary>The web page where the token owner approves or declines a publication over MCP (W4).</summary>

    private string Origin()
    {
        if (ConfiguredOrigin is { } configured)
        {
            return configured;
        }
        var request = m_http?.HttpContext?.Request;
        return request is null || !request.Host.HasValue ? "" : $"{request.Scheme}://{request.Host.Value}";
    }
}
