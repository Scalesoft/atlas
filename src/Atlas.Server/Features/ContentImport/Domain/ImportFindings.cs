using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// Collects what a page conversion could not carry over unchanged. Occurrences of one construct are
/// counted into a single finding that keeps the first line and excerpt, so a page with hundreds of
/// inline styles still yields a short, reviewable report. Construct names shared with the editor's
/// paste/drop import (<c>MarkdownImportDegradedKind</c>) are spelled the same: wiki-link, embed, comment,
/// footnote, subscript, superscript and math; docs/architecture.md lists the deliberate differences.
/// </summary>
public sealed class ImportFindings
{
    private const int MaximumConstructs = 100;
    private const int MaximumExcerpt = 160;

    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        ["subscript"] = "Dolní index nemá v editoru obdobu; zůstává jako text se zdrojovým zápisem.",
        ["superscript"] = "Horní index nemá v editoru obdobu; zůstává jako text se zdrojovým zápisem.",
        ["kbd"] = "Klávesová zkratka <kbd> je převedená na formátování kódu.",
        ["comment"] = "Komentář (HTML <!-- --> nebo Obsidian %%…%%) byl vypuštěn; je jen v původním ZIP.",
        ["obsidian-block-id"] = "Identifikátor bloku Obsidianu (^id) byl vypuštěn.",
        ["html-style"] = "Vložené styly HTML (barvy, rozměry) byly vypuštěny; text zůstává.",
        ["html-table-merged"] = "Tabulka HTML používá slučování buněk; v editoru jsou buňky samostatné.",
        ["html-table-flattened"] = "Buňka tabulky HTML obsahovala odstavce nebo seznam; obsah je sloučený do jednoho řádku.",
        ["html-table-header"] = "Tabulka HTML bez hlavičky; první řádek je použit jako hlavička.",
        ["link-unresolved"] = "Odkaz nelze bezpečně přeložit; zůstává jako text.",
        ["source-link-unresolved"] = "Odkaz na stránku původní wiki, která není v importu; odkaz vede na původní adresu.",
        ["wiki-link"] = "Wiki odkaz neodpovídá žádné importované stránce ani souboru; zůstává jako text.",
        ["wiki-link-ambiguous"] = "Wiki odkaz odpovídá více stránkám stejně dobře; použita nejbližší, zkontrolujte cíl.",
        ["embed"] = "Vložený soubor nebo stránka (![[…]]) není v importu; zůstává jako text.",
        ["anchor-unresolved"] = "Kotva odkazu neodpovídá žádnému nadpisu cílové stránky; odkaz vede na začátek stránky.",
        ["block-reference"] = "Odkaz na blok (^id) nemá v editoru obdobu; odkaz vede na stránku.",
        ["embed-page"] = "Vložení (transkluze) stránky je převedené na odkaz.",
        ["embed-ambiguous"] = "Soubor se stejným jménem je v exportu vícekrát; použit nejbližší ke stránce.",
        ["image-unavailable"] = "Obrázek není v exportu bezpečně dostupný; zůstává popis nebo odkaz bez automatického načítání.",
        ["callout-kind"] = "Neznámý typ upozornění je převedený na Poznámku.",
        ["callout-fold"] = "Sbalitelný stav upozornění se nepřenáší; obsah je vždy rozbalený.",
        ["diagram-invalid"] = "Diagram Mermaid obsahuje HTML, konfigurační direktivy nebo je příliš dlouhý; zůstává jako zdrojový kód.",
        ["diagram-unsupported"] = "Tento typ diagramu editor nevykresluje; zůstává jako zdrojový kód.",
        ["math"] = "Vzorec je prázdný nebo delší než 10 000 znaků; zůstává jako text.",
        ["footnote"] = "Poznámky pod čarou jsou převedené na oddíl na konci stránky s odkazy tam a zpět.",
        ["task-ordered"] = "Úkoly v číslovaném seznamu zůstávají jako text [ ] / [x].",
        ["definition-list"] = "Seznam definic je převedený na tučný termín a odstavce.",
        ["table-too-large"] = "Tabulka překračuje 1000 buněk editoru; zůstává jako zdrojový text.",
        ["wikijs-attributes"] = "Atribut Wiki.js {.třída} (styl, tabset) byl vypuštěn.",
        ["dynamic-query"] = "Dotaz pluginu (Dataview, Tasks) se neprovádí; zůstává jako zdrojový kód.",
        ["unsupported-block"] = "Nepodporovaný blok zůstává jako čitelný zdrojový kód.",
        ["front-matter-invalid"] = "Metadata nejsou platné YAML; převzaty pouze jednoduché řádky.",
        ["tag-invalid"] = "Štítek je prázdný, delší než 48 znaků nebo přesahuje limit 20 štítků; nepřenesen.",
        ["html-dropped"] = "Interaktivní nebo prezentační prvek HTML byl vypuštěn; je jen v původním ZIP.",
        ["html-depth"] = "HTML je vnořené hlouběji než 32 úrovní; hlubší obsah byl vypuštěn a je jen v původním ZIP.",
    };

    private readonly Dictionary<(string Construct, string Impact), Entry> m_entries = [];
    private readonly List<(string Construct, string Impact)> m_order = [];

    public static string MessageFor(string construct)
    {
        if (Messages.TryGetValue(construct, out var message))
        {
            return message;
        }
        if (construct.StartsWith("html:", StringComparison.Ordinal))
        {
            return "Značka <" + construct[5..] + "> nemá v editoru obdobu; text je zachovaný bez ní.";
        }
        if (construct.StartsWith("html-dropped:", StringComparison.Ordinal))
        {
            return "Vypuštěn " + WikiHtmlToMarkdown.DroppedLabel(construct[13..]) + " (<" + construct[13..] + ">); obsah zůstává jen v původním ZIP.";
        }
        return "Část stránky nebyla převedena beze změny; originál je v ZIP.";
    }

    public void Add(string construct, string impact, int? line = null, string? excerpt = null)
    {
        var key = (construct, impact);
        if (m_entries.TryGetValue(key, out var entry))
        {
            entry.Count++;
            return;
        }
        if (m_entries.Count >= MaximumConstructs)
        {
            return;
        }
        m_entries[key] = new Entry { Count = 1, Line = line, Excerpt = Trim(excerpt) };
        m_order.Add(key);
    }

    /// <summary>Merges findings produced by a nested conversion; their location is replaced by the enclosing one when given.</summary>
    public void AddRange(IEnumerable<WikiImportFinding> findings, int? line = null, string? excerpt = null)
    {
        foreach (var finding in findings)
        {
            var key = (finding.Construct, finding.Impact);
            if (m_entries.TryGetValue(key, out var entry))
            {
                entry.Count += finding.Count;
                continue;
            }
            if (m_entries.Count >= MaximumConstructs)
            {
                continue;
            }
            m_entries[key] = new Entry { Count = finding.Count, Line = line ?? finding.Line, Excerpt = Trim(excerpt ?? finding.Excerpt) };
            m_order.Add(key);
        }
    }

    public IReadOnlyList<WikiImportFinding> ToArray()
    {
        return m_order.Select(key =>
        {
            var entry = m_entries[key];
            return new WikiImportFinding(key.Construct, key.Impact, entry.Count, entry.Line, entry.Excerpt, MessageFor(key.Construct));
        }).ToArray();
    }

    private static string? Trim(string? excerpt)
    {
        if (excerpt is null)
        {
            return null;
        }
        var value = excerpt.Trim();
        if (value.Length == 0)
        {
            return null;
        }
        return value.Length <= MaximumExcerpt ? value : value[..MaximumExcerpt] + "…";
    }

    private sealed class Entry
    {
        public int Count { get; set; }

        public int? Line { get; init; }

        public string? Excerpt { get; init; }
    }
}
