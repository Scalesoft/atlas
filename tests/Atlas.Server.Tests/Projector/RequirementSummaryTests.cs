using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>What the summary block, the overview and "Na co čekáme" compute from the blocks (spec §1.2, §4).</summary>
public sealed class RequirementSummaryTests
{
    [Fact]
    public void The_summary_counts_states_and_done_out_of_the_applicable_blocks_rounded_down()
    {
        Requirement[] blocks =
        [
            Block("a", "done"), Block("b", "done"), Block("c", "planned"), Block("d", "waiting_for_input", "inbound", party: "IT KBN"),
            Block("e", "not_applicable", "inbound"), Block("f", "in_progress", "inbound"), Block("g", "done", "inbound")
        ];

        var summary = RequirementSummary.Of(blocks);

        Assert.Equal((7, 6, 3, 50, 1), (summary.Total, summary.Applicable, summary.Done, summary.Percent, summary.Waiting));
        Assert.Equal(1, summary.States["not_applicable"]);
        Assert.Equal(0, summary.States["blocked"]);
        Assert.Equal(["d", "f"], summary.InboundMissing.Select(x => x.Key));
        Assert.Equal(0, RequirementSummary.Of([Block("x", "not_applicable")]).Percent);
        Assert.Equal(99, RequirementSummary.Of([.. Enumerable.Range(0, 199).Select(i => Block("d" + i, "done")), Block("open", "planned")]).Percent);
    }

    /// <summary>The summary's readable line (spec §2.3, review V1, V2) groups the unfinished blocks by readiness in the words of the web's
    /// requirementReadiness: by direction our work "lze nasadit", a hand-over "lze předat", an input "čeká na dodání"; "čeká na vstup";
    /// "čeká na N prerekvizit" (its requires); "blokováno", also behind a blocked prerequisite however deep. Five titles, then how many more.
    /// The web writes the same (requirementSummary.ts, tests/fixtures/content/requirements.json).</summary>
    [Fact]
    public void The_summary_line_says_what_is_ready_in_the_words_of_its_direction()
    {
        var document = ProjectorDocuments.Rich([ProjectorDocuments.Summary("s"),
            .. "ABCDEF".Select(name => ProjectorDocuments.Block("b" + name, name.ToString().ToLowerInvariant(), name.ToString())),
            ProjectorDocuments.Block("bG", "g", "G", direction: "outbound"), ProjectorDocuments.Block("bH", "h", "H", direction: "inbound"),
            ProjectorDocuments.Block("bI", "i", "I", requires: "h"), ProjectorDocuments.Block("bJ", "j", "J", state: "blocked", requires: "h"),
            ProjectorDocuments.Block("bK", "k", "K", state: "done"), ProjectorDocuments.Block("bL", "l", "L", state: "not_applicable", requires: "h"),
            ProjectorDocuments.Block("bM", "m", "M", requires: "i,k"), ProjectorDocuments.Block("bN", "n", "N", requires: "m"),
            ProjectorDocuments.Block("bO", "o", "O", state: "waiting_for_input"), ProjectorDocuments.Block("bP", "p", "P", requires: "j", content: [
                ProjectorDocuments.Block("bQ", "q", "Q", state: "done")])]);

        var summary = Atlas.Server.Platform.Content.EditorMarkdownWriter.Write(document["document"]!.AsObject()).Split("\n\n")[0];

        Assert.Equal("**Stav nasazení:** 12 % hotovo (2 z 16) · čeká na: O · nedodané vstupy: H · lze nasadit: A, B, C, D, E +1 · lze předat: G"
            + " · čeká na dodání: H · čeká na vstup: O · čeká na 1 prerekvizitu: I, M, N · blokováno: J, P", summary);
    }

    [Fact]
    public void A_block_that_is_not_done_waits_on_what_it_requires_that_is_not_done_on_the_same_page()
    {
        Requirement[] blocks =
        [
            Block("tested", "planned", requires: ["orders", "ssl", "elsewhere"]), Block("orders", "in_progress", requires: ["ssl"]),
            Block("ssl", "done"), Block("finished", "done", requires: ["orders"])
        ];

        var waiting = RequirementSummary.WithWaitingOn(blocks);

        Assert.Equal(["orders"], waiting[0].WaitingOn);
        Assert.Empty(waiting[1].WaitingOn);
        Assert.Empty(waiting[3].WaitingOn);
    }

    [Fact]
    public void The_overview_is_a_matrix_of_the_pages_and_keys_of_its_rows_with_each_pages_percent()
    {
        var kbn = Guid.NewGuid();
        var kostelec = Guid.NewGuid();
        RequirementRow[] rows =
        [
            new(kbn, "KBN", Block("ssl", "done", "inbound")) { PageDone = 1, PageApplicable = 3 },
            new(kbn, "KBN", Block("sms", "waiting_for_input", "inbound")) { PageDone = 1, PageApplicable = 3 },
            new(kostelec, "Kostelec", Block("ssl", "planned", "inbound")) { PageDone = 0, PageApplicable = 0 }
        ];

        var overview = RequirementOverviewBuilder.Build(rows);

        Assert.Equal(["sms", "ssl"], overview.Keys);
        Assert.Equal([("KBN", "sms"), ("KBN", "ssl"), ("Kostelec", "ssl")], overview.Pages.SelectMany(p => p.Cells.Select(c => (p.Title, c.Key))));
        Assert.Equal([(33, 3), (0, 0)], overview.Pages.Select(p => (p.Percent, p.Applicable)));
    }

    [Fact]
    public void Waiting_is_grouped_by_party_longest_waiting_first_and_undated_last()
    {
        var page = Guid.NewGuid();
        RequirementRow[] rows =
        [
            new(page, "KBN", Block("a", "waiting_for_input", party: "IT KBN", date: "2026-10-01")),
            new(page, "KBN", Block("b", "waiting_for_input", party: "Dodavatel", date: "2026-09-01")),
            new(page, "KBN", Block("c", "waiting_for_input", party: "IT KBN", date: "2026-08-15")),
            new(page, "KBN", Block("d", "waiting_for_input", party: "IT KBN")),
            new(page, "KBN", Block("e", "waiting_for_input"))
        ];

        var waiting = RequirementWaitingBuilder.Build(rows);

        Assert.Equal([("IT KBN", "c a d"), ("Dodavatel", "b"), ("", "e")],
            waiting.Parties.Select(x => (x.Party, string.Join(" ", x.Items.Select(i => i.Key)))));
        Assert.Equal(("KBN", page), (waiting.Parties[0].Items[0].PageTitle, waiting.Parties[0].Items[0].PageId));
    }

    private static Requirement Block(string key, string state, string direction = "internal", string party = "", string date = "",
        IReadOnlyList<string>? requires = null)
    {
        return new Requirement(key + "-id", key, key.ToUpperInvariant(), direction, state, date, party, "", requires ?? [], null, 0);
    }
}
