using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Features.SpaceSettings.Domain;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.SpaceCovers;

public sealed class CoverCatalogTests
{
    /// <summary>The acceptance table of the design (2026-10-10-space-image-design.md, Verification): realistic Czech space
    /// names, descriptions and icons with the drawing they must get. The one deliberate table of this feature: a change of
    /// tooling/space-covers/matching.py is judged against it.</summary>
    public static TheoryData<string, string, string, string?> Picks => new()
    {
        // Czech inflection: one stem covers the forms of a word.
        { "Zálohování", "", "book-open", "it-backup" },
        { "Dovolené 2026", "", "calendar", "ops-vacation" },
        { "Smlouvy a právní dokumenty", "", "book-open", "ops-legal" },
        { "Nasazení aplikací", "", "book-open", "it-cicd" },
        // A tie (one name word each for it-backup and it-database) goes to catalog order.
        { "Zálohy databází", "", "book-open", "it-backup" },
        // A false friend: "založení" is not "záloh…".
        { "Založení firmy", "", "book-open", null },
        // Short terms match whole words only: HR is not "hry".
        { "HR", "", "book-open", "ops-hr" },
        { "Hry a turnaje", "", "book-open", null },
        // "report" alone would be it-analytics (earlier in the catalog); "sql" and the icon carry it-database.
        { "SQL reporty", "", "database", "it-database" },
        // The default icon implies nothing; no hit is no drawing.
        { "Tým Kobra", "", "book-open", null },
        // The icon alone, the description alone.
        { "Tým Kobra", "", "database", "it-database" },
        { "Tým Kobra", "Evidence faktur a plateb dodavatelům.", "book-open", "ops-invoicing" },
        // The order of the three signals: name over icon, icon over description, name over both.
        { "Marketing", "", "database", "ops-marketing" },
        { "Tým Kobra", "Zápisy z porad.", "database", "it-database" },
        { "Porady", "Zálohy a obnova databází", "database", "ops-meetings" },
        // Whole spaces as people write them.
        { "Lidé a onboarding", "Nástup, směrnice, týmy a nástroje.", "users", "ops-hr" },
        { "Runbooky a provoz", "Postupy pro nasazení, obnovu a řešení incidentů jednotlivých systémů.", "server", "it-monitoring" },
        { "Obchod a klienti", "Obchodní proces, klienti, smlouvy a SLA.", "building-2", "ops-sales" },
        // A prefix that starts another topic's word: "reklamace" is a complaint, not an advertisement.
        { "Reklamace", "", "book-open", "ops-support" },
        // Czech forms the stems must reach: the genitive plural "plateb" (a name hit, and a description hit that no tie decides).
        { "Seznam plateb", "", "book-open", "ops-invoicing" },
        { "Tým Kobra", "Evidence faktur a plateb.", "book-open", "ops-invoicing" },
        // A tie goes to the earlier entry, whatever the topic: a generic word must not sit in the entry that would win it.
        { "Interní směrnice", "", "book-open", "ops-policies" },
        { "Datové schránky", "", "book-open", "ops-communication" },
        { "Podpora uživatelů", "", "book-open", "ops-support" },
        // The phrase beats its generic half: "bezpečnost" alone is IT, "práce" alone is HR, together they are health and safety.
        { "Bezpečnost práce", "", "book-open", "ops-safety" },
        { "Dohody o provedení práce", "", "book-open", "ops-hr" },
        // The specific topic beats the generic employee, and a deposit invoice is not a backup, nor a social network a network.
        { "Nábor zaměstnanců", "", "book-open", "ops-recruitment" },
        { "Zálohové faktury", "", "book-open", "ops-invoicing" },
        { "Sociální sítě", "", "book-open", "ops-marketing" },
    };

    [Theory]
    [MemberData(nameof(Picks))]
    public void Match_picks_the_drawing_of_a_realistic_space(string name, string description, string icon, string? expected)
    {
        Assert.Equal(expected, CoverCatalog.Match(name, description, icon));
    }

    /// <summary>Authoring errors surface per drawing: a missing stem, an earlier entry that shadows a later one's own name, an
    /// icon no space can have (so a renamed icon is noticed here, not as a silently lost hint).</summary>
    [Fact]
    public void Every_drawing_is_the_pick_for_its_own_name_and_names_only_icons_a_space_can_have()
    {
        Assert.NotEmpty(CoverCatalog.Entries);
        Assert.All(CoverCatalog.Entries, entry =>
        {
            Assert.True(CoverCatalog.Knows(entry.Id));
            Assert.Equal(entry.Id, CoverCatalog.Match(entry.Name, null, SpaceAppearance.DefaultIcon));
            Assert.DoesNotContain(SpaceAppearance.DefaultIcon, entry.Icons);
            Assert.All(entry.Icons, icon => Assert.Contains(icon, AppearanceCatalog.Icons));
        });
        Assert.False(CoverCatalog.Knows("no-such-drawing"));
        Assert.False(CoverCatalog.Knows(null));
    }

    /// <summary>An answer does not depend on what the memo has seen before: after more distinct inputs than <see
    /// cref="CoverCatalog.MemoLimit"/>, a known name gets its pick, and gets it again from the memo. What the memo holds is not
    /// observable, so this proves the answers only.</summary>
    [Fact]
    public void A_name_gets_the_same_pick_again_after_more_distinct_inputs_than_the_memo_limit()
    {
        for (var i = 0; i <= CoverCatalog.MemoLimit; i++)
        {
            Assert.Null(CoverCatalog.Match($"Prostor číslo {i}", null, "book-open"));
        }
        Assert.Equal("it-backup", CoverCatalog.Match("Zálohy", null, "book-open"));
        Assert.Equal("it-backup", CoverCatalog.Match("Zálohy", null, "book-open"));
    }
}
