using System.Text;
using Microsoft.Extensions.AI;
using Atlas.Server.Features.Assistant.Sources;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>Whole-page coverage, in section rounds and document order, within the unchanged 24 000-character prompt budget.</summary>
public static class SummaryPrompt
{
    public const string Version = "summary@1";
    public const int Budget = 24_000;
    public static string System
    {
        get
        {
            using var stream = typeof(SummaryPrompt).Assembly.GetManifestResourceStream("Atlas.Server.Features.Assistant.Prompts.summary.system.v1.txt")
                ?? throw new InvalidOperationException("Summary prompt is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
        }
    }

    public static IReadOnlyList<ChatMessage> Build(IReadOnlyList<AnswerPromptSource> sources)
        => [new(ChatRole.System, System), new(ChatRole.User, UserMessage(sources))];

    public static IReadOnlyList<AuthorizedSource> Select(IReadOnlyList<AuthorizedSource> sources)
    {
        var sections = sources.GroupBy(x => x.HeadingId ?? x.HeadingTitle ?? "").Select(x => new Queue<AuthorizedSource>(x)).ToArray();
        var selected = new List<AuthorizedSource>();
        while (sections.Any(x => x.Count > 0))
        {
            foreach (var section in sections.Where(x => x.Count > 0))
            {
                var source = section.Dequeue();
                var trial = selected.Append(source).Select(x => new AnswerPromptSource(x.Title, x.HeadingTitle, x.Text)).ToArray();
                if (System.Length + UserMessage(trial).Length <= Budget) selected.Add(source);
            }
        }
        return selected;
    }

    private static string UserMessage(IReadOnlyList<AnswerPromptSource> sources)
    {
        var text = new StringBuilder("Shrň pouze následující poskytnuté části publikované stránky.\n\n");
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            text.Append("<zdroj n=\"").Append(index + 1).Append("\" stránka=\"").Append(Attribute(source.PageTitle))
                .Append("\" sekce=\"").Append(Attribute(source.Heading ?? "")).AppendLine("\">")
                .AppendLine(AnswerPromptBuilder.Escape(source.Text)).AppendLine("</zdroj>");
        }
        return text.ToString();
    }
    private static string Attribute(string text) => AnswerPromptBuilder.Escape(text).Replace("\"", "&quot;").Replace("\r", " ").Replace("\n", " ");
}
