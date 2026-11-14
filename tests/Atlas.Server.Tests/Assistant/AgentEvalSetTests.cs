using Atlas.AiEval;
using Atlas.Server.Features.Assistant.Agent;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// WP-16: structure of the agent evaluation set (tests/ai-eval/agent). It never calls a model. Because the expected router groups are
/// checked for every case, the set doubles as a regression suite for <see cref="AssistantToolGroups"/>.
/// </summary>
public sealed class AgentEvalSetTests
{
    private static readonly string[] Phases = ["F2", "F3", "F4", "F5"];
    private static readonly string[] Kinds = ["answer", "refuse", "propose"];

    private static string Path(string file) => System.IO.Path.Combine(AppContext.BaseDirectory, "ai-eval", "agent", file);

    private static AgentEvalSet Tuning => AgentEvalSets.Load(Path("tuning.json"));

    [Fact]
    public void The_held_out_set_is_refused_unless_requested_and_shares_no_question_with_tuning()
    {
        Assert.Throws<HeldOutSetException>(() => AgentEvalSets.Load(Path("test.json")));

        var test = AgentEvalSets.Load(Path("test.json"), allowHeldOut: true);
        var tuning = Tuning.Cases.Select(c => AnswerEvalSets.Normalize(c.Question)).ToHashSet();

        Assert.True(test.IsHeldOut);
        Assert.False(Tuning.IsHeldOut);
        Assert.DoesNotContain(test.Cases, c => tuning.Contains(AnswerEvalSets.Normalize(c.Question)));
        if (test.Status == "template")
        {
            Assert.Empty(test.Cases);   // no invented company tasks
            return;
        }
        Assert.Equal("owner", test.Status);
        Assert.DoesNotContain(test.Cases, c => c.Synthetic);
    }

    [Fact]
    public void The_tuning_set_covers_every_category_and_the_known_prototype_failure()
    {
        var cases = Tuning.Cases;
        var byCategory = cases.GroupBy(c => c.Category).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct().Count());
        Assert.Equal(cases.Count, cases.Select(c => AnswerEvalSets.Normalize(c.Question + string.Join('|', c.RecentQuestions ?? []) + c.AsUser)).Distinct().Count());
        foreach (var category in new[] { "search", "tree", "tags", "mentions", "access", "history", "summary", "labels", "drafts", "moves", "publish", "injection" })
        {
            Assert.True(byCategory.ContainsKey(category), category);
        }
        Assert.True(byCategory["injection"] >= 3);
        Assert.All(cases.Where(c => c.Category == "injection"), c => Assert.False(string.IsNullOrEmpty(c.Outcome.Forbidden), c.Id));
        Assert.All(cases, c => Assert.True(c.Phase is null || Phases.Contains(c.Phase), c.Id));
        Assert.All(cases, c => Assert.Contains(c.Outcome.Kind, Kinds));
        // Only labels (WP-21) and "no proposal" injections are runnable; other proposals wait for their phase.
        Assert.All(cases.Where(c => c.Outcome.Kind == "propose"),
            c => Assert.True(c.Phase is not null || c.Outcome.ProposalTool == "atlas_apply_labels" || c.Outcome.MaxItems == 0, c.Id));
        Assert.All(cases.Where(c => c.Outcome.ProposalTool is not null), c => Assert.Contains(c.Outcome.ProposalTool!, c.Tools.Required));
        Assert.All(cases.Where(c => c.Outcome.MinItems > c.Outcome.MaxItems), c => Assert.Fail(c.Id));
        Assert.True(cases.Count(c => c.Category == "labels" && c.Phase is null) >= 6);
        Assert.Contains(cases, c => c.Category == "injection" && c.Outcome is { Kind: "propose", MaxItems: 0 });

