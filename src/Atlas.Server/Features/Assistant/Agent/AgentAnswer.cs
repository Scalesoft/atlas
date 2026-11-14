using System.Text.RegularExpressions;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Domain;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>The stored answer of an agent turn, its sources (ordinal = position + 1) and the cited ordinals.</summary>
public sealed record AgentAnswerResult(AiStoredAnswer Answer, IReadOnlyList<AssistantReturnedPage> Sources, IReadOnlyList<int> Cited);

/// <summary>
/// The citations of an agent turn (spec §4.1, WP-14). The agent answers in plain text (structured output and tool calls together are
/// unreliable on small models), so its text is stored as the sentences <c>AiMessageView</c> already renders, each cleaned like a
/// validated answer (<see cref="AnswerValidator.Clean"/>). The sources are only pages the tools returned in this turn: every page
/// read with <c>atlas_get_page</c> and every <c>atlas_search</c> hit whose title the answer names (the prompt asks for page titles).
/// A sentence cites the sources whose title it contains. Pure: no database, no model, no logging.
/// </summary>
public static partial class AgentAnswer
{
    public const int MaxSentences = 40;
    public const int MaxSources = 20;

    /// <returns>Null when nothing of the text is left to show (an unusable output).</returns>
    public static AgentAnswerResult? Build(string? text, IReadOnlyList<AssistantReturnedPage> returned)
    {
        var sentences = Split().Split(Markdown().Replace(text ?? "", "")).Select(AnswerValidator.Clean).Where(sentence => sentence.Length > 0)
            .Take(MaxSentences).ToArray();
        if (sentences.Length == 0)
        {
            return null;
        }
        var whole = string.Join(' ', sentences);
        var sources = returned.GroupBy(page => page.PageId)
            .Select(group => group.First() with { Read = group.Any(page => page.Read) })
            .Where(page => page.Read || AnswerValidator.NamesTitle(whole, page.Title))
            .Take(MaxSources).ToArray();
        var stored = sentences.Select(sentence => new AiStoredSentence(sentence,
            sources.Select((page, index) => (page, number: index + 1)).Where(x => AnswerValidator.NamesTitle(sentence, x.page.Title)).Select(x => x.number).ToArray())).ToArray();
        return new AgentAnswerResult(new AiStoredAnswer(true, stored), sources, stored.SelectMany(sentence => sentence.Sources).Distinct().Order().ToArray());
    }

    /// <summary>Lines, and sentences within a line, so one long paragraph is not cut at 600 characters.</summary>
    [GeneratedRegex(@"\s*\n\s*|(?<=[.!?])\s+")]
    private static partial Regex Split();

    /// <summary>Bold, italics underscores and heading marks the panel would show literally.</summary>
    [GeneratedRegex(@"\*\*|__|(?m)^\s{0,3}#{1,6}\s+")]
    private static partial Regex Markdown();
}
