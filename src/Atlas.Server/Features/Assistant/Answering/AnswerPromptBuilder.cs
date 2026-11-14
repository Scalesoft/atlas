using System.Text;
using Atlas.Server.Features.Assistant.Prompts;
using Microsoft.Extensions.AI;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>A passed source; its number is its 1-based position in the list given to the builder.</summary>
public sealed record AnswerPromptSource(string PageTitle, string? Heading, string Text);

/// <summary>A previous exchange; answer sentences carry no source numbers (those belong to the old sources). <see cref="Outcome"/>:
/// what became of the agent turn's write proposal (<c>AssistantProposals.Outcome</c>), so the next agent turn knows what was applied.</summary>
public sealed record AnswerHistoryTurn(string Question, IReadOnlyList<string> AnswerSentences, string? Outcome = null);

/// <summary>
/// Builds the model input of one answer (plan §7, R6): the versioned system message, at most three previous exchanges,
/// and the last user message with the question and the sources as delimited blocks. Text of sources and question is
/// escaped (&lt; and &gt; as entities) so a source can neither close its block nor forge another. Pure, logs nothing.
/// </summary>
public static class AnswerPromptBuilder
{
    /// <summary>The most sources of any effort (<see cref="AnswerEffort.Max"/>).</summary>
    public const int MaxSources = 14;
    public const int MaxSourceLength = 2600;
    public const int MaxQuestionLength = 2000;
    public const int MaxHistoryTurns = 3;
    public const int MaxHistoryLength = MaxQuestionLength + AnswerValidator.MaxSentences * AnswerValidator.MaxSentenceLength;

    public static IReadOnlyList<ChatMessage> Build(string question, IReadOnlyList<AnswerPromptSource> sources, IReadOnlyList<AnswerHistoryTurn>? history = null)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, AnswerPrompt.System) };
        foreach (var turn in TrimHistory(history ?? []))
        {
            messages.Add(new ChatMessage(ChatRole.User, Escape(Cut(turn.Question, MaxQuestionLength))));
            messages.Add(new ChatMessage(ChatRole.Assistant, string.Join(" ", turn.AnswerSentences)));
        }
        messages.Add(new ChatMessage(ChatRole.User, UserMessage(question, sources)));
        return messages;
    }

    public static string UserMessage(string question, IReadOnlyList<AnswerPromptSource> sources)
    {
        var text = new StringBuilder().Append("Otázka: ").AppendLine(Escape(Cut(question, MaxQuestionLength))).AppendLine();
        for (var i = 0; i < Math.Min(sources.Count, MaxSources); i++)
        {
            var source = sources[i];
            text.Append("<zdroj n=\"").Append(i + 1).Append("\" stránka=\"").Append(Attribute(source.PageTitle))
                .Append("\" sekce=\"").Append(Attribute(source.Heading ?? "")).AppendLine("\">")
                .AppendLine(Escape(Cut(source.Text, MaxSourceLength))).AppendLine("</zdroj>");
        }
        return text.ToString();
    }

    /// <summary>History is trimmed first: the last three exchanges, the oldest dropped until at least one maximum valid exchange fits.</summary>
    private static List<AnswerHistoryTurn> TrimHistory(IReadOnlyList<AnswerHistoryTurn> history)
    {
        var turns = history.TakeLast(MaxHistoryTurns).ToList();
        while (turns.Count > 0 && turns.Sum(turn => turn.Question.Length + turn.AnswerSentences.Sum(sentence => sentence.Length)) > MaxHistoryLength)
        {
            turns.RemoveAt(0);
        }
        return turns;
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    internal static string Escape(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Attribute(string text) => Escape(text).Replace("\"", "&quot;").Replace("\r", " ").Replace("\n", " ");
}
