using System.Globalization;
using System.Text;

namespace Atlas.Server.Platform.Content;

/// <summary>What an agent (MCP, REST) can do with one kind of page content in Markdown.</summary>
public enum SyntaxSupport
{
    /// <summary>Written as Markdown; <see cref="MarkdownToRich"/> reads it into the component.</summary>
    Write,

    /// <summary>On a page it reads as a placeholder <c>⟦c:N⟧</c> and cannot be created from Markdown.</summary>
    ReadOnly,

    /// <summary>Not Markdown, but another tool writes it.</summary>
    OtherTool,

    /// <summary>The editor has no such thing, or Markdown cannot carry it.</summary>
    Unsupported,
}

/// <summary>A mistake agents are likely to make and what to write instead.</summary>
public sealed record SyntaxMistake(string Wrong, string Right);

/// <summary>
/// An example of a writable component. <paramref name="Text"/> is what the agent writes; <paramref name="Stored"/> is what the page's
/// Markdown shows after the save, when that differs (a <c>[[Název]]</c> becomes a link); the reference test holds both to the reader.
/// </summary>
public sealed record SyntaxExample(string Text, string? Stored = null);

/// <summary>One entry of <see cref="MarkdownSyntaxReference"/>; <paramref name="Nodes"/> are the editor node types it covers.</summary>
public sealed record SyntaxComponent(string Id, string Title, SyntaxSupport Support, string[] Nodes, string Purpose)
{
    /// <summary>The exact syntax and how it reads.</summary>
    public string Syntax { get; init; } = "";

    /// <summary>Every option and its values.</summary>
    public string Options { get; init; } = "";

    public SyntaxExample? Minimal { get; init; }

    public SyntaxExample? Full { get; init; }

    public SyntaxMistake[] Mistakes { get; init; } = [];
}

/// <summary>
/// The one reference of the Markdown that agents write into a page (spec 5.2.2, MCP "atlas_get_syntax"), kept next to
/// <see cref="MarkdownToRich"/>, which is binding: the tool, the server's instructions and the descriptions of the write tools draw from
/// it, and <c>MarkdownSyntaxReferenceTests</c> holds every example to the reader and the writer and fails for an editor node that has no
/// entry. A new component of the editor therefore needs an entry here. Czech, like every MCP answer.
/// </summary>
public static class MarkdownSyntaxReference
{
    private const string Guid1 = "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b";

