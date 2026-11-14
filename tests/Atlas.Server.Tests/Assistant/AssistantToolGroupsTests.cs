using Atlas.Server.Features.Assistant.Agent;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>WP-13: the tool-group router is a pure function of the question, the two earlier questions and the panel context (D19: no API level).</summary>
public sealed class AssistantToolGroupsTests
{
    private static AssistantToolSelection Pick(string question, params string[] earlier) =>
        AssistantToolGroups.Select(question, earlier, null);

    [Fact]
    public void Pages_without_a_tag_get_the_basics_and_the_structure()
    {
        var pick = Pick("které stránky nemají tag");

        Assert.Equal(["zaklad", "struktura"], pick.Groups);
        Assert.Contains("atlas_find_pages_by_tag", pick.Tools);
        Assert.DoesNotContain("atlas_apply_labels", pick.Tools);
    }

    [Fact]
    public void Adding_a_tag_to_pages_offers_labels_and_mentions_without_any_level()
    {
        var pick = Pick("přidej tag VPN stránkám o VPN");

        Assert.Equal(["zaklad", "stitky", "struktura", "vyskyty"], pick.Groups);
        Assert.Contains("atlas_apply_labels", pick.Tools);
        Assert.Contains("atlas_find_mentions", pick.Tools);
        Assert.DoesNotContain("atlas_publish", pick.Tools);
    }

    /// <summary>Review K2: with the Projector module on, every turn reads requirement pages through its read tools; off, never.</summary>
    [Fact]
    public void Requirement_reads_join_every_turn_only_with_the_projector_module_on()
    {
        var on = AssistantToolGroups.Select("Na co čekáme v KBN?", null, new AssistantToolContext(Projector: true));

        Assert.Equal(["zaklad", "pozadavky"], on.Groups);
        Assert.Equal(["atlas_search", "atlas_get_page", "atlas_list_spaces", "projector_get_requirements", "projector_waiting", "projector_overview",
            "projector_get_catalog", "projector_query", "projector_get_requirement_history"], on.Tools);
        Assert.DoesNotContain(on.Tools, tool => tool is "projector_update_requirement" or "projector_insert_requirements");
        Assert.Equal(["zaklad"], AssistantToolGroups.Select("Na co čekáme v KBN?", null, null).Groups);
        // The lowest priority: a draft with the tree keeps its tools, the requirement reads give way.
        Assert.DoesNotContain("pozadavky", AssistantToolGroups.Select("uprav sekci Přístup a přesuň stránku", null, new AssistantToolContext(Projector: true)).Groups);
    }

    // Plan 3.5 C: a new deployment as a copy of another with modules added, one proposal; the reads find the source and the module.
    [Fact]
    public void A_new_deployment_by_another_one_offers_the_copy_with_the_requirement_reads_only_with_the_projector_module_on()
    {
        const string question = "Založ mi projekt podle Dubovic a přidej tam navíc ještě telekonference";
        var on = AssistantToolGroups.Select(question, null, new AssistantToolContext(Projector: true));

        Assert.Equal(["zaklad", "nasazeni", "pozadavky"], on.Groups);
        Assert.Contains("projector_create_deployment", on.Tools);
        Assert.Contains("projector_get_catalog", on.Tools);
        Assert.DoesNotContain("nasazeni", AssistantToolGroups.Select(question, null, null).Groups);
        Assert.DoesNotContain("nasazeni", AssistantToolGroups.Select("Co chybí v projektu Dubovice?", null, new AssistantToolContext(Projector: true)).Groups);
        Assert.NotNull(AiProposalShape.For("projector_create_deployment"));
    }

    /// <summary>Plan 3.5 I: a question about requirements keeps their reads before the other read groups; the write groups still come first.</summary>
    [Fact]
    public void A_requirement_question_keeps_the_requirement_reads_ahead_of_other_read_groups()
    {
        var projector = new AssistantToolContext(Projector: true);
        // historie + struktura + zaklad = 11 tools; before, the 6 requirement reads did not fit and were dropped.
        Assert.Equal(["zaklad", "pozadavky"],
            AssistantToolGroups.Select("Kdo změnil stav požadavku NIA v prostoru Portál?", null, projector).Groups.Where(x => x is "zaklad" or "pozadavky"));
        Assert.Contains("projector_get_requirement_history", AssistantToolGroups.Select("Kdo změnil stav požadavku NIA v prostoru Portál?", null, projector).Tools);
        // Without a requirement word the order is as before: the tree and history win the budget.
        Assert.DoesNotContain("pozadavky", AssistantToolGroups.Select("Kdo změnil strukturu v prostoru Portál?", null, projector).Groups);
        // A write group keeps its place ahead of the requirement reads.
        Assert.Contains("koncepty", AssistantToolGroups.Select("uprav sekci Stav na stránce zakázky", null, projector).Groups);
    }

