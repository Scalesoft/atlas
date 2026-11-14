using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>A deployment made from the catalog is written as requirement headings (catalogSelection.ts) whose dependency line links the
/// page itself through the id of all f: the server writes the page's real id, and no block of the old shape is ever stored.</summary>
public sealed class DeploymentFromCatalogTests
{
    private const string Own = "ffffffff-ffff-ffff-ffff-ffffffffffff";

    [PostgreSqlFact]
    public async Task A_new_deployment_links_its_requirement_headings_to_the_page_it_becomes()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_deployment_catalog_", modules: TestModules.AllEnabled);
        var actor = await host.SeedUserAsync("writer");
        host.Actor.Current = actor;
        var space = await host.SeedSpaceAsync("Nasazení", (actor, SpaceRole.Writer));
        var content = $$$"""
            {"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":3},{"id":"heading-anchors","version":2},{"id":"pages","version":1}],
            "document":{"type":"doc","content":[
              {"type":"heading","attrs":{"level":3,"headingId":"vpn","state":"planned","direction":"inbound"},"content":[{"type":"text","text":"VPN"}]},
              {"type":"heading","attrs":{"level":3,"headingId":"portal","state":"planned"},"content":[{"type":"text","text":"Portál"}]},
              {"type":"paragraph","attrs":{"dependencies":true},"content":[{"type":"pageReference","attrs":{"id":"{{{Own}}}","title":"VPN","url":"#/pages/{{{Own}}}?heading=vpn"}}]}]}}
            """.ReplaceLineEndings("");

        var page = await host.InvokeAsync<PageDto>(new CreatePage(new(space, null, "Nasazení", content, "")));

        Assert.DoesNotContain(Own, page.ContentJson, StringComparison.Ordinal);
        Assert.Contains($"#/pages/{page.Id:D}?heading=vpn", page.ContentJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"requirement\"", page.ContentJson, StringComparison.Ordinal);
    }
}
