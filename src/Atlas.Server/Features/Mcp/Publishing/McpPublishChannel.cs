namespace Atlas.Server.Features.Mcp.Publishing;

/// <summary>Shared checks of the tool's two steps: the call came over /mcp with a token (decided by the server), and G5.</summary>
public static class McpPublishChannel
{
    /// <summary>The token of the current mcp request; anything else may not publish over MCP.</summary>
    public static Guid RequireToken(IRequestChannel channel)
    {
        var current = channel.Current;
        return current.IsMcp && current.ApiTokenId is { } tokenId
            ? tokenId
            : throw new ApiException(403, "Publikovat přes MCP lze jen tokenem s kanálem MCP.", McpPublishRules.ConfirmationRequired);
    }

    /// <summary>
    /// WP-41, spec §6.3: the assistant publishes only while it runs a proposal
    /// its owner confirmed (<see cref="HttpRequestChannel.ApprovedProposalMarker"/>, set by nothing but the decision executor). Any other
    /// request, the assistant's own turn included, is 403 <c>publish_confirmation_required</c>: fail closed.
    /// </summary>
    public static void RequireApprovedProposal(IRequestChannel channel, IHttpContextAccessor http)
    {
        if (channel.Current.Channel != ContentChannel.Assistant || http.HttpContext?.Items.ContainsKey(HttpRequestChannel.ApprovedProposalMarker) != true)
        {
            throw new ApiException(403, "Asistent publikuje jen návrh, který uživatel potvrdil na kartě v panelu.", McpPublishRules.ConfirmationRequired);
        }
    }

    /// <summary>G5: a page of a space excluded from AI and MCP answers as a missing page.</summary>
    public static async Task RequireInScopeAsync(McpVisibility visibility, Guid spaceId, CancellationToken ct)
    {
        try
        {
            await visibility.RequireSpaceAsync(spaceId, ct);
        }
        catch (ApiException)
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
    }
}
