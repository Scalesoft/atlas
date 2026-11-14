<script setup lang="ts">
import { ref, shallowRef } from "vue";
import { MarkdownEditor, MarkdownRenderer } from "@atlas/markdown/vue";
import { formatMarkdownShortcut, markdownKeyboardShortcuts, markdownTypingShortcuts, MARKDOWN_SHORTCUT_HELP_EVENT, type MarkdownEditorController, type MarkdownRichValue, type MarkdownShortcut } from "@atlas/markdown/core";
import { createContactsPlugin, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import "@atlas/markdown/styles/base.css";
import { createDocumentWidgets } from "../../documentWidgets";
import { pickContact } from "../../contacts/contactSlash";
import { rankPeople, type PersonHit } from "../../contacts/contactSearch";
import { knownContact, learnPicked } from "../../contacts/contactSources";
import { editorChrome, editorOutline, useEditorTheme } from "../../editor/editorSetup";
import { createHeadingFoldPlugin } from "../../editor/headingFoldPlugin";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

// Local sample people only; nothing leaves the gallery. Atlas's page editor gets its directory (and later its own person
// picker through the contacts plugin's `pick`) from editorDirectories.ts.
const theme = useEditorTheme();
const people: MarkdownContact[] = [
  { name: "Jan Kovařík", department: "Vývoj", emails: ["jan.kovarik@example.cz"] },
  { name: "Jana Nováková", organization: "Nemocnice Dolní Ves" },
  { name: "Petr Dvořák", department: "Provoz" },
];
const fold = (text: string) => text.normalize("NFD").replace(/\p{M}+/gu, "").toLowerCase();
const directory = {
  search: async (query: string) => people.filter(person => fold(person.name).includes(fold(query))),
  get: async () => null,
};
const plugins = [createContactsPlugin({ directory, suggestions: true }), ...createDocumentWidgets({ kind: "edit" }).filter(plugin => plugin.id !== "contacts")];
const page = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }],
  document: { type: "doc", content: [{ type: "paragraph" }] } });

// Names that are people: the sample directory as the known people — two Jana Nováková (the name asks which; the project manager
// ranks first after „PM“, the one used in this space otherwise), two „MM“ (Marek Musil, Michal Macek: initials in the picker and
// `@`), Marek's learned alias „MMu“ (underlined and offered in the text, never a chip by itself), the picker over them for „Udělat
// kontakt“. Linking is the gallery's own (the person as found); Atlas links through the directory.
const known: MarkdownContact[] = [...people,
  { name: "Jana Nováková", organization: "Modelsoft", title: "Projektová manažerka", emails: ["jana.novakova@modelsoft.example"] },
  { name: "Marek Musil", organization: "Modelsoft", title: "Vývojář" }, { name: "Michal Macek", organization: "Modelsoft", title: "Konzultant" }]
  .map((person, index) => ({ ...person, objectId: `a1b2c3d4-0000-4000-8000-0000000000${30 + index}` }));
const knownHits: PersonHit[] = known.map(contact => ({ key: `obj:${contact.objectId}`, contact, source: "directory", objectId: contact.objectId, origin: "Adresář",
  ...(contact.name === "Marek Musil" && { aliases: ["MMu"] }) }));
const asIs = async (contact: MarkdownContact) => contact;
// The hospital's Jana is the one used in this sample space.
const usage = async (objectId: string) => (objectId === known[1]!.objectId ? { here: 3, total: 4 } : { here: 0, total: 1 });
// `@` over the same people as Atlas's people search ranks them (initials, aliases); a pick runs Atlas's own `learn`
// (contactSources.ts `learnPicked`, as editorDirectories.ts gives it), whose directory change is shown here: nothing leaves the gallery.
const learned = ref("nic");
const nameDirectory = { search: async (query: string) => rankPeople(query, knownHits).map(hit => ({ ...hit.contact, objectId: hit.objectId })), get: async () => null,
  learn: (query: string, contact: MarkdownContact) => {
    learned.value = `nic („${query}“ najde jméno samo)`;
    learnPicked(query, contact, { change: async (_id, payload) => { learned.value = `„${String(payload.learnAlias)}“ → ${contact.name}`; return { version: 1, duplicate: false }; } });
  } };
