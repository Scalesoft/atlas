using Atlas.AiEval;
using Atlas.Server.Features.Assistant.Answering;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Plan phase 3 Q7: the held-out test set is never used while tuning, and every tuning case builds a valid prompt.</summary>
public sealed class AnswerEvalSetTests
{
    private static string Path(string file) => System.IO.Path.Combine(AppContext.BaseDirectory, "ai-eval", "answer", file);

    [Fact]
    public void The_held_out_set_is_refused_unless_explicitly_requested_and_is_never_the_default()
    {
        Assert.Throws<HeldOutSetException>(() => AnswerEvalSets.Load(Path("test.json")));

        // The guard follows the declared set too, so renaming the file does not unlock it.
        var renamed = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"held-{Guid.NewGuid():N}.json");
        File.Copy(Path("test.json"), renamed);
        try
        {
            Assert.Throws<HeldOutSetException>(() => AnswerEvalSets.Load(renamed));
            Assert.True(AnswerEvalSets.Load(renamed, allowHeldOut: true).IsHeldOut);
        }
        finally
        {
            File.Delete(renamed);
        }

        Assert.False(AnswerEvalSets.Load(Path("tuning.json")).IsHeldOut);
    }

    [Fact]
    public void The_tuning_set_has_no_question_in_common_with_the_held_out_set()
    {
        var tuning = AnswerEvalSets.Load(Path("tuning.json")).Cases.Select(c => AnswerEvalSets.Normalize(c.Question)).ToHashSet();
        var test = AnswerEvalSets.Load(Path("test.json"), allowHeldOut: true).Cases.Select(c => AnswerEvalSets.Normalize(c.Question)).ToArray();

        Assert.DoesNotContain(test, tuning.Contains);
        Assert.Equal(tuning.Count, AnswerEvalSets.Load(Path("tuning.json")).Cases.Count);
    }

    [Fact]
    public void The_held_out_set_is_a_template_until_the_owner_delivers_at_least_30_real_questions()
    {
        var set = AnswerEvalSets.Load(Path("test.json"), allowHeldOut: true);

        Assert.Equal("test", set.Set);
        if (set.Status == "template")
        {
            Assert.Empty(set.Cases);   // no invented company questions
            return;
        }
        Assert.Equal("owner", set.Status);
        Assert.True(set.Cases.Count >= 30);
        Assert.True(set.Cases.Count(c => !c.Answerable) >= 8);
        Assert.True(set.Cases.Count(c => c.Answerable && c.Expected.Count > 1) >= 3);
        Assert.DoesNotContain(set.Cases, c => c.Synthetic);
    }

    [Fact]
    public void The_tuning_set_covers_answers_abstentions_and_injections()
    {
        var cases = AnswerEvalSets.Load(Path("tuning.json")).Cases;

        Assert.True(cases.Count >= 30);
        Assert.Equal(cases.Count, cases.Select(c => AnswerEvalSets.Normalize(c.Question)).Distinct().Count());
        Assert.True(cases.Count(c => !c.Answerable) >= 6);
        Assert.True(cases.Count(c => c.Forbidden == "KANÁREK-42") >= 4);
        Assert.All(cases.Where(c => c.Answerable), c => Assert.NotEmpty(c.Expected));
        Assert.All(cases.Where(c => !c.Answerable), c => Assert.Empty(c.Expected));
    }

    [Fact]
    public void Every_tuning_case_builds_a_prompt_within_budget_and_parses_the_fake_answer()
    {
        foreach (var evalCase in AnswerEvalSets.Load(Path("tuning.json")).Cases)
        {
            var sources = Enumerable.Range(1, 6).Select(i => new AnswerPromptSource($"Stránka {i}", "Sekce", new string('x', 3000))).ToArray();
            var messages = AnswerPromptBuilder.Build(evalCase.Question, sources);

            Assert.True(messages.Sum(m => m.Text.Length) <= 24_000, evalCase.Question);

            // The fake provider answers from the <zdroj n=…> blocks, including an invented source number.
            var numbers = System.Text.RegularExpressions.Regex.Matches(messages[^1].Text, "<zdroj n=\"(\\d+)\"").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
            var json = System.Text.Json.JsonSerializer.Serialize(new AnswerResult(evalCase.Answerable, evalCase.Answerable ? [new AnswerSentence("Odpověď.", [numbers[0], 9])] : []), System.Text.Json.JsonSerializerOptions.Web);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<AnswerResult>(json, System.Text.Json.JsonSerializerOptions.Web);
            var validated = AnswerValidator.Validate(parsed, numbers.Length);

            Assert.Equal(evalCase.Answerable, validated.Answerable);
            Assert.Equal(evalCase.Answerable ? 1 : 0, validated.InvalidCitations);
        }
    }
}
