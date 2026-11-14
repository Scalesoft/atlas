using System.Security.Claims;

namespace Atlas.Server.Platform.Identity;

/// <summary>
/// Reads the channel like <see cref="HttpCurrentActor"/> reads the actor: from the authenticated principal of the current request.
/// An identity of the personal API scheme is api, or mcp under /mcp; any other request is web (or assistant, below); work without an
/// HttpContext is system.
/// The token id comes from the "api_token_id" claim; an "api_channel" claim, when the scheme sets one, must agree with the path.
/// A request carrying <see cref="AssistantMarker"/> is assistant: only the assistant's tool session sets it, never anything the client sends.
/// </summary>
public sealed class HttpRequestChannel : IRequestChannel
{
    /// <summary>Atlas.Server.Features.PersonalApi.PersonalApiAuthentication.SchemeName; Platform does not reference the feature.</summary>
    public const string PersonalApiScheme = "Atlas.PersonalApi";
    public const string McpPath = "/mcp";

    /// <summary>The <see cref="HttpContext.Items"/> key the assistant's tool session (Features/Assistant/Agent) sets on its request.
    /// An object key: no header, form or route value can produce it.</summary>
    public static readonly object AssistantMarker = new();

    /// <summary>The <see cref="HttpContext.Items"/> key set only while the assistant runs a proposal its owner confirmed (spec §6.3,
    /// WP-20); <c>atlas_publish</c> on the channel assistant may publish only with it (WP-41). An object key, like <see cref="AssistantMarker"/>.</summary>
    public static readonly object ApprovedProposalMarker = new();

    /// <summary>The <see cref="HttpContext.Items"/> key of the assistant conversation whose confirmed proposal runs (3.6-H attribution).</summary>
    public static readonly object AssistantConversation = new();

    private readonly IHttpContextAccessor m_httpContextAccessor;

    public HttpRequestChannel(IHttpContextAccessor httpContextAccessor)
    {
        m_httpContextAccessor = httpContextAccessor;
    }

    public RequestChannel Current
    {
        get
        {
            var context = m_httpContextAccessor.HttpContext;
            if (context is null)
            {
                return ActingAccount.Channel ?? RequestChannel.System;
            }
            var identity = context.User.Identities.FirstOrDefault(x => x.IsAuthenticated && x.AuthenticationType == PersonalApiScheme);
            var assistant = context.Items.ContainsKey(AssistantMarker);
            if (identity is null)
            {
                return assistant ? RequestChannel.Assistant with { ConversationId = context.Items[AssistantConversation] as Guid? } : RequestChannel.Web;
            }
            if (assistant)
            {
                // Fail closed: the assistant runs as a browser session, so a token request with its marker is attributable to neither.
                throw new ApiException(401, "Platný osobní API token je vyžadován.");
            }
            if (!Guid.TryParse(identity.FindFirst("api_token_id")?.Value, out var tokenId))
            {
                // Fail closed: a personal API identity without its token cannot be attributed.
                throw new ApiException(401, "Platný osobní API token je vyžadován.");
            }
            var mcp = context.Request.Path.StartsWithSegments(McpPath);
            var claimed = identity.FindFirst("api_channel")?.Value;
            if (claimed is not null && claimed != (mcp ? "mcp" : "rest"))
            {
                throw new ApiException(401, "Platný osobní API token je vyžadován.");
            }
            return new RequestChannel(mcp ? ContentChannel.Mcp : ContentChannel.Api, tokenId);
        }
    }
}