const namePlugins = [createContactsPlugin({ directory: nameDirectory, suggestions: true, known: async () => knownHits.map(knownContact), link: asIs, usage,
  pick: pickContact(async text => rankPeople(text, knownHits), asIs, () => {}) }), ...plugins.slice(1)];
const namesPage = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }], document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text: "Poradu vede Petr Dvořák, zápis pošle Jan Kovařík a podklady MMu. Za nemocnici přijde Jana Nováková." }] },
  { type: "paragraph", content: [{ type: "text", text: "Kontaktní osoba dodavatele: Marie Veselá" }] },
  { type: "paragraph" },
] } });

// Math in place: a sample with an inline and a block formula; nothing else to set up (the math plugin is part of the document widgets).
const formulas = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "math", version: 1 }], document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text: "Obsah kruhu je " }, { type: "mathInline", attrs: { data: { version: 1, expression: "\\pi r^2" } } }, { type: "text", text: ", cena zůstává „$5 a $10“." }] },
  { type: "mathBlock", attrs: { data: { version: 1, expression: "x = \\frac{-b \\pm \\sqrt{b^2 - 4ac}}{2a}" } } },
  { type: "paragraph" },
] } });

// The table of contents is edited where it is shown; the sample page has two headings so the list is not empty.
const tocPage = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "toc", version: 1 }],
  document: { type: "doc", content: [
    { type: "tableOfContents", attrs: { data: { version: 1, title: "Obsah dokumentu", minLevel: 1, maxLevel: 3 } } },
    { type: "heading", attrs: { level: 1 }, content: [{ type: "text", text: "Úvod" }] },
    { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Cíle" }] },
  ] } });

// Folding by heading: the same plugin as the page (DocumentEditor), in the editor and in reading; both remember the folds as one
// sample page („galerie“), so a fold in one shows in the other.
const foldPlugins = [...plugins, createHeadingAnchorsPlugin(), createHeadingFoldPlugin(() => "galerie")];
const heading = (level: number, headingId: string, text: string) => ({ type: "heading", attrs: { level, headingId }, content: [{ type: "text", text }] });
const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const foldPage = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content: [
  heading(2, "portal", "Portál pacienta"), paragraph("Přístup přes NIA, objednávky a zprávy pacientům."),
  heading(3, "server", "Server"), paragraph("Virtuální server v DMZ, RDP pro dodavatele."),
  heading(3, "dns", "DNS a SSL"), paragraph("Doména portal.nemocnice.example, certifikát od nemocnice."),
  heading(4, "certifikat", "Podrobnosti o certifikátu"), paragraph("Dřívější rozbalovací blok je nadpis: sbalí se jako každý nadpis, šipkou na konci řádku nebo Alt+↑/↓."),
  heading(2, "mpi", "MPI"), paragraph("Napojení na NIS a registr ÚZIS."),
] } });

// A timed list (the editor's „Časová odrážka“, features/timed): two dated notes and an empty one that stamps itself on the first letter;
// below it plain bullets to turn into one from the selection bubble, the first keeping its date.
const timedItem = (text?: string) => ({ type: "timedItem", content: [text ? paragraph(text) : { type: "paragraph" }] });
const bulletItem = (text: string) => ({ type: "listItem", content: [paragraph(text)] });
const timedPage = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [
  paragraph("Telefonáty s dodavatelem:"),
  { type: "timedList", content: [timedItem("3. 11. 2026 17:19 — Domluven termín instalace"), timedItem("30. 10. 2026 — Odeslána žádost o certifikát"), timedItem()] },
  paragraph("Postup instalace (odrážky k převedení):"),
  { type: "bulletList", content: [bulletItem("2. 11. 2026 Objednán server"), bulletItem("Připraveno DNS"), bulletItem("Čeká se na certifikát")] },
] } });

const groups = [...new Set(markdownKeyboardShortcuts.map(entry => entry.group))]
  .map(group => ({ group, entries: markdownKeyboardShortcuts.filter(entry => entry.group === group) }));
const keys = (entry: MarkdownShortcut) => [entry.keys, ...entry.then ? [entry.then] : []].map(step => formatMarkdownShortcut(step)).join(", pak ");
// The editor asks its host for the overview on Ctrl+/; Atlas's shell opens its shortcut help, the gallery marks the list.
const asked = ref(false);
function onHelp(event: Event) {
  event.preventDefault();
  asked.value = true;
  setTimeout(() => { asked.value = false; }, 1200);
}
</script>