        var known = cases.Single(c => c.Question == "Které stránky nemají žádné tagy?");
        Assert.Contains("atlas_get_space_tree", known.Tools.Required);
        Assert.Equal(3, known.Tools.MinCalls!["atlas_get_space_tree"]);
    }

    [Fact]
    public void Every_case_agrees_with_the_router_and_never_forbids_what_it_requires_or_offers()
    {
        foreach (var c in Tuning.Cases)
        {
            var pick = AssistantToolGroups.Select(c.Question, c.RecentQuestions, new AssistantToolContext(c.Context?.Page is not null, c.Context?.Space is not null));

            Assert.True(c.Groups.SequenceEqual(pick.Groups), $"{c.Id}: expected [{string.Join(',', c.Groups)}], router gave [{string.Join(',', pick.Groups)}]");
            Assert.All(c.Tools.Required, entry => Assert.True(entry.Split('|').Any(pick.Tools.Contains), $"{c.Id}: {entry} is not offered by the router"));
            Assert.All(c.Tools.Forbidden, tool => Assert.DoesNotContain(tool, c.Tools.Required.SelectMany(r => r.Split('|'))));
            Assert.All(c.Tools.Forbidden, tool => Assert.True(c.Outcome.Kind == "propose" || !pick.Tools.Contains(tool), $"{c.Id}: router offers forbidden {tool}"));
        }
    }

    /// <summary>D18. A request to write a draft and publish it at once expects the draft's card only: the page does not exist until the
    /// user confirms it, so its publication is the next turn's (WP-41).</summary>
    [Fact]
    public void Publishing_is_only_expected_when_the_last_question_asks_for_it()
    {
        foreach (var c in Tuning.Cases)
        {
            var offered = c.Groups.Contains("publikace");

            if (offered)
            {
                Assert.True(c.Tools.Required.Contains("atlas_publish") || c.Outcome.ProposalTool == "atlas_create_draft", c.Id);
            }
            else
            {
                Assert.DoesNotContain("atlas_publish", c.Tools.Required);
                Assert.Contains("atlas_publish", c.Tools.Forbidden);
            }
        }
        Assert.Contains(Tuning.Cases, c => c.Outcome.ProposalTool == "atlas_publish" && c.Outcome.MaxItems == 1);
        // The page of the publication case is a draft of the seed, and its text asks for a publication the injection case must not make.
        var draft = Tuning.Seed!.Spaces.SelectMany(s => s.Pages).Single(p => !p.Published);
        Assert.Contains("publikuj", draft.Markdown);
        Assert.All(Tuning.Cases.Where(c => c.Question.Contains(draft.Title)), c => Assert.Equal("KANÁREK-42", c.Outcome.Forbidden));
    }

    [Fact]
    public void The_seed_is_consistent_with_the_cases()
    {
        var seed = Tuning.Seed ?? throw new InvalidOperationException("tuning.json has no seed");
        var pages = seed.Spaces.SelectMany(s => s.Pages).ToArray();

        Assert.Equal(pages.Length, pages.Select(p => p.Slug).Distinct().Count());
        Assert.All(seed.Spaces, s => Assert.All(s.Pages.Where(p => p.Parent is not null), p => Assert.Contains(s.Pages, q => q.Slug == p.Parent)));
        Assert.Contains(seed.Spaces, s => s.Closed);
        Assert.Contains(seed.Spaces, s => s.ExcludedFromAi);
        Assert.True(pages.Count(p => p.Markdown.Contains("KANÁREK-42")) >= 2);
        Assert.All(Tuning.Cases, c => Assert.Contains(c.AsUser, seed.Users));
        Assert.All(Tuning.Cases.Where(c => c.Context?.Space is not null), c => Assert.Contains(seed.Spaces, s => s.Slug == c.Context!.Space));
        Assert.All(seed.Spaces.SelectMany(s => s.Members), m => Assert.Contains(m.User, seed.Users));

        // The "no tags" case expects exactly the untagged pages of the spaces the reader may see and the agent may read.
        var visible = seed.Spaces.Where(s => !s.Closed && !s.ExcludedFromAi).SelectMany(s => s.Pages).Where(p => p.Tags.Count == 0).Select(p => p.Title);
        var expected = Tuning.Cases.Single(c => c.Id == "tags-none-all-spaces").Outcome.Facts;
        Assert.Equal(visible.Order(), expected.Order());
        Assert.Equal(seed.Spaces.Count(s => !s.Closed && !s.ExcludedFromAi), Tuning.Cases.Single(c => c.Id == "tags-none-all-spaces").Tools.MinCalls!["atlas_get_space_tree"]);
    }
}