    /// <summary>Every kind of page content with what an agent may do with it; the nodes of the editor are each covered by one entry (tested).</summary>
    public static IReadOnlyList<SyntaxComponent> All { get; } =
    [
        new("text", "Text, nadpisy, citace, kód, odkazy", SyntaxSupport.Write,
            ["doc", "paragraph", "heading", "hardBreak", "text", "blockquote", "codeBlock", "horizontalRule"],
            "Základ stránky: odstavce, nadpisy, formátování, odkazy, citace, kód, vodorovná čára.")
        {
            Syntax = "Nadpisy `#` až `######` (úroveň se zachová). Odstavce odděluje prázdný řádek, tvrdé zalomení řádku je `\\` na konci řádku. " +
                "Značky: `**tučně**`, `*kurzíva*`, `~~přeškrtnuto~~`, `==zvýrazněno==`, `` `kód` ``, odkaz `[text](https://…)`. Citace `> text`. Blok kódu ```` ```jazyk ````; " +
                "jazyk začínající `a-` se píše ```` ```a-code a-jazyk ````. Čára `---`.",
            Options = "Odkazy jen na `http`, `https`, `mailto`, stránky wiki (`atlas://pages/{id}`, `[[Název]]`) a přílohy stránky; jiné schéma je text s upozorněním. " +
                "Podtržení Markdown nenese. Syrové HTML je převedené na text s upozorněním.",
            Minimal = new("## Postup\n\nText s **tučným** a [odkazem](https://example.test)."),
            Full = new("## Postup\n\nPrvní řádek\\\ndruhý řádek, ==zvýrazněno==, ~~staré~~, `kód`.\n\n> Citace\n\n```csharp\nvar x = 1;\n```\n\n---"),
            Mistakes =
            [
                new("<b>tučně</b>, <br> a jiné HTML", "Markdown: `**tučně**`, tvrdé zalomení `\\` na konci řádku."),
                new("Nadpis tučným odstavcem (`**Postup**`)", "Opravdový nadpis: `## Postup`. Sekce a headingId vznikají jen z nadpisů."),
            ],
        },
        new("list", "Odrážkový a číslovaný seznam", SyntaxSupport.Write, ["bulletList", "orderedList", "listItem"],
            "Seznam odrážek nebo číslovaných kroků, i vnořený.")
        {
            Syntax = "Odrážka `- text` (nebo `*`, `+`), číslovaný seznam `1. text`; vnoření odsazením o dvě až čtyři mezery.",
            Options = "Číslovaný seznam začíná číslem prvního řádku. Styl číslování (`a.`, `i.`) Markdown nenese, uložený se při úpravě zachová.",
            Minimal = new("- první\n- druhá"),
            Full = new("1. Krok\n   - podbod\n   - další podbod\n2. Další krok"),
            Mistakes = [new("Seznam psaný jako `• první` nebo `1) první` na řádcích bez Markdownu", "`- první` a `1. první`.")],
        },
        new("task-list", "Seznam úkolů", SyntaxSupport.Write, ["taskList", "taskItem"], "Odrážky se zaškrtávacím políčkem.")
        {
            Syntax = "`- [ ] text` nezaškrtnuto, `- [x] text` zaškrtnuto.",
            Minimal = new("- [ ] Zavolat klientovi\n- [x] Poslat nabídku", Stored: "- [ ] Zavolat klientovi\n\n- [x] Poslat nabídku"),
            Mistakes = [new("`[ ] text` bez odrážky, nebo `- [] text`", "`- [ ] text` (mezera uvnitř závorek, před nimi pomlčka).")],
        },
        new("timed-list", "Časová odrážka (záznamy s datem a časem)", SyntaxSupport.Write, ["timedList", "timedItem"],
            "Poznámky, deník, historie změn: každý záznam začíná datem a případně časem.")
        {
            Syntax = "Odrážkový seznam, jehož první položka začíná datem `D. M. RRRR` a případně časem `HH:MM`, pak pomlčka a text: `- 3. 11. 2026 17:19 — text`. " +
                "Datum na začátku řádku píšete přesně takto, server se postará o zápis.",
            Options = "Čas lze vynechat (`- 3. 11. 2026 — text`).",
            Minimal = new("- 3. 11. 2026 17:19 — Schváleno vedením.", Stored: "- 3\\. 11. 2026 17:19 — Schváleno vedením."),
            Full = new("- 3. 11. 2026 17:19 — Schváleno vedením.\n- 4. 11. 2026 — Nasazeno do provozu.",
                Stored: "- 3\\. 11. 2026 17:19 — Schváleno vedením.\n- 4\\. 11. 2026 — Nasazeno do provozu."),
            Mistakes = [new("Datum v jiném tvaru (`2026-11-03`, `3.11.`) nebo odstavec místo odrážky", "`- 3. 11. 2026 17:19 — text`.")],
        },
        new("callout", "Informační box", SyntaxSupport.Write, ["callout"],
            "Barevně zvýrazněný blok (poznámka, tip, upozornění) uvnitř stránky. V Markdownu je to GitHub alert.")
        {
            Syntax = "Citace, jejíž **první řádek je jen značka** `[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]` nebo `[!CAUTION]`; text boxu je na dalších řádcích citace. " +
                "Uvnitř smí být odstavce, seznamy i kód, každý řádek začíná `> `.",
            Options = "Druh: `NOTE` (poznámka), `TIP` (tip), `IMPORTANT` (důležité), `WARNING` (upozornění), `CAUTION` (pozor, riziko). Velikost písmen ve značce nehraje roli, píše se velkými. " +
                "Žádné jiné druhy (`INFO`, `DANGER`) neexistují.",
            Minimal = new("> [!WARNING]\n> Kdo soubor dostane, může se jím přihlásit."),
            Full = new("> [!IMPORTANT]\n> **Pozor**\n>\n> Druhý odstavec boxu.\n>\n> - bod seznamu\n> - druhý bod"),
            Mistakes =
            [
                new("`> [!WARNING] Text na stejném řádku jako značka`", "Značka sama na řádku, text na dalším: `> [!WARNING]` a pod ním `> Text`."),
                new("`[!INFO]`, `[!DANGER]`, `:::warning`, `⚠️ Pozor:`", "Jen pět druhů: NOTE, TIP, IMPORTANT, WARNING, CAUTION."),
            ],
        },
        new("table", "Tabulka", SyntaxSupport.Write, ["table", "tableRow", "tableHeader", "tableCell"],
            "Obyčejná tabulka s jedním řádkem záhlaví a buňkami s textem (formátování, odkazy, vzorce, zmínky kontaktů).")
        {
            Syntax = "Tabulka GFM: záhlaví, oddělovací řádek `| --- |` a řádky; v buňce `\\|` je svislítko, `<br>` zalomení řádku.",
            Options = "Zarovnání sloupce v oddělovači: `:---` vlevo, `:---:` na střed, `---:` vpravo. Sloučené buňky, buňky s více bloky a nestejné řádky GFM nenese: použijte `a-table`.",
            Minimal = new("| Název | Hodnota |\n| --- | --- |\n| A | 1 |\n| B | 2 |"),
            Full = new("| Název | Počet |\n| :--- | ---: |\n| **A** | 1 |\n| B<br>druhý řádek | 2 |"),
            Mistakes =
            [
                new("Tabulka bez oddělovacího řádku `| --- |`", "Za záhlavím vždy `| --- | --- |`."),
                new("Tabulka s odlišným počtem buněk v řádcích nebo s kopií tabulky grafu", "Sloučené či nestejné buňky `a-table`; tabulka napojená na graf viz `chart`."),
            ],
        },
        new("a-table", "Tabulka se sloučenými buňkami (a-table)", SyntaxSupport.Write, ["table"],
            "Tabulka, kterou GFM neunese: sloučené buňky, buňky s více bloky, nestejné řádky.")
        {
            Syntax = "Blok ```` ```a-table ```` s YAML seznamem řádků: `- cells:` a pod ním buňky `- content: text`. Obsah buňky je Markdown.",
            Options = "Tabulka bez sloučených buněk se uloží jako obyčejná tabulka GFM. Klíče buňky: `header: true` (buňka záhlaví), `colspan`, `rowspan` (1–1000), `align` (`left`, `center`, `right`), `content` (Markdown buňky). Jiný klíč je chyba `invalid_document`.",
            Minimal = new("```a-table\n- cells:\n    - header: true\n      colspan: 2\n      content: Hlavička\n- cells:\n    - content: A\n    - content: B\n```"),
            Mistakes = [new("```` ```a-table ```` s tabulkou Markdownu uvnitř", "Pro obyčejnou tabulku použijte GFM (`table`); `a-table` je YAML.")],
        },
        new("math", "Vzorec", SyntaxSupport.Write, ["mathInline", "mathBlock"], "Matematický zápis (TeX) v textu nebo jako samostatný blok.")
        {
            Syntax = "V textu `$E = mc^2$`; blok řádky `$$` (začátek i konec na vlastním řádku) nebo ```` ```math ````. Zdroj je TeX.",
            Options = "`$` musí přiléhat k obsahu a za uzavíracím `$` nesmí být číslice ani písmeno (`$5 a $10` zůstane textem). Neuzavřené `$$` zůstane textem; vzorec, který editor odmítne (prázdný), zůstane textem nebo kódem.",
            Minimal = new("Platí $E = mc^2$."),
            Full = new("Platí $E = mc^2$.\n\n$$\n\\int_0^1 x\\,dx = \\frac{1}{2}\n$$"),
            Mistakes =
            [
                new("`\\( x \\)` a `\\[ x \\]`, `$ x $` s mezerami u dolarů", "`$x$` bez mezer; blok `$$` na vlastních řádcích."),
                new("Prázdný řádek uvnitř `$$` bloku", "Blok `$$` bez prázdného řádku uvnitř."),
            ],
        },
        new("diagram", "Diagram (Mermaid)", SyntaxSupport.Write, ["diagram"], "Vývojový, sekvenční a jiné diagramy ze zdroje Mermaid.")
        {
            Syntax = "Blok ```` ```mermaid ```` se zdrojem Mermaid.",
            Options = "Zdroj s HTML nebo direktivou `%%{…}` editor odmítne a zůstane blokem kódu. Titulek diagramu Markdown nenese (diagram s titulkem se čte jako značka `⟦c:N⟧`).",
            Minimal = new("```mermaid\nflowchart LR\n  A --> B\n```"),
            Full = new("```mermaid\nsequenceDiagram\n  Uživatel->>Server: Požadavek\n  Server-->>Uživatel: Odpověď\n```"),
            Mistakes =
            [
                new("```` ```plantuml ````, ```` ```diagram ````, obrázek diagramu", "Jen ```` ```mermaid ```` (jiný jazyk je obyčejný kód)."),
                new("Mermaid se zdrojem s `<script>` nebo `%%{init}%%`", "Zdroj bez HTML a bez direktiv."),
            ],
        },
        new("contact", "Kontakt", SyntaxSupport.Write, ["contactCard", "contactInline"],
            "Odkaz na člověka z adresáře kontaktů, jako karta nebo zmínka v textu. Kontakt je jedna entita: odkazujte na existující, nevytvářejte kopii ani textovou náhradu.")
        {
            Syntax = "Karta: blok ```` ```a-contact ```` s YAML seznamem; jeden kontakt na blok, několik lidí = několik bloků za sebou. V textu: `[Jméno](a-contact:?name=…&objectId=…)` (hodnoty procentově kódované, mezera `%20`; popisek se nečte). " +
                "`objectId` získáte z `atlas_list_entities` nebo z odpovědi `atlas_upsert_contact`.",
            Options = "Klíče v tomto pořadí: `name` (povinné), `role` (místní role na stránce), `title`, `organization`, `department`, `username`, `phones` (seznam), `emails` (seznam), `website`, `address`, " +
                "`photo`, `kind` (`auto`, `internal`, `external`), `directoryId`, `objectId` (GUID existujícího kontaktu), `internalUser` (`id`, `name`, `photo`). V odkazu telefony a e-maily jako opakované `phone=` a `email=`. " +
                "Kontakt bez `objectId` server propojí s adresářem podle e-mailu či účtu, jinak ho založí (odpověď to uvede).",
            Minimal = new("```a-contact\n- name: Jana Nováková\n```"),
            Full = new($"```a-contact\n- name: Jana Nováková\n  role: PM projektu\n  title: Vedoucí provozu\n  organization: Example\n  phones: [\"+420 123 456 789\"]\n  emails: [jana.novakova@example.test]\n  objectId: {Guid1}\n```\n\nVolejte [Jana Nováková](a-contact:?name=Jana%20Nováková&objectId={Guid1})."),
            Mistakes =
            [
                new("Jméno a telefon psané textem „Jana Nováková, tel. 123“", "Blok `a-contact` nebo odkaz `[Jana](a-contact:?name=…)`; nejdřív zkuste najít existující kontakt (`atlas_list_entities`)."),
                new("`[Jana](a-contact:Jana)` bez `?name=`", "`[Jana](a-contact:?name=Jana)`."),
                new("Neznámý klíč v YAML nebo více lidí v jednom bloku s různými klíči", "Jen klíče výše; blok bez chyb, jinak `400 invalid_document` s řádkem."),
            ],
        },
        new("chart", "Graf", SyntaxSupport.Write, ["chart"], "Sloupcový, čárový, koláčový nebo prstencový graf z mřížky hodnot, volitelně napojený na tabulku stránky.")
        {
            Syntax = "Blok ```` ```a-chart ````: hlavička (`title`, `kind`, `source`), řádek `---` a mřížka jako tabulka Markdownu. První řádek mřížky jmenuje řady, první sloupec kategorie, hodnoty jsou čísla s desetinnou čárkou (`12,5`); " +
                "prázdná buňka je mezera v datech, `\\|` svislítko v buňce.",
            Options = "`title` (text), `kind`: `bar` (výchozí), `line`, `pie`, `doughnut`. `source` (graf z tabulky stránky): `table` (id tabulky), `rows` a `columns` od 1 včetně záhlaví, např. `1-3`; tabulka pak má hned za sebou odstavec `{#id}`. " +
                "Hodnoty se pak berou z tabulky (liší-li se mřížka, platí tabulka a zápis varuje). Graf napojený na tabulku a jeho tabulka zůstávají při čtení značkou `⟦c:N⟧`.",
            Minimal = new("```a-chart\ntitle: Tržby\n---\n| Měsíc | Tržby |\n| --- | --- |\n| Leden | 12,5 |\n| Únor | 18 |\n```"),
            Full = new("| Měsíc | Tržby |\n| --- | --- |\n| Leden | 12,5 |\n| Únor | 18 |\n\n{#trzby}\n\n```a-chart\ntitle: Tržby 2026\nkind: line\nsource:\n  table: trzby\n  rows: 1-3\n  columns: 1-2\n---\n| Měsíc | Tržby |\n| --- | --- |\n| Leden | 12,5 |\n| Únor | 18 |\n```"),
            Mistakes =
            [
                new("Graf bez řádku `---` nebo s hlavičkou bez mřížky", "Hlavička, `---`, mřížka s oddělovačem `| --- |`; jinak `400 invalid_document`."),
                new("Čísla s jednotkou (`12 Kč`) nebo s tečkou (`12.5`)", "Čísla s desetinnou čárkou bez jednotky (`12,5`); jednotku dejte do `title`."),
                new("Obrázek grafu nebo tabulka místo grafu", "Blok `a-chart`."),
            ],
        },
        new("overview", "Přehled", SyntaxSupport.Write, ["overview"], "Souhrn komponent jednoho druhu z celé stránky (kontakty, úkoly YouTrack, stav požadavků).")
        {
            Syntax = "Blok ```` ```a-overview ```` s jediným řádkem `kind: …`.",
            Options = "`kind`: `contacts` (kontakty stránky), `issues` (úkoly YouTrack), `requirements` (stav nasazení; v Markdownu se čte jako řádek **Stav nasazení:** …). Stránka smí mít víc přehledů. Obsah přehledu vrací `atlas_get_overview`.",
            Minimal = new("```a-overview\nkind: contacts\n```"),
            Mistakes = [new("Další klíče nebo seznam kontaktů uvnitř bloku", "Jen `kind: …`; kontakty píšete zvlášť (`contact`).")],
        },
        new("page-link", "Odkaz na stránku", SyntaxSupport.Write, ["pageReference"], "Odkaz na jinou stránku wiki, i na její sekci.")
        {
            Syntax = "`[[Název stránky]]` nebo `[text](atlas://pages/{pageId})`, na sekci `atlas://pages/{pageId}?heading={headingId}`.",
            Options = "Název se převede na odkaz, jen když je mezi stránkami, které smíte číst, právě jedna shoda (bez ohledu na velikost písmen); jinak zůstane textem a zápis varuje.",
            Minimal = new("Viz [[Provoz]].", Stored: "Viz [Provoz](#/pages/" + Guid1 + ")."),
            Mistakes =
            [
                new("Adresa wiki zkopírovaná z prohlížeče (`https://…/#/pages/…`) jako text", "`[[Název]]` nebo `atlas://pages/{pageId}`."),
                new("`[[Název]]` se zkráceným nebo zdvojeným názvem", "Přesný název z `atlas_search`/`atlas_list_pages`, nebo `atlas://pages/{pageId}`."),
            ],
        },
        new("image", "Obrázek", SyntaxSupport.Write, ["image"], "Obrázek nahraný ke stránce, jako samostatný blok.")
        {
            Syntax = "Obrázek přílohy stránky, sám v odstavci: `![popis](/api/attachments/{attachmentId}/content)`; odkaz ke stažení souboru `[název](/api/attachments/{attachmentId}/download)`. " +
                "Přílohu nejdřív nahrajte `atlas_upload_attachment` a vezměte její `attachmentId`.",
            Options = "Volitelný titulek `![popis](…/content \"titulek\")`. Šířku a zarovnání mění `atlas_update_embed`. Obrázek z jiné adresy (`https://…`, `data:`) se převede na odkaz s upozorněním.",
            Minimal = new($"![Schéma sítě](/api/attachments/{Guid1}/content)"),
            Mistakes =
            [
                new("`![x](https://example.test/a.png)`", "Nahrát soubor ke stránce a použít jeho `/api/attachments/{id}/content`."),
                new("Obrázek uvnitř odstavce s textem, nadpisu nebo odkazu", "Obrázek v samostatném odstavci."),
            ],
        },
        new("attachment", "Příloha (karta souboru)", SyntaxSupport.ReadOnly, ["attachment"], "Karta nahraného souboru na stránce.")
        {
            Syntax = "Z Markdownu ji vytvořit nelze; na stránce je značka `⟦c:N⟧`. Odkaz ke stažení souboru napíšete jako běžný odkaz `[název](/api/attachments/{attachmentId}/download)`.",
        },
        new("openapi", "Specifikace OpenAPI", SyntaxSupport.ReadOnly, ["openapi"], "Vložená specifikace API.")
        {
            Syntax = "Z Markdownu ji vytvořit nelze; na stránce je značka `⟦c:N⟧`. Značku neměňte a nepřesouvejte.",
        },
        new("secret", "Odkaz na heslo", SyntaxSupport.ReadOnly, ["secretReference", "secretInlineReference"], "Odkaz na heslo z trezoru.")
        {
            Syntax = "Z Markdownu ji vytvořit nelze (zápis s direktivou `:secret` server odmítne); na stránce je značka `⟦c:N⟧`. Heslo nikdy nepište do textu.",
        },
        new("concealed", "Skrytý obsah", SyntaxSupport.ReadOnly, ["concealedContent"], "Obsah skrytý za zobrazením na požádání.")
        {
            Syntax = "Z Markdownu ji vytvořit nelze; na stránce je značka `⟦c:N⟧`.",
        },
        new("toc", "Obsah dokumentu", SyntaxSupport.ReadOnly, ["tableOfContents"], "Automatický obsah podle nadpisů.")
        {
            Syntax = "Z Markdownu ji vytvořit nelze; na stránce je značka `⟦c:N⟧`. Obsah vytvoří a nastaví editor ve webu.",
        },
        new("youtrack", "Úkol YouTrack", SyntaxSupport.ReadOnly, ["youtrackIssue"], "Karta úkolu YouTrack se stavem.")
        {
            Syntax = "Z Markdownu ji vytvořit nelze; na stránce je značka `⟦c:N⟧`. Na úkol odkážete běžným odkazem `[PROJ-123](https://…)`; přehled úkolů stránky dá `a-overview` s `kind: issues`.",
        },
        new("requirement", "Požadavek a souhrn požadavků", SyntaxSupport.OtherTool, ["requirement", "requirementSummary"],
            "Požadavky nasazení (Projector): nadpisy s atributy stavu, strany a závislostí.")
        {
            Syntax = "Spravují je nástroje `projector_*` (např. `projector_insert_requirements`, `projector_update_requirement`), ne ruční zápis atributů `{…}` v nadpisech. Přehled stavu: `a-overview` s `kind: requirements`.",
        },
        new("emoji", "Emoji", SyntaxSupport.Unsupported, ["emoji"], "Emoji komponentu z Markdownu vytvořit nelze.")
        {
            Syntax = "Napište přímo znak Unicode (✅); je to obyčejný text. Do názvu stránky ani do textu nevkládejte emoji místo ikony nebo stavu: ikona a tagy jsou vlastnosti stránky (`atlas_set_decoration`, `atlas_set_tags`).",
        },
        new("html", "Syrové HTML", SyntaxSupport.Unsupported, [], "HTML wiki nepřijímá.")
        {
            Syntax = "Značky HTML se převedou na text s upozorněním. Použijte Markdown; podtržení a barvy textu Markdown nenese.",
        },
    ];