<template>
  <SettingsPage title="Editor z klávesnice" description="Stránka se dá napsat bez myši: lomítko a pár písmen vloží blok nebo komponentu, text za příkazem do ní rovnou vteče, Markdown se píše jako Markdown a Tab z editoru nikdy nevypadne omylem.">
    <SettingsSection title="Vyzkoušejte"
      description="Zkuste: „/nadp 2“ Enter, „- “, „|Jméno|Telefon|“ Enter (Tab mezi buňkami, na konci nový řádek), „/kód ts“ Enter, „> [!tip] “, „/tabulka 3x4“, „/kontakt jan“ Enter a pak Enter na Janu. V nabídce je první řádek předvybraný; šipky posouvají, Enter i Tab vybere (jako každý výběr), Esc zavře a nechá text. Často použité příkazy jdou nahoru, hledá se bez diakritiky i podle zkratky („tbl“). Komponenta se upravuje tam, kde je: klik na ni (např. „/graf“ Enter a klik na graf) nebo Enter či F2 na vybrané komponentě otevře její úpravu s kurzorem v prvním poli, Esc vrátí výběr na komponentu, další Esc výběr zruší a kurzor stojí hned za komponentou (u čipu i bloku, v editoru i z jejího názvu). Panel s „Použít“ (karta kontaktu, pole požadavku, popisek, ořez a efekty obrázku, nastavení hesla a skrytého obsahu) změny klikem mimo, odchodem fokusu i druhým klikem na komponentu použije; neplatné hodnoty ho nechají otevřený s chybou u pole, klik se pak nikam nepropíše a klávesnice zůstane u chyby; po dokončení úpravy zůstane komponenta vybraná a klávesnice v editoru. Blok s textem uvnitř upravuje na místě své vlastní nastavení, text se píše jako jinde: informační box typ (klik na štítek), skrytý obsah zobrazení (klik na štítek), kód jazyk (Enter či F2 na vybraném bloku), obrázek popisek a alternativní text (klik na obrázek nebo jeho popisek; dvojklik obrázek zvětší); druhý klik úpravu zavře a „Upravit“ v liště vede na totéž. Tab otevře její lištu, šipky ji projdou, Esc vrátí do textu. Esc a pak Tab editor opustí. Čip, který vznikl sám z napsaného textu (vzorec „$x^2$ “, úkol YouTrack v požadavku, termín „do pátku “, kontakt), vrátí Backspace hned potom na text přesně, jak byl napsán, a Ctrl+Z vrátí jen převod, ne psaní; kdykoli později převede vybraný čip (nebo ten hned před kurzorem: kontakt, úkol, stránku, emoji, vzorec) na jeho viditelný text Ctrl+Shift+Mezerník nebo „Převést na text“ v jeho liště. Ctrl+Mezerník (nebo Alt+Enter, na Macu tam, kde Ctrl+Mezerník přepíná vstupní zdroj; stejně Ctrl+., Shift+F10, klávesa nabídky a pravé tlačítko, které nejdřív vybere prvek nebo slovo pod sebou, Shift + pravé nechá nabídku prohlížeče) otevře u kurzoru nebo výběru „Co s tím jde dělat“: co jde s textem udělat teď (odkaz na stránku z označených slov, úkol YouTrack z napsaného klíče, emoji z „:smile“, „Převést na text“ u čipu, u vybrané komponenty (i tabulky) její akce z lišty – úprava, převody, smazání – a Kopírovat a Smazat (u čipu Odebrat: kontakt, úkol i stránka zůstávají), v názvu požadavku Čekáme na… a Termín…, s modulem Návrhy při psaní i jeho návrhy, s Alt+Enter nahoře); první řádek je předvybraný, psaní filtruje, Enter použije, Esc zavře. Výběr v panelu editoru („/youtrack“ Vložit úkol, „Přidat z adresáře“ u kontaktu) je sdílené pole hledání (createSuggestionField: createSuggestionSearch a createSuggestionList v poli panelu, jako pole odkazu): hledá při psaní (místo pro výsledky má od otevření, takže panel neroste po skocích; řádky načítání ukáže, až když odpověď nepřijde do 100 ms), první výsledek je předvybraný, Enter nebo Tab ho vezme i dřív, než přijde odpověď, šipky se pohybují s klávesnicí v poli, první Esc smaže napsané a teprve další panel zavře; vyzkoušíte ho v ukázce „Čip v textu“ (/youtrack). Proč ne nová komponenta: je to jen složení dvou existujících dílů pro pole uvnitř panelu, kde plovoucí seznam u kurzoru nedává smysl. Editor stránky nemá rámeček ani záři: stránka je plátno a úpravy značí kurzor; klávesnici (Tab do editoru) ukáže jen tenký proužek vlevo. Rámeček s září má editor jen jako pole formuláře (dialog, komentář, blok tabule). Kurzor při psaní a pohybu plynule doklouže na nové místo a bliká až v klidu, jako ve Wordu: má ho každý editor Atlasu (sdílené editorChrome, volba smoothCaret editoru), při výběru textu, na vybrané komponentě a bez fokusu není vidět, s omezenými animacemi se jen přesune a nebliká.">
      <div class="keyboard-example" :data-help-asked="asked || undefined" @[MARKDOWN_SHORTCUT_HELP_EVENT]="onHelp">
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázka psaní z klávesnice" :plugins="plugins" :outline="editorOutline"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="Jména lidí v textu"
      description="Stránky se propojují samy, bez práce navíc: jméno známého člověka (adresář a účty Atlasu, volba known pluginu kontaktů, načtená jednou za editor, a kontakty, které stránka už má) se pozná celé, bez ohledu na velikost písmen a diakritiku. Tři cesty, každá na jindy. Když jméno píšete: mezera nebo interpunkce po celém jménu („Petr Dvořák “) z něj hned udělá kontakt (čip v textu); Backspace hned potom vrátí napsané jméno bez té mezery, mezera znovu udělá čip znovu, Ctrl+Z vrátí jen čip. Jméno dvou lidí („Jana Nováková “) se nikdy nepřiřadí samo: vznikne nerozhodnutý kontakt (čárkovaný, s „?“) s „nejspíš … · nebo …“ podle stránky (kdo už na ní je, slova kolem jména proti funkci, třeba „PM“, kdo se používá v tomto prostoru); klik nebo Enter na něm vybere osobu (když takové jméno editor nezná, otevře rovnou výběr osoby s tím jménem). Když už jméno v textu je: známá jména mají tečkované podtržení (text se neposune, jen při úpravách, nejvýš 200 na stránku); klik nabídne „Je to Jan Kovařík“ a „Není to on/ona“, Ctrl+., Ctrl+Mezerník nebo pravé tlačítko s kurzorem ve jméně totéž v nabídce akcí („Je to Jan Kovařík“, „Není to on/ona“) (podtržení u toho výskytu zmizí do zavření stránky). Když editor člověka nezná: označte jméno („Marie Veselá“) a v bublině nad výběrem zvolte „Udělat kontakt“ (nebo tentýž řádek v nabídce akcí, Ctrl+.): otevře se výběr osoby s označeným textem jako hledáním, první je předvybraný, Enter ho vloží, Nový kontakt ho založí; výběr najde i podle iniciál („MM“ oba, „MMu“ jen Marka Musila). Naučené zkratky z adresáře („MMu“ u Marka Musila, upravují se na kontaktu) se v textu podtrhnou a nabídnou jako jméno (třípísmenná jen tak, jak se naučila, „MMu“, ne zkratka „MMU“, a nikdy uvnitř e-mailu), samy se ale na čip nikdy nepřevedou (zkratky ani iniciály ne). Výběr osoby po napsání něčeho, co není začátek jména ani jiného pole („@MMu“, „MMu“ v poli strany požadavku), adresář naučí jako zkratku té osoby (řádek pod ukázkou), začátek jména („@Mar“) nic. Seznam „@“ nechá nahoře lepší shodu (začátek jména před slovem jména, to před iniciálami a jinými poli) a mezi stejně dobrými řadí podle kontextu bez AI: kdo už je na stránce, čí funkci nebo celé pole říkají slova před „@“ („PM @ja“, krátké slovo jako „na“ ne), koho jste právě vybrali. Kontakt se do adresáře sám nezapíše. V kódu, odkazech a nadpisech se jména nehledají, v polích kontaktu samotného nic z toho neběží. Skloňované jméno („Petru Dvořákovi“) se jen podtrhne a nabídne, samo se nepřevede. Rozpoznávání nezdržuje otevření stránky: je to jeden engine jádra editoru pro lidi, úkoly YouTrack i názvy stránek (ukázka „Čipy vznikají z textu“ v Čipu v textu); známí lidé se vyžádají hned se startem editoru a seznamy „Je to …?“ se načtou ve volné chvíli po jeho prvním vykreslení, takže první jméno se pozná stejně rychle jako další. Proč ne nový výběr: nabídka u jména je seznam návrhů editoru (createSuggestionList), čip je čip jádra editoru a „Udělat kontakt“ otevře tentýž ContactPicker jako /kontakt.">
      <div class="keyboard-example" data-example-contact-names>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="namesPage" profile="rich" ariaLabel="Ukázka jmen lidí v textu" :plugins="namePlugins" :outline="editorOutline"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
      <p class="st-muted" data-example-name-learned aria-live="polite">Adresář by se naučil: {{ learned }}</p>
    </SettingsSection>
    <SettingsSection title="Vzorce na místě"
      description="Vzorec se upravuje tam, kde je vidět: Enter nebo klik na vzorec otevře pole LaTeX pod ním (blok) nebo u něj (ve větě), vzorec se mění za psaní a chybu v zápisu vypíše pod polem, poslední platný vzorec zůstane. Esc pole zavře. Zkuste napsat „$x^2$ “ (vzorec ve větě; Backspace hned po něm vrátí text) a „$$“ + Enter v prázdném odstavci (blok). Částky jako „$5 a $10“ se za vzorec nepovažují. Použijte, když vzorec patří přímo do textu stránky; samostatné formuláře k tomu nejsou.">
      <div class="keyboard-example">
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="formulas" profile="rich" ariaLabel="Ukázka úpravy vzorců" :plugins="plugins" :outline="editorOutline"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="Obsah dokumentu se upravuje na místě"
      description="Komponenta, která je vidět, se upravuje tam, kde je: u Obsahu dokumentu klikněte na název (nebo na něj přejděte Tabem a stiskněte Enter), přepište ho a Enter nebo odchod z pole uloží, Esc zruší, prázdný název vrátí výchozí. Čip „H1–H3“ vedle názvu otevře malou nabídku rozsahu nadpisů (jedna úroveň, běžné rozsahy, Všechny nadpisy): první volba je vybraná, šipky a Enter ji použijí, změna se projeví hned a jde vrátit Ctrl+Z. Klik na položku obsahu dál přejde na nadpis. Žádný formulář ani Uložit; stejný princip má požadavek v Projectoru a diagram. Použijte ho pro každou komponentu, jejíž nastavení je jedno či dvě pole.">
      <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="tocPage" profile="rich" ariaLabel="Ukázka obsahu dokumentu" :plugins="plugins" :outline="editorOutline"
        v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
    </SettingsSection>
    <SettingsSection title="Sbalení sekce podle nadpisu"
      description="Šipka na konci řádku nadpisu sbalí vše pod ním až k dalšímu nadpisu stejné nebo vyšší úrovně; ukáže se při najetí, u sbaleného nadpisu je vždy, z klávesnice Alt+F10 (úchyt bloku) a Tab na ni a Enter, v editoru Alt+↑ a Alt+↓ s kurzorem v nadpisu. Nadpis bez obsahu pod sebou šipku nemá. Je to pohled čtenáře uložený v prohlížeči pro tuto stránku, ne obsah: do dokumentu se nic nezapíše, ostatní vidí stránku po svém a tisk ukáže všechno. Platí při čtení i při úpravách (editor nahoře, čtení dole sdílí jednu ukázkovou stránku, takže sbalení v jednom je vidět i ve druhém). Kurzor, který do sbalené sekce dojde, ji rozbalí, stejně jako odkaz na nadpis (#kotva), obsah „Na této stránce“ nebo citace. Požadavek je obyčejný nadpis a sbaluje se stejně (stejná šipka a Alt+↑/↓). Rozbalovací blok má šipku také na konci řádku a stejné klávesy, ale je obsahem stránky.">
      <div class="keyboard-example" data-example-heading-fold>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="foldPage" profile="rich" ariaLabel="Ukázka sbalení sekce podle nadpisu" :plugins="foldPlugins" :outline="editorOutline"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
      <div class="keyboard-example reading-example" data-example-heading-fold-reading>
        <MarkdownRenderer :value="foldPage" profile="rich" :plugins="foldPlugins" ariaLabel="Ukázka sbalení sekce při čtení" :class="theme" />
      </div>
    </SettingsSection>
    <SettingsSection title="Časová odrážka"
      description="Odrážkový seznam poznámek s datem a časem zápisu, na jakékoli stránce i v poznámkách požadavku („/cas“, Ctrl+Shift+D nebo tlačítko s hodinami mezi typy seznamů v bublině nad výběrem; vstupní pravidlo nemá). Hotové odrážky, číslovaný seznam i checklist převede na místě: označte položky (nebo jen stůjte v seznamu) a zvolte Časovou odrážku; každá položka dostane razítko, ta, která už datem začíná („2. 11. 2026 Objednán server“), si ho nechá. Jiný typ seznamu ji vrátí zpět a razítka zůstanou textem; obojí je jedno Ctrl+Z. Každá nová položka hned dostane dnešní datum a čas (Praha), který jde přepsat: Enter na konci položky založí další, psaní do prázdné položky ji orazítkuje prvním písmenem (číslice ne, to píšete vlastní datum), Backspace hned za čerstvým razítkem razítko smaže a další Backspace položku, jako u odrážek; každý krok je jedno Ctrl+Z. Den a čas jsou tlumený čip nad textem položky, text zůstává textem, Markdown je běžný odrážkový seznam („- 3. 11. 2026 17:19 — text“). Kdy ji zvolit místo odrážek: když záleží na tom, kdy co zaznělo (telefonáty, záznamy postupu); pro výčet bez času zůstaňte u odrážek, pro úkoly se stavem (hotovo, nehotovo) u checklistu a pro záznamy s více sloupci (kdo, co, do kdy) u tabulky. Dole totéž při čtení.">
      <div class="keyboard-example" data-example-timed-list>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="timedPage" profile="rich" ariaLabel="Ukázka časové odrážky" :plugins="plugins" :outline="editorOutline"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
      <div class="keyboard-example reading-example" data-example-timed-list-reading>
        <MarkdownRenderer :value="timedPage" profile="rich" :plugins="plugins" ariaLabel="Ukázka časové odrážky při čtení" :class="theme" />
      </div>
    </SettingsSection>
    <SettingsSection title="Klávesy editoru"
      description="Seznam je data editoru (markdownKeyboardShortcuts v @atlas/markdown/core): stejné popisky jako v nabídkách a tooltipech. Přehled klávesových zkratek Atlasu ho ukazuje, když je kurzor v editoru; Ctrl+/ v editoru o něj požádá událostí markdown-shortcut-help.">
      <div class="keyboard-groups" :class="{ asked }">
        <section v-for="entry in groups" :key="entry.group" class="keyboard-group">
          <h3>{{ entry.group }}</h3>
          <dl>
            <template v-for="item in entry.entries" :key="item.keys.join('+')">
              <dt><kbd>{{ keys(item) }}</kbd></dt><dd>{{ item.label }}</dd>
            </template>
          </dl>
        </section>
        <section class="keyboard-group">
          <h3>Markdown na začátku řádku</h3>
          <dl>
            <template v-for="item in markdownTypingShortcuts" :key="item.typed">
              <dt><code>{{ item.typed }}</code></dt><dd>{{ item.label }}</dd>
            </template>
          </dl>
        </section>
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.keyboard-example { border: 1px solid var(--line); border-radius: var(--radius-md, 10px); min-height: 220px; }
.reading-example { margin-top: 12px; padding: 16px 16px 16px 48px; }
.keyboard-groups { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 16px 24px; border-radius: var(--radius-md, 10px);
  transition: box-shadow var(--atlas-motion-fast) var(--atlas-motion-ease); }
.keyboard-groups.asked { box-shadow: 0 0 0 2px var(--accent); }
.keyboard-group h3 { margin: 0 0 6px; font-size: 13px; font-weight: 600; }
.keyboard-group dl { display: grid; grid-template-columns: minmax(0, max-content) minmax(0, 1fr); gap: 4px 12px; margin: 0; font-size: 13px; }
.keyboard-group dt { overflow-wrap: anywhere; }
.keyboard-group dd { margin: 0; color: var(--text-muted, inherit); }
kbd, code { font-family: var(--font-mono, ui-monospace, monospace); font-size: 12px; }
</style>