    [Fact]
    public void Publish_only_counts_in_the_last_question()
    {
        Assert.Contains("publikace", Pick("publikuj to", "napiš koncept o VPN").Groups);
        Assert.Contains("atlas_publish", Pick("Zveřejni to prosím").Tools);

        var earlier = Pick("a ještě něco k VPN", "publikuj to");
        Assert.DoesNotContain("publikace", earlier.Groups);
        Assert.DoesNotContain("atlas_publish", earlier.Tools);
    }

    [Theory]
    [InlineData("Jaké mají stránky ikonky?", "struktura")]
    [InlineData("zobraz strom prostoru", "struktura")]
    [InlineData("Přesuň tuto stránku pod Úvod", "presuny")]
    [InlineData("kdo má přístup k prostoru", "pristup")]
    [InlineData("kdo je autorem, jaká je historie", "historie")]
    [InlineData("Shrň mi ty stránky", "souhrny")]
    public void Inflected_forms_and_missing_diacritics_match(string question, string group) =>
        Assert.Contains(group, Pick(question).Groups);

    [Theory]
    [InlineData("Kdo naposledy upravil stránku Pravidla hesel?")]
    [InlineData("kdo vytvořil tu stránku o VPN")]
    [InlineData("Kdo to napsal a kdy to upravila Jana?")]
    public void Past_tense_edit_verbs_are_history_not_a_draft_request(string question)
    {
        var pick = Pick(question);

        Assert.Contains("historie", pick.Groups);
        Assert.DoesNotContain("koncepty", pick.Groups);
    }

    [Theory]
    [InlineData("uprav sekci Úvod")]
    [InlineData("Můžete upravit tu stránku?")]
    [InlineData("vytvoř koncept o VPN")]
    public void Imperative_and_infinitive_edit_verbs_are_a_draft_request(string question) =>
        Assert.Contains("koncepty", Pick(question).Groups);

    [Fact]
    public void A_label_noun_without_a_write_verb_is_not_a_write_group()
    {
        Assert.DoesNotContain("stitky", Pick("jaké tagy existují").Groups);
        Assert.Contains("stitky", Pick("změň ikonu stránky VPN").Groups);
    }

    [Fact]
    public void An_open_space_and_a_deictic_question_is_about_its_structure()
    {
        var pick = AssistantToolGroups.Select("co tady je", null, new AssistantToolContext(SpaceOpen: true));

        Assert.Contains("struktura", pick.Groups);
        Assert.DoesNotContain("struktura", Pick("co tady je").Groups);
    }

    [Fact]
    public void Never_more_than_fourteen_tools_and_lowest_priority_groups_drop_whole()
    {
        var pick = Pick("publikuj: napiš koncept, přesuň, přidej tag, kde všude se zmiňuje, strom, kdo má přístup, historie, shrň");

        Assert.True(pick.Tools.Count <= AssistantToolGroups.MaxTools);
        Assert.Equal(pick.Tools.Distinct().Count(), pick.Tools.Count);
        // The tree a label or move needs survives; the draft group (lower priority than labels) is what drops.
        Assert.Equal(["zaklad", "publikace", "stitky", "presuny", "struktura", "pristup"], pick.Groups);
    }

    [Theory]
    [InlineData("navrhni ikonky pro stránky v IT")]
    [InlineData("Navrhněte ikony a tagy pro Personální")]
    [InlineData("dej stránkám v Provozu vhodné ikonky")]
    [InlineData("přiřaď stránkám barvy")]
    [InlineData("oštítkuj stránky v Provozu")]
    [InlineData("přidej tag hr všem stránkám v Personální")]
    public void A_label_request_is_labels_with_the_tree_and_never_drafts(string question)
    {
        var pick = Pick(question);

        Assert.Equal(["zaklad", "stitky", "struktura"], pick.Groups);
        Assert.Contains("atlas_apply_labels", pick.Tools);
        Assert.Contains("atlas_get_space_tree", pick.Tools);
        Assert.DoesNotContain("atlas_create_draft", pick.Tools);
    }

    [Fact]
    public void Tagging_pages_about_a_topic_adds_the_mentions()
    {
        Assert.Equal(["zaklad", "stitky", "struktura", "vyskyty"], Pick("otaguj stránky o VPN").Groups);
    }