    /// <summary>The entry with this id (case-insensitive), or null.</summary>
    public static SyntaxComponent? Find(string? id)
        => All.FirstOrDefault(x => string.Equals(x.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids an agent may ask for, comma separated.</summary>
    public static string Ids => string.Join(", ", All.Select(x => x.Id));

    /// <summary>One line for the server's instructions: what an agent can write, by name.</summary>
    public static string WritableSummary => string.Join(", ", All.Where(x => x.Support == SyntaxSupport.Write).Select(x => x.Id));

    /// <summary>The overview of every component, by what an agent may do with it.</summary>
    public static string Overview()
    {
        var text = new StringBuilder("Syntaxe obsahu stránky v Markdownu (atlas_get_syntax s component = id vrátí přesnou syntaxi, volby, příklady a časté chyby jedné komponenty).\n");
        Section(text, "\nLze psát Markdownem:", SyntaxSupport.Write);
        Section(text, "\nJen čtení (na stránce značka ⟦c:N⟧, z Markdownu ji vytvořit nelze; značku při úpravě nechte beze změny):", SyntaxSupport.ReadOnly);
        Section(text, "\nPíše je jiný nástroj:", SyntaxSupport.OtherTool);
        Section(text, "\nNepodporováno:", SyntaxSupport.Unsupported);
        text.Append("\nZáklad: informační box je citace se značkou na vlastním řádku (`> [!WARNING]`, pod ním `> text`); neplatná komponenta (i jiný blok ```` ```a-… ````) je chyba `invalid_document` s řádkem a nic se neuloží.\n");
        return text.ToString();
    }

    private static void Section(StringBuilder text, string heading, SyntaxSupport support)
    {
        text.Append(heading).Append('\n');
        foreach (var component in All.Where(x => x.Support == support))
        {
            text.Append("- ").Append(component.Id).Append(": ").Append(component.Title).Append(". ").Append(component.Purpose).Append('\n');
        }
    }

    /// <summary>The full entry of one component.</summary>
    public static string Describe(SyntaxComponent component)
    {
        var text = new StringBuilder();
        text.Append(component.Title).Append(" (").Append(component.Id).Append("): ").Append(component.Support switch
        {
            SyntaxSupport.Write => "lze psát Markdownem",
            SyntaxSupport.ReadOnly => "jen čtení, z Markdownu ji vytvořit nelze",
            SyntaxSupport.OtherTool => "píše ji jiný nástroj",
            _ => "nepodporováno",
        }).Append("\n\nÚčel: ").Append(component.Purpose).Append("\n\nSyntaxe: ").Append(component.Syntax).Append('\n');
        if (component.Options.Length > 0)
        {
            text.Append("\nVolby: ").Append(component.Options).Append('\n');
        }
        Example(text, "Minimální příklad", component.Minimal);
        Example(text, "Plný příklad", component.Full);
        if (component.Mistakes.Length > 0)
        {
            text.Append("\nČasté chyby:\n");
            foreach (var mistake in component.Mistakes)
            {
                text.Append("- Špatně: ").Append(mistake.Wrong).Append(" → Správně: ").Append(mistake.Right).Append('\n');
            }
        }
        return text.ToString();
    }

    private static void Example(StringBuilder text, string heading, SyntaxExample? example)
    {
        if (example is null)
        {
            return;
        }
        text.Append('\n').Append(heading).Append(":\n````markdown\n").Append(example.Text).Append("\n````\n");
        if (example.Stored is not null)
        {
            text.Append("Na stránce se uloží jako: ").Append(example.Stored.Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
        }
    }

    /// <summary>The unknown-name answer: the valid ids.</summary>
    public static string Unknown(string? component)
        => string.Create(CultureInfo.InvariantCulture, $"Komponenta „{component}“ není v referenci. Platné názvy: {Ids}.");
}
