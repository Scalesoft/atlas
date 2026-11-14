using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Features.Projector.Domain;
using Xunit;

namespace Atlas.Server.Tests.Projector;

/// <summary>Catalog insertion with dependencies (Projector v1 spec §1.4, §4): dependencies first, cycles and unknown keys named.</summary>
public sealed class RequirementResolverTests
{
    private static readonly CatalogDefinition[] Catalog =
    [
        Definition("portal.orders.tested", "Otestováno", "portal.orders"),
        Definition("portal.orders", "Objednávky", "portal.requirements", "portal.ssl"),
        Definition("portal.requirements", "Strukturované požadavky"),
        Definition("portal.ssl", "SSL certifikát")
    ];

    [Fact]
    public void Dependencies_come_before_what_needs_them_with_the_reason()
    {
        var resolution = RequirementResolver.Resolve(Catalog, ["portal.orders.tested"], []);

        Assert.Equal(["portal.requirements", "portal.ssl", "portal.orders", "portal.orders.tested"], resolution.Items.Select(x => x.Definition.Key));
        Assert.Equal("Potřebuje ho: Objednávky", resolution.Items[0].Reason);
        Assert.Equal("portal.orders", resolution.Items[0].RequiredBy);
        Assert.Null(resolution.Items[3].RequiredBy);
        Assert.Empty(resolution.Warnings);
    }

    [Fact]
    public void Keys_already_on_the_page_are_skipped_with_their_dependencies_and_each_key_comes_once()
    {
        var resolution = RequirementResolver.Resolve(Catalog, ["portal.orders.tested", "portal.ssl", "portal.orders"], ["portal.orders"]);

        Assert.Equal(["portal.orders.tested", "portal.ssl"], resolution.Items.Select(x => x.Definition.Key));
        // Asked for by name, so the user learns why nothing came of it; a dependency already there is no news.
        Assert.Equal(["Požadavek „portal.orders“ už na stránce je; znovu se nevloží."], resolution.Warnings);
    }

    [Fact]
    public void An_unknown_key_is_a_warning_and_the_rest_is_still_resolved()
    {
        var resolution = RequirementResolver.Resolve([.. Catalog, Definition("sms", "SMS brána", "sms.contract")], ["missing", "sms"], []);

        Assert.Equal(["sms"], resolution.Items.Select(x => x.Definition.Key));
        Assert.Equal(["Požadavek „missing“ v katalogu není.", "Požadavek „sms.contract“ v katalogu není."], resolution.Warnings);
    }

    [Fact]
    public void A_cycle_is_refused_with_its_path()
    {
        var refused = Assert.Throws<ApiException>(() => RequirementResolver.Resolve(
            [Definition("a", "A", "b"), Definition("b", "B", "c"), Definition("c", "C", "a")], ["a"], []));

        Assert.Equal((400, "cyclic_dependency"), (refused.Status, refused.Code));
        Assert.Equal(new[] { "a", "b", "c", "a" }, refused.Extensions!["path"]);
    }

    [Fact]
    public void A_key_wanted_twice_comes_once()
    {
        var resolution = RequirementResolver.Resolve(Catalog, ["portal.ssl", "portal.ssl", " portal.ssl "], []);

        Assert.Equal(["portal.ssl"], resolution.Items.Select(x => x.Definition.Key));
    }

    [Fact]
    public void A_diamond_inserts_the_shared_dependency_once_before_both_paths()
    {
        var resolution = RequirementResolver.Resolve([Definition("a", "A", "b", "c"), Definition("b", "B", "d"), Definition("c", "C", "d"), Definition("d", "D")], ["a"], []);

        Assert.Equal(["d", "b", "c", "a"], resolution.Items.Select(x => x.Definition.Key));
    }

    [Fact]
    public void A_dependency_on_the_page_marked_not_applicable_is_not_inserted_again_and_is_reported()
    {
        var resolution = RequirementResolver.Resolve(Catalog, ["portal.orders"], ["portal.ssl"], notApplicable: ["portal.ssl"]);

        Assert.Equal(["portal.requirements", "portal.orders"], resolution.Items.Select(x => x.Definition.Key));
        Assert.Equal(["Požadavek „portal.ssl“ už na stránce je, ale je označený jako nerelevantní; „Objednávky“ na něm závisí."], resolution.Warnings);
    }

    [Fact]
    public void A_self_dependency_is_a_cycle_of_one()
    {
        var refused = Assert.Throws<ApiException>(() => RequirementResolver.Resolve([Definition("a", "A", "a")], ["a"], []));

        Assert.Equal("cyclic_dependency", refused.Code);
        Assert.Equal(new[] { "a", "a" }, refused.Extensions!["path"]);
        Assert.Contains("a → a", refused.Message);
    }

    [Fact]
    public void The_same_key_in_two_catalog_pages_takes_the_first_in_catalog_order_with_a_warning()
    {
        var first = Definition("ssl", "SSL z prvního katalogu");
        var second = Definition("ssl", "SSL z druhého katalogu");

        var resolution = RequirementResolver.Resolve([first, second], ["ssl"], []);
        var (unique, warnings) = RequirementResolver.Distinct([first, second]);

        Assert.Same(first, Assert.Single(resolution.Items).Definition);
        Assert.Equal(["Klíč „ssl“ je v katalogu víckrát; platí první výskyt („SSL z prvního katalogu“)."], resolution.Warnings);
        Assert.Same(first, Assert.Single(unique));
        Assert.Equal(resolution.Warnings, warnings);
    }

    [Fact]
    public void A_requires_chain_deeper_than_the_cap_is_refused_without_overflowing_the_stack()
    {
        // A catalog page holds about 16 000 blocks; a chain of them must not take the process down (A1).
        var chain = Enumerable.Range(0, 50_000).Select(i => Definition($"k{i}", $"K{i}", i + 1 < 50_000 ? [$"k{i + 1}"] : [])).ToArray();

        var refused = Assert.Throws<ApiException>(() => RequirementResolver.Resolve(chain, ["k0"], []));

        Assert.Equal((400, "dependency_too_deep"), (refused.Status, refused.Code));
    }

    [Fact]
    public void A_chain_within_the_cap_comes_dependencies_first()
    {
        var chain = Enumerable.Range(0, 30).Select(i => Definition($"k{i}", $"K{i}", i + 1 < 30 ? [$"k{i + 1}"] : [])).ToArray();

        var resolution = RequirementResolver.Resolve(chain, ["k0"], []);

        Assert.Equal(Enumerable.Range(0, 30).Reverse().Select(i => $"k{i}"), resolution.Items.Select(x => x.Definition.Key));
    }

    [Fact]
    public void An_insertion_of_more_blocks_than_the_cap_is_refused()
    {
        var leaves = Enumerable.Range(0, 2_000).Select(i => Definition($"leaf{i}", $"Leaf {i}")).ToArray();

        var refused = Assert.Throws<ApiException>(() => RequirementResolver.Resolve([Definition("all", "Vše", leaves.Select(x => x.Key).ToArray()), .. leaves], ["all"], []));

        Assert.Equal((400, "dependency_too_deep"), (refused.Status, refused.Code));
    }

    private static CatalogDefinition Definition(string key, string title, params string[] requires)
    {
        return new CatalogDefinition(key, title, title, "internal", requires, new JsonObject { ["type"] = "requirement" }, Guid.Empty, "", "");
    }
}