    [Theory]
    [InlineData("navrhni text o VPN")]
    [InlineData("napiš nový odstavec o tagování")]
    public void Text_as_the_object_or_a_writing_verb_without_a_label_noun_is_drafts(string question) =>
        Assert.Contains("koncepty", Pick(question).Groups);

    [Theory]
    [InlineData("Přeformátuj tuto stránku podle stránky Pravidla hesel")]
    [InlineData("uprav sekci Délka hesla")]
    [InlineData("napiš stránku o čipových kartách")]
    [InlineData("Přejmenuj stránku Služební cesty")]
    public void A_draft_request_gets_the_reads_it_needs_within_the_cap(string question)
    {
        var pick = Pick(question);

        Assert.Equal(["zaklad", "koncepty", "struktura"], pick.Groups);
        Assert.Contains("atlas_get_page", pick.Tools);
        Assert.Contains("atlas_search", pick.Tools);
        Assert.Contains("atlas_get_space_tree", pick.Tools);
        Assert.Contains("atlas_replace_page", pick.Tools);
        Assert.True(pick.Tools.Count <= AssistantToolGroups.MaxTools);
    }

    // The wiki tasks of the palette and the panel (assistantEntries.ts wikiTasks) and how people say the same.
    [Theory]
    [InlineData("doplň ikonky do prostoru")]
    [InlineData("Doplň ikonky stránkám v prostoru IT, které ještě ikonu nemají. Vyber je podle obsahu stránek.")]
    [InlineData("Nastav této stránce vhodnou ikonu a tagy podle jejího obsahu.")]
    public void Filling_in_icons_is_labels(string question)
    {
        var pick = Pick(question);

        Assert.Equal(["zaklad", "stitky", "struktura"], pick.Groups);
        Assert.Contains("atlas_apply_labels", pick.Tools);
    }

    [Theory]
    [InlineData("projdi tuto stránku a zkonvertuj všechny věci do kontaktů")]
    [InlineData("Projdi tuto stránku a všechny kontaktní údaje (jména s telefony, e-maily a funkcemi) převeď na kontakty. Ostatní text nech beze změny.")]
    public void Converting_a_page_into_contacts_is_a_draft(string question)
    {
        var pick = Pick(question);

        Assert.Equal(["zaklad", "koncepty", "struktura"], pick.Groups);
        Assert.Contains("atlas_replace_page", pick.Tools);
        Assert.Contains("atlas_update_section", pick.Tools);
    }

    [Theory]
    [InlineData("poskládej v tomto prostoru pěkné levé menu")]
    [InlineData("navrhni lepší pořadí stránek v menu")]
    [InlineData("seřaď stránky v prostoru IT")]
    [InlineData("Poskládej v prostoru IT srozumitelné levé menu: přečti strom prostoru, popiš, jak bys stránky seřadil a zanořil (související pohromadě, nejdůležitější nahoře), a celé mi to dej k potvrzení najednou.")]
    public void Arranging_the_left_menu_is_the_batch_move_in_the_tree_without_drafts(string question)
    {
        var pick = Pick(question);

        Assert.Equal(["zaklad", "menu", "struktura"], pick.Groups);
        Assert.Equal(["atlas_search", "atlas_get_page", "atlas_list_spaces", "atlas_arrange_pages"], pick.Tools.Take(4));
        Assert.DoesNotContain("atlas_create_draft", pick.Tools);

        // Switched off by the admin (assistant.arrange-pages): no batch move, so the agent describes the menu instead of proposing it.
        var off = AssistantToolGroups.Select(question, null, null, new HashSet<string> { AssistantToolGroups.MenuGroup });
        Assert.DoesNotContain("menu", off.Groups);
        Assert.DoesNotContain("atlas_arrange_pages", off.Tools);
    }

    [Fact]
    public void A_single_move_does_not_carry_the_batch_move()
    {
        var pick = Pick("Přesuň tuto stránku pod Úvod");

        Assert.Equal(["zaklad", "presuny", "struktura"], pick.Groups);
        Assert.DoesNotContain("atlas_arrange_pages", pick.Tools);
    }

    [Fact]
    public void A_contact_question_is_not_a_draft() =>
        Assert.DoesNotContain("koncepty", Pick("jaký je kontakt na správce VPN").Groups);

    [Fact]
    public void The_tree_survives_with_labels_when_many_groups_compete()
    {
        var pick = Pick("navrhni ikonky a tagy, kdo má přístup, historie, kde všude se zmiňuje, shrň");

        Assert.Contains("struktura", pick.Groups);
        Assert.Contains("stitky", pick.Groups);
        Assert.True(pick.Tools.Count <= AssistantToolGroups.MaxTools);
    }
}
