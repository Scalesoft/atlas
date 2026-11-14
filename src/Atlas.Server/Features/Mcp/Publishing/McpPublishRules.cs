namespace Atlas.Server.Features.Mcp.Publishing;

/// <summary>The refusal of a publication over MCP or the assistant that its caller may not make: 403 publish_confirmation_required.</summary>
public static class McpPublishRules
{
    public const string ConfirmationRequired = "publish_confirmation_required";

    public static ApiException Refusal(string message) => new(403, message, ConfirmationRequired);
}
