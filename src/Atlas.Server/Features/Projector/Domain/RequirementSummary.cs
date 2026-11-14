using Atlas.Server.Features.Projector.Contracts;

namespace Atlas.Server.Features.Projector.Domain;

/// <summary>What the summary block shows of one page's blocks (spec §1.2); the web's summary computes the same (R2).</summary>
public static class RequirementSummary
{
    public static RequirementSummaryDto Of(IReadOnlyList<Requirement> blocks)
    {
        var states = RequirementAttributes.States.ToDictionary(state => state, state => blocks.Count(block => block.State == state), StringComparer.Ordinal);
        var done = states["done"];
        var applicable = blocks.Count - states["not_applicable"];
        var percent = Percent(done, applicable);
        var inboundMissing = blocks.Where(block => block.Direction == "inbound" && !RequirementAttributes.IsFinished(block.State)).ToArray();
        return new RequirementSummaryDto(blocks.Count, done, percent, states, states["waiting_for_input"], inboundMissing) { Applicable = applicable };
    }

    /// <summary>Done out of what applies, rounded down so that 100 % means everything is done; 0 when nothing applies.</summary>
    public static int Percent(int done, int applicable) => applicable == 0 ? 0 : done * 100 / applicable;

    /// <summary>The blocks with <see cref="Requirement.WaitingOn"/>: for one not finished, the keys it requires whose blocks on the page
    /// are not finished either, and the prerequisites of other pages (<paramref name="remote"/>, <c>key@pageId</c>) that are not finished
    /// or that the reader may not read (unknown counts as not finished). A key with no block on the page waits on nothing here.</summary>
    public static IReadOnlyList<Requirement> WithWaitingOn(IReadOnlyList<Requirement> blocks,
        IReadOnlyDictionary<string, RequirementPrerequisite>? remote = null)
    {
        var open = blocks.Where(block => !RequirementAttributes.IsFinished(block.State)).Select(block => block.Key).ToHashSet(StringComparer.Ordinal);
        bool Waits(string key) => remote?.GetValueOrDefault(key) is { } other ? !other.Readable || !RequirementAttributes.IsFinished(other.State!) : open.Contains(key);
        return blocks.Select(block => RequirementAttributes.IsFinished(block.State)
            ? block
            : block with { WaitingOn = block.Requires.Where(key => key != block.Key && Waits(key)).ToArray() }).ToArray();
    }
}

/// <summary>The matrix of the space overview (spec §4) from the rows the filter kept (<see cref="Persistence.RequirementIndex.Filter"/>):
/// sorted keys, each page's cells in key order, pages in the order of the rows.</summary>
public static class RequirementOverviewBuilder
{
    public static RequirementOverview Build(IReadOnlyList<RequirementRow> rows)
    {
        var keys = rows.Select(row => row.Requirement.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var pages = rows.GroupBy(row => row.PageId)
            .Select(page => new OverviewPage(page.Key, page.First().PageTitle,
                page.Select(row => row.Requirement).OrderBy(block => block.Key, StringComparer.Ordinal).ThenBy(block => block.Position).ToArray())
            {
                Percent = RequirementSummary.Percent(page.First().PageDone, page.First().PageApplicable), Applicable = page.First().PageApplicable
            })
            .ToArray();
        return new RequirementOverview(keys, pages);
    }
}

/// <summary>"Na co čekáme" (spec §4): the waiting_for_input blocks (the rows of the filter <c>waiting</c>) grouped by party (by contact
/// when the party is one), oldest date first and undated last, so the party waited on the longest leads.</summary>
public static class RequirementWaitingBuilder
{
    public static WaitingList Build(IReadOnlyList<RequirementRow> rows)
    {
        var waiting = rows.OrderBy(row => row.Requirement.Date.Length == 0).ThenBy(row => row.Requirement.Date, StringComparer.Ordinal);
        // A contact is one party whatever text its blocks show (plan 3.1 §2.3 point 1); the text of its longest waiting block names it.
        return new WaitingList(waiting.GroupBy(row => Guid.TryParse(row.Requirement.PartyContactId, out var contact) ? "contact:" + contact : "text:" + row.Requirement.Party.Trim(),
                StringComparer.Ordinal)
            .Select(party => new WaitingParty(party.First().Requirement.Party.Trim(), party.Select(row => new WaitingItem(row.PageId, row.PageTitle, row.Requirement.BlockId,
                row.Requirement.Key, row.Requirement.Title, row.Requirement.Direction, row.Requirement.State, row.Requirement.Date)).ToArray(),
                Guid.TryParse(party.First().Requirement.PartyContactId, out var id) ? id : null))
            .ToArray());
    }
}
