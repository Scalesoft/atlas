using System.Text.Json;

namespace Atlas.AiEval;

/// <summary>One evaluation case (see tests/ai-eval/answer/README.md).</summary>
public sealed record AnswerEvalCase(string Question, bool Answerable, IReadOnlyList<string> Expected, string? Forbidden, bool Synthetic = false);

public sealed record AnswerEvalSet(string Set, string? Status, IReadOnlyList<AnswerEvalCase> Cases)
{
    public bool IsHeldOut => Set == "test";
}

public sealed class HeldOutSetException(string message) : InvalidOperationException(message);

/// <summary>
/// Loads evaluation sets. Shared source (linked into the prototype and the server tests) so the held-out guard that
/// protects the test set while tuning is the same code in the runner and in the tests. A held-out set (declared
/// <c>"set": "test"</c> or named <c>test.json</c>) loads only with an explicit <c>allowHeldOut</c>.
/// </summary>
public static class AnswerEvalSets
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static AnswerEvalSet Load(string path, bool allowHeldOut = false)
    {
        if (!allowHeldOut && Path.GetFileNameWithoutExtension(path).Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new HeldOutSetException($"'{path}' is the held-out test set; it is not used while tuning (pass --held-out true for a release evaluation).");
        }
        var text = File.ReadAllText(path);
        var set = text.TrimStart().StartsWith('[')
            ? new AnswerEvalSet("tuning", null, JsonSerializer.Deserialize<AnswerEvalCase[]>(text, Options) ?? [])
            : JsonSerializer.Deserialize<AnswerEvalSet>(text, Options) ?? throw new InvalidDataException($"'{path}' is not an evaluation set.");
        if (!allowHeldOut && set.IsHeldOut)
        {
            throw new HeldOutSetException($"'{path}' declares the held-out set \"test\"; it is not used while tuning (pass --held-out true for a release evaluation).");
        }
        return set;
    }

    public static string Normalize(string question) => string.Join(' ', question.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('?', '.', '!');
}
