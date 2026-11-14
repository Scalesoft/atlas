using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Platform.Validation;
using ModelContextProtocol.Protocol;

namespace Atlas.Server.Features.Mcp;

/// <summary>
/// Errors a tool returns to the model: a tool result with isError, the Czech message as text and { code, status, message } as
/// structured content (spec §8). Never an internal detail, an argument or page text, and never a hint that an unreadable page exists:
/// the messages are the ones the shared handlers already answer the web and REST with.
/// </summary>
public static class McpErrors
{
    public static CallToolResult From(ApiException error)
    {
        var extensions = new JsonObject();
        foreach (var (key, value) in error.Extensions ?? new Dictionary<string, object?>())
        {
            if (key is not ("code" or "status" or "message") && value is not null)
            {
                extensions[key] = JsonSerializer.SerializeToNode(value);
            }
        }
        return Error(error.Status, error.Code ?? CodeFor(error.Status), Rules.ForApiClient(error.Message), extensions);
    }

    public static CallToolResult InvalidArgument(string message) => Error(400, "invalid_argument", message);

    public static CallToolResult Error(int status, string code, string message, JsonObject? extensions = null)
    {
        message = ProblemResponse.Title(message);
        var structured = new JsonObject { ["code"] = code, ["status"] = status, ["message"] = message };
        foreach (var (key, value) in extensions ?? [])
        {
            structured[key] = value?.DeepClone();
        }
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"Chyba {status} ({code}): {message}" }],
            StructuredContent = JsonSerializer.SerializeToElement(structured)
        };
    }

    public static string CodeFor(int status) => status switch
    {
        400 => "invalid_argument",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        409 => "version_conflict",
        429 => "rate_limited",
        503 => "unavailable",
        _ => "error"
    };
}
