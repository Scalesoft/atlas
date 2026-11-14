using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiSuggestions.Contracts;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.AiSuggestions;

/// <summary>P2 (design §7.8): the parent shared by most of the pages nearest to the new title, only where the actor may create
/// and only along a path the actor may read.</summary>
public sealed class PlacementSuggestionTests
{
    private static string Meaning(string text)
        => text.Contains("Síťový kanál", StringComparison.Ordinal) ? FakeAi.Near("koncept vpn kanal")
            : text.Contains("Tajný kanál", StringComparison.Ordinal) || text.Contains("kanál pro VPN", StringComparison.Ordinal) ? "koncept vpn kanal"
            : text;

    [PostgreSqlFact]
    public async Task Suggests_the_most_common_parent_of_similar_pages_only_where_the_actor_may_create()
    {
        await using var f = await SuggestionFixture.CreateAsync(Meaning);
        var network = await f.PublishAsync("Síť");
        var secret = await f.PublishAsync("Tajné", author: f.Admin);
        // Three pages under Tajné mean exactly what the title means, two under Síť nearly: Tajné has the most votes.
        var hiddenChild = await f.PublishAsync("Tajný kanál 1", parentId: secret.Id);
        await f.PublishAsync("Tajný kanál 2", parentId: secret.Id);
        await f.PublishAsync("Tajný kanál 3", parentId: secret.Id);
        await f.PublishAsync("Síťový kanál 1", parentId: network.Id);
        await f.PublishAsync("Síťový kanál 2", parentId: network.Id);
        await f.PublishAsync("Počasí", "pocasi bourka vitr dest mraky");
        await f.ActivateAsync();
        const string title = "Nový kanál pro VPN";

        var admin = await f.AsAsync(f.Admin, flow => flow.PlacementAsync(title, f.SpaceId, default));
        Assert.Equal(secret.Id, admin!.ParentId);
        Assert.Equal("Tajné", admin.ParentTitle);
        Assert.Equal([new PlacementPathItem(secret.Id, "Tajné")], admin.Path);

        // Tajné becomes its owner's own; its children stay readable, yet a parent the writer cannot read is never suggested.
        await f.RestrictAsync(secret.Id);
        Assert.Equal(1, await f.Index.CountAsync($"""
            SELECT count(*) FROM "EffectivePageAccess" WHERE "UserId"='{f.Writer.Id}' AND "PageId"='{hiddenChild.Id}' AND "Role">=1
            """));
        var writer = await f.AsAsync(f.Writer, flow => flow.PlacementAsync(title, f.SpaceId, default));
        Assert.Equal(network.Id, writer!.ParentId);
        Assert.Equal("Síť", writer.ParentTitle);
        Assert.Equal([new PlacementPathItem(network.Id, "Síť")], writer.Path);
        Assert.Single(f.Sent(AiTaskCatalog.PageCreateSuggest));

        // A reader may not create in the space: nothing, and the title never leaves Atlas.
        Assert.Null(await f.AsAsync(f.Reader, flow => flow.PlacementAsync(title + " doma", f.SpaceId, default)));
        Assert.Null(await f.AsAsync(f.Writer, flow => flow.PlacementAsync(title + " doma", f.RestrictedSpaceId, default)));
        Assert.Single(f.Sent(AiTaskCatalog.PageCreateSuggest));
        // Nothing near enough: nothing.
        Assert.Null(await f.AsAsync(f.Writer, flow => flow.PlacementAsync("Rozpočet oddělení", f.SpaceId, default)));
        // A space excluded from AI: nothing, and nothing sent.
        await f.ExcludeAsync(f.SpaceId);
        var sent = f.SentWorkingText.Count;
        Assert.Null(await f.AsAsync(f.Writer, flow => flow.PlacementAsync(title + " v kanceláři", f.SpaceId, default)));
        Assert.Equal(sent, f.SentWorkingText.Count);

        // Without the capability the endpoint is unavailable.
        await f.SetTaskAsync(AiTaskCatalog.PageCreateSuggest, false);
        var unavailable = await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Writer, flow => flow.PlacementAsync(title, f.OtherSpaceId, default)));
        Assert.Equal((503, AiGate.UnavailableCode), (unavailable.Status, unavailable.Code));
    }
}
