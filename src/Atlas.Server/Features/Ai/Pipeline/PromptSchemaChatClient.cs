using System.Text.Json;
using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>
/// Structured output for every model. A request with a JSON response format gets a JSON answer or fails with
/// <see cref="AiInvalidOutputException"/>. With <c>NativeJsonSchema</c> off (models whose chat template breaks llama.cpp grammar
/// sampling, e.g. EuroLLM) the schema moves from <c>response_format</c> into the system message, and the JSON value is cut
/// out of the prose or code fence around it. A native answer gets the same extraction, so a stray fence does not fail it.
/// </summary>
public sealed class PromptSchemaChatClient(IChatClient inner, AiProfileSnapshot profile) : NonStreamingChatClient(inner)
{
    /// <summary>The instruction that precedes the schema in the fallback's system message.</summary>
    public const string SchemaInstruction = "Odpověz pouze jedním JSON objektem bez dalšího textu a bez bloku kódu, přesně podle tohoto JSON schématu:";

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options?.ResponseFormat is not ChatResponseFormatJson json)
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        if (!profile.NativeJsonSchema && json.Schema is { } schema)
        {
            messages = WithSchemaInPrompt(messages, schema.GetRawText());
            options = options.Clone();
            options.ResponseFormat = null;
        }
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        var value = ExtractJson(response.Text) ?? throw new AiInvalidOutputException();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, value))
        {
            ResponseId = response.ResponseId,
            ConversationId = response.ConversationId,
            ModelId = response.ModelId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            Usage = response.Usage,
        };
    }

    /// <summary>The JSON object (or array) of the answer: the whole text, else the span from the first <c>{</c> to the last
    /// <c>}</c> (prose or a code fence around it), else the same for <c>[ ]</c>. Null when none of them parses.</summary>
    public static string? ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (IsJson(trimmed))
        {
            return trimmed;
        }
        foreach (var (open, close) in new[] { ('{', '}'), ('[', ']') })
        {
            var start = trimmed.IndexOf(open);
            var end = trimmed.LastIndexOf(close);
            if (start >= 0 && end > start && trimmed[start..(end + 1)] is var candidate && IsJson(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool IsJson(string text)
    {
        if (text.Length == 0 || text[0] is not ('{' or '['))
        {
            return false;
        }
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>One system message: some local chat templates (Gemma) accept no more than one.</summary>
    private static List<ChatMessage> WithSchemaInPrompt(IEnumerable<ChatMessage> messages, string schema)
    {
        var list = messages.ToList();
        var instruction = SchemaInstruction + "\n" + schema;
        var index = list.FindIndex(message => message.Role == ChatRole.System);
        if (index < 0)
        {
            list.Insert(0, new ChatMessage(ChatRole.System, instruction));
        }
        else
        {
            list[index] = new ChatMessage(ChatRole.System, list[index].Text + "\n\n" + instruction);
        }
        return list;
    }
}
