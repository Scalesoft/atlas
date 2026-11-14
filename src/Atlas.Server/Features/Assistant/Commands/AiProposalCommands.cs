using System.Text;
using NHibernate.Linq;
using Atlas.Server.Features.Ai.Access;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Persistence;
using Atlas.Server.Features.Mcp.Domain;
using Atlas.Server.Features.PageLinks;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Assistant.Commands;

// The steps of a proposal decision (spec §6.1), each one short transaction; the stored call runs between the claim and the finish,
// outside any transaction (AssistantProposals.DecideAsync). In a …Commands namespace like the answer steps: nothing prints content.

/// <summary>The owner's proposal as the card shows it now (a pending one past its expiry reads expired); 404 for anybody else.</summary>
public sealed record GetAiProposal(Guid Id);

/// <summary>Step 1 of a decision: the owner's pending proposal locked, the "AI s nástroji" right again when approving, then
/// <c>declined</c>, or <c>applying</c> with the chosen items (distinct, in range, at least one).</summary>
public sealed record ClaimAiProposal(Guid Id, bool Approve, IReadOnlyList<int> Items);

/// <summary><see cref="Declined"/> for a declined proposal; otherwise the claimed <see cref="Row"/> and the chosen indexes in order.</summary>
public sealed record AiProposalClaim(AiToolProposalRow? Row, IReadOnlyList<int> Selected, AiProposalView? Declined);

/// <summary>Step 2: the result of the call stored per item (<c>AssistantProposals.Results</c>): applied or failed.</summary>
public sealed record FinishAiProposal(Guid Id, bool Applied, IReadOnlyList<AiProposalResult> Results);

/// <summary>Titles and paths of the pages of a proposal, as the user may see them now, and the names of their spaces
/// (<see cref="AiProposalPages"/>; a request with an empty page id asks only for its space).</summary>
public sealed record PreviewAiProposalPages(IReadOnlyList<AiProposalPageRequest> Pages);

public sealed record AiProposalPageRequest(Guid PageId, Guid SpaceId);

public sealed record AiProposalPage(string Title, string Path)
{
    private bool PrintMembers(StringBuilder builder) => false;
}

public sealed record AiProposalPages(IReadOnlyDictionary<Guid, AiProposalPage> Items, IReadOnlyDictionary<Guid, string> Spaces)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Items = ").Append(Items.Count);
        return true;
    }
}

public sealed class AiProposalHandler(ICurrentActor actor, AiProposalStore proposals, AiPolicyResolver policy, AiGate gate, TimeProvider clock)
{
    public const string Expired = "proposal_expired";
    public const string Superseded = "proposal_superseded";
    public const string Decided = "proposal_decided";

    public async Task<AiProposalView> Handle(GetAiProposal query, CancellationToken ct)
        => (await OwnedAsync(query.Id, false, ct)).View(clock.GetUtcNow().UtcDateTime);

    public async Task<AiProposalClaim> Handle(ClaimAiProposal command, CancellationToken ct)
    {
        var row = await OwnedAsync(command.Id, true, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (command.Approve)
        {
            // D17, spec §4.2: a right taken away stops an open proposal too; content rights are the tool's shared handlers' (D19).
            var permission = await policy.ResolveAsync(actor.Claimed().Id, ct);
            if (!permission.Enabled) throw AiGate.Unavailable();
            AiGate.RequireTools(permission, AiTaskCatalog.AssistantAgent);
            // The admin's switch of the batch move stops its open proposals as it stops the offer of the tool.
            if (row.Tool == "atlas_arrange_pages") await gate.RequireGlobalAsync(AiTaskCatalog.AssistantArrange, ct);
        }
        switch (row.EffectiveState(now))
        {
            case AiProposalState.Expired:
                throw new ApiException(StatusCodes.Status409Conflict, "Návrh vypršel. Zeptejte se asistenta znovu.", Expired);
            case AiProposalState.Superseded:
                throw new ApiException(StatusCodes.Status409Conflict, "Návrh nahradila novější otázka.", Superseded);
            case not AiProposalState.Pending:
                throw new ApiException(StatusCodes.Status409Conflict, "O návrhu už bylo rozhodnuto.", Decided);
        }
        if (!command.Approve)
        {
            await proposals.MoveAsync(row.Id, AiProposalState.Pending, AiProposalState.Declined, null, now, ct);
            return new AiProposalClaim(null, [], (row with { State = AiProposalState.Declined }).View(now));
        }
        var selected = command.Items.Distinct().Order().ToArray();
        if (selected.Length == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Vyberte aspoň jednu položku návrhu.");
        }
        if (selected.Length != command.Items.Count || selected[0] < 0 || selected[^1] >= row.Items.Count)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "Neplatná položka návrhu.");
        }
        await proposals.MoveAsync(row.Id, AiProposalState.Pending, AiProposalState.Applying, null, now, ct);
        return new AiProposalClaim(row, selected, null);
    }

    public async Task<AiProposalView> Handle(FinishAiProposal command, CancellationToken ct)
    {
        var row = await OwnedAsync(command.Id, true, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var results = command.Results;
        var state = command.Applied ? AiProposalState.Applied : AiProposalState.Failed;
        if (!await proposals.MoveAsync(row.Id, AiProposalState.Applying, state, results, now, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "O návrhu už bylo rozhodnuto.", Decided);
        }
        return (row with { State = state, Results = results }).View(now);
    }

    private async Task<AiToolProposalRow> OwnedAsync(Guid id, bool forUpdate, CancellationToken ct)
        => await proposals.OwnedAsync(id, actor.Claimed().Id, forUpdate, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "Návrh nebyl nalezen.");
}

/// <summary>
/// The title (as the user would open it) and the path "Prostor › … › Stránka" of pages the caller has already read through a shared
/// handler (<c>atlas_get_labels</c>); an ancestor the user may not list is "…" (<see cref="McpPagePaths"/>).
/// </summary>
public sealed class PreviewAiProposalPagesHandler(IContentAccess access, PageLinkStore links, McpPagePaths paths, ISession session)
{
    public async Task<AiProposalPages> Handle(PreviewAiProposalPages query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(null, ct);
        var ids = query.Pages.Select(page => page.PageId).Distinct().ToArray();
        var titles = (await links.ResolveAsync(actor, ids, ct)).ToDictionary(page => page.Id, page => page.Title);
        var spaceIds = query.Pages.Select(page => page.SpaceId).Distinct().ToList();
        var spaces = (await session.Query<Space>().Where(space => spaceIds.Contains(space.Id)).ToListAsync(ct)).ToDictionary(space => space.Id, space => space.Name);
        var described = await paths.DescribeAsync(actor, query.Pages.Where(page => titles.ContainsKey(page.PageId)).DistinctBy(page => page.PageId)
            .ToDictionary(page => page.PageId, page => (spaces.GetValueOrDefault(page.SpaceId) ?? "", titles[page.PageId])), ct);
        return new AiProposalPages(described.ToDictionary(pair => pair.Key, pair => new AiProposalPage(titles[pair.Key], pair.Value)), spaces);
    }
}
