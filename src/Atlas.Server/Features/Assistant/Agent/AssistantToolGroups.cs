using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>What the panel shows next to the question: an open page and/or space (spec §5.1); <paramref name="Projector"/>: the module is on.</summary>
public sealed record AssistantToolContext(bool PageOpen = false, bool SpaceOpen = false, bool Projector = false);

/// <summary>The groups chosen for one turn and the tool names they contain (distinct, in group order).</summary>
public sealed record AssistantToolSelection(IReadOnlyList<string> Groups, IReadOnlyList<string> Tools);

/// <summary>
/// Spec §5.1: deterministic choice of tool groups from Czech word stems in the question and the last two earlier questions, plus the
/// panel context. Pure, no model. There is no API level input (D19): write groups are offered to anyone allowed to use the agent
/// and the shared handlers enforce content rights; <c>publikace</c> only when the LAST question asks to publish (D18). The module
/// MCP switch is not read (D17).
/// The tool lists name tools of later work packages too (<c>atlas_apply_labels</c>, <c>atlas_summarize_pages</c>); the session
/// (WP-12) must intersect the result with the tools that actually exist. At most <see cref="MaxTools"/> tools: a group that would
/// not fit whole is dropped, lowest priority first (the order of <see cref="Defs"/>).
/// A write or expensive group names the read group it needs (<c>Needs</c>: labels, drafts and moves need the tree (moves its treeVersion), summaries the tree to find the pages, drafts to find the parent of a new
/// page or the page "X" of "přeformátuj podle X") and is only chosen together with it, so
/// the cap drops lower-priority groups instead of the tree. A label request ("navrhni ikonky", "otaguj", "dej … tag") is
/// <c>stitky</c>, not <c>koncepty</c> ("doplň ikonky" too); converting text into contacts ("převeď na kontakty") is a draft; the left menu
/// ("poskládej menu", "seřaď") is the tree, so the batch move of <c>menu</c>. Drafts only when the object is text (section, paragraph, content) or the verb is a writing verb
/// without a label noun in the last question.
/// </summary>
public static class AssistantToolGroups
{
    public const int MaxTools = 14;

    /// <summary>The groups that write (as proposals); a turn with one of them is a task, not a question about the wiki.</summary>
    public static readonly IReadOnlySet<string> WriteGroups = new HashSet<string>(StringComparer.Ordinal) { "publikace", "nasazeni", "stitky", "koncepty", "presuny", "menu" };
    /// <summary>The left menu of a space as one proposal (<c>atlas_arrange_pages</c>), offered only while <c>assistant.arrange-pages</c> is on.</summary>
    public const string MenuGroup = "menu";
    private const int HistoryDepth = 2;

    /// <summary>Priority order. Stems are regex fragments over text lower-cased and stripped of diacritics, matched at a word start.</summary>
    private sealed record Def(string Name, string[] Tools, string[] Stems, string[]? NeedsVerb = null, bool LastQuestionOnly = false, string? Needs = null,
        bool Projector = false);

    private static readonly string[] WriteVerbs =
        ["pridej", "pridat", "pridan", "odeber", "odstran", "nastav", "zmen", "prirad", "oznac", "otaguj", "ostitkuj", "prebarv", "obarv", "vymen",
         "navrh(?!l)", "dej(?:te)?(?![a-z])", "dopln"];

    private static readonly Def[] Defs =
    [
        new("zaklad", ["atlas_search", "atlas_get_page", "atlas_list_spaces"], []),
        new("publikace", ["atlas_publish"], ["publikuj", "publikovat", "zverejn", "uverejn"], LastQuestionOnly: true),
        // Plan 3.5 C: „založ mi projekt podle Dubovic a přidej telekonference“, a copy of a deployment with modules added, in one proposal;
        // only with the module, and with the requirement reads to find the source and the module (projector_overview, projector_get_catalog).
        new("nasazeni", ["projector_create_deployment"], ["projekt", "nasazen", "zakazk"], NeedsVerb: ["zaloz(?!il)", "vytvor(?!il)", "nov[ayeo]"],
            Needs: "pozadavky", Projector: true),
        new("stitky", ["atlas_apply_labels", "atlas_suggest_tags"], ["tag", "stitk", "stitek", "ikon", "barv", "otaguj", "ostitkuj"], NeedsVerb: WriteVerbs, Needs: "struktura"),
        new("koncepty", ["atlas_create_draft", "atlas_update_section", "atlas_insert_section", "atlas_replace_page", "atlas_rename_page"],
            ["napis", "uprav(?!il)", "preformatuj", "zaloz(?!il)", "vytvor(?!il)", "prepis(?!al)", "prejmenuj", "nahrad(?!il)", "sekc", "koncept", "navrh(?!l)",
             "konvert", "zkonvert", "(?:na|do) kontakt"],
            Needs: "struktura"),
        new("presuny", ["atlas_move_page", "atlas_move_page_to_space"], ["presun", "prenes", "prerad", "pod stranku", "podstranku"], Needs: "struktura"),
        // A switch of its own (assistant.arrange-pages): an ordinary move question does not carry the batch tool.
        new("menu", ["atlas_arrange_pages"], ["usporad", "serad", "posklad", "poradi", "menu"], Needs: "struktura"),
        new("struktura", ["atlas_get_space_tree", "atlas_list_pages", "atlas_get_labels", "atlas_list_appearance", "atlas_find_pages_by_tag"],
            ["strom", "struktur", "hierarch", "ikon", "tag", "stitk", "stitek", "barv", "prostor", "podstran", "nemaji", "nemaj"]),
        new("vyskyty", ["atlas_find_mentions", "atlas_get_backlinks"],
            ["vsude", "vsech", "zmin", "odkaz", "vyskyt", "stran\\w* o "]),
        new("pristup", ["atlas_explain_access"], ["pristup", "opravn", "proc vidi", "kdo vidi", "kdo ma pravo"]),
        new("historie", ["atlas_get_history", "atlas_list_attachments", "atlas_get_attachment"],
            ["histori", "autor", "priloh", "verz", "revize", "revizi", "zmenil", "upravil", "napsal", "vytvoril", "zalozil", "prepsal"]),
        new("souhrny", ["atlas_summarize_pages"], ["shrn", "souhrn", "prehled"], Needs: "struktura"),
        // Review K2: requirement pages answer from their attributes (states, parties, what is waited on), which search alone misses; every
        // turn with the module on gets the reads, lowest priority. Its writes have no proposal shape yet, so they stay with MCP clients.
        new("pozadavky", ["projector_get_requirements", "projector_waiting", "projector_overview", "projector_get_catalog", "projector_query",
            "projector_get_requirement_history"], []),
    ];

