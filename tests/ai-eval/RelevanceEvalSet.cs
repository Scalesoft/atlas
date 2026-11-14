using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.AiEval;

/// <summary>One query of a relevance set (see tests/ai-eval/relevance/README.md). <paramref name="Expected"/>: backup paths of the
/// pages that answer it (any one counts); empty for a question the wiki cannot answer. <paramref name="Section"/>: the heading
/// where the answer is, when it matters. <paramref name="Ask"/>: also asked as a whole-wiki agent turn.</summary>
public sealed record RelevanceEvalCase(string Id, string Category, string Query, IReadOnlyList<string> Expected, string? Section,
    bool Ask = false, string? Source = null);

public sealed record RelevanceEvalSet(string Set, string? Status, IReadOnlyList<RelevanceEvalCase> Cases);

/// <summary>A set over real wiki content names real pages, so it stays outside Git; the shape is checked here, on every load,
/// instead of in a repository test.</summary>
public static partial class RelevanceEvalSets
{
    public static readonly IReadOnlySet<string> Categories = new HashSet<string>
    {
        "title", "identifier", "inflection", "question-en", "paraphrase", "unanswerable",
    };

    public static RelevanceEvalSet Load(string path)
    {
        var set = JsonSerializer.Deserialize<RelevanceEvalSet>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"'{path}' is not a relevance set.");
        var problems = new List<string>();
        problems.AddRange(set.Cases.GroupBy(item => item.Id).Where(group => group.Count() > 1).Select(group => $"duplicate id {group.Key}"));
        problems.AddRange(set.Cases.GroupBy(item => AnswerEvalSets.Normalize(item.Query)).Where(group => group.Count() > 1).Select(group => $"duplicate query '{group.Key}'"));
        foreach (var item in set.Cases)
        {
            if (!Categories.Contains(item.Category)) problems.Add($"{item.Id}: unknown category {item.Category}");
            if ((item.Category == "unanswerable") != (item.Expected.Count == 0)) problems.Add($"{item.Id}: only unanswerable cases expect no page");
            if (item.Category == "unanswerable" && !item.Ask) problems.Add($"{item.Id}: an unanswerable case is only measured as an agent turn (ask)");
            problems.AddRange(item.Expected.Where(expected => !BackupPath().IsMatch(expected)).Select(expected => $"{item.Id}: '{expected}' is not a backup path"));
        }
        return problems.Count == 0 ? set : throw new InvalidDataException($"'{path}': {string.Join("; ", problems)}");
    }

    [GeneratedRegex("^[a-z0-9-]+(/[a-z0-9-]+)+$")]
    private static partial Regex BackupPath();
}