    private static readonly Regex[] StemRegexes = Defs.Select(d => Compile(d.Stems)).ToArray();
    private static readonly Regex[] VerbRegexes = Defs.Select(d => Compile(d.NeedsVerb ?? [])).ToArray();
    private static readonly Regex LabelNoun = Compile(["tag", "stitk", "stitek", "ikon", "barv", "otaguj", "ostitkuj"]);
    private static readonly Regex MenuNoun = Compile(["usporad", "serad", "posklad", "poradi", "menu"]);
    private static readonly Regex ContentObject = Compile(["text", "sekc", "odstav", "koncept", "obsah", "clanek", "nadpis"]);
    private static readonly Regex RequirementWords = Compile(["pozadav", "zakazk", "nasazen", "ceka", "blok", "stav"]);
    private static readonly int Structure = Array.FindIndex(Defs, def => def.Name == "struktura");
    private static readonly int Requirements = Array.FindIndex(Defs, def => def.Name == "pozadavky");
    private static readonly Regex Deictic = Compile(["tady", "zde", "tuto ", "tato ", "teto ", "tomto ", "tehle", "tuhle"]);

    /// <param name="switchedOff">Groups the admin switched off (<see cref="MenuGroup"/> without <c>assistant.arrange-pages</c>); their
    /// stems choose nothing and their tools leave room in the budget for the others.</param>
    public static AssistantToolSelection Select(string question, IReadOnlyList<string>? recentQuestions, AssistantToolContext? context,
        IReadOnlySet<string>? switchedOff = null)
    {
        var last = Normalize(question);
        var scope = string.Join(' ', (recentQuestions ?? []).TakeLast(HistoryDepth).Select(Normalize).Append(last));

        var chosen = new HashSet<string>();
        var used = new HashSet<string>();
        // Plan 3.5 I: a question about requirements takes their reads before the other read groups (still after zaklad and every write
        // group), so "kdo změnil … v prostoru" does not push them out of the budget with historie and struktura.
        var order = Enumerable.Range(0, Defs.Length).ToList();
        if (context is { Projector: true } && RequirementWords.IsMatch(scope))
        {
            order.Remove(Requirements);
            order.Insert(Structure, Requirements);
        }
        foreach (var i in order)
        {
            var def = Defs[i];
            if (switchedOff?.Contains(def.Name) == true)
            {
                continue;
            }
            var text = def.LastQuestionOnly ? last : scope;
            var hit = def.Name == "zaklad" || def.Name == "pozadavky" && context is { Projector: true }
                || def.Stems.Length > 0 && (!def.Projector || context is { Projector: true }) && StemRegexes[i].IsMatch(text)
                    && (def.NeedsVerb is null || VerbRegexes[i].IsMatch(text))
                // An open space plus "tady" / "této stránky" is a question about its structure.
                || def.Name == "struktura" && context is { SpaceOpen: true } && Deictic.IsMatch(last);
            // "navrhni ikonky" is a label request and "navrhni pořadí menu" a move: drafts need text as the object or no label or menu
            // noun in the last question (drafts would push the moves out of the tool budget).
            if (def.Name == "koncepty" && hit && (LabelNoun.IsMatch(last) || MenuNoun.IsMatch(last)) && !ContentObject.IsMatch(last))
            {
                hit = false;
            }
            if (!hit)
            {
                continue;
            }

            var needed = def.Needs is null ? null : Defs.Single(d => d.Name == def.Needs);
            var extra = def.Tools.Concat(needed?.Tools ?? []).Where(tool => !used.Contains(tool)).Distinct().ToArray();
            if (used.Count + extra.Length > MaxTools)
            {
                continue;
            }

            chosen.Add(def.Name);
            if (needed is not null)
            {
                chosen.Add(needed.Name);
            }
            used.UnionWith(extra);
        }

        var picked = Defs.Where(def => chosen.Contains(def.Name)).ToArray();
        return new AssistantToolSelection(picked.Select(def => def.Name).ToArray(), picked.SelectMany(def => def.Tools).Distinct().ToArray());
    }

    private static Regex Compile(string[] stems) =>
        new(stems.Length == 0 ? "(?!)" : "(?<![a-z0-9])(?:" + string.Join('|', stems) + ")", RegexOptions.CultureInvariant);

    /// <summary>Lower case, no diacritics, punctuation to spaces, padded so a trailing-space stem ("tato ") also matches at the end.</summary>
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return sb.Append(' ').ToString();
    }
}
