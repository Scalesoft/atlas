<script lang="ts">
import type { MarkdownRichValue } from "@atlas/markdown/core";
import placeholderLogo from "./assets/placeholder-logo.svg?no-inline";
import placeholderLogo2 from "./assets/placeholder-logo-2.svg?no-inline";
import placeholderPhoto from "./assets/placeholder-photo.svg?no-inline";

// Local sample data only: every chip of the page editor in one sentence, nothing leaves the gallery. One sample of each kind of entity
// the host manifest names (ChipExample.test.ts), so a new chip shows up here or fails the test. „/youtrack“ finds two sample issues.
export const sentence: MarkdownRichValue = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }, { id: "youtrack", version: 1 }, { id: "pages", version: 1 },
  { id: "atlas-requirements", version: 1 }, { id: "emoji", version: 1 }, { id: "math", version: 1 }, { id: "secrets", version: 4 }],
  document: { type: "doc", content: [
    { type: "paragraph", content: [
      { type: "text", text: "Přístup VPN pro " },
      { type: "contactInline", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", kind: "internal", internalUser: { id: "u1", name: "Jana Nováková" } }] } } },
      { type: "text", text: " řeší " },
      { type: "youtrackIssue", attrs: { id: "2-1235", key: "PX2-1235", title: "Přístup VPN", url: "https://youtrack.example/issue/PX2-1235", state: "Open" } },
      { type: "text", text: " podle " },
      { type: "pageReference", attrs: { id: "11111111-1111-4111-8111-111111111111", title: "Provozní příručka", url: "/#/pages/11111111-1111-4111-8111-111111111111" } },
      { type: "text", text: ", za zákazníka " },
      // A person with no account here (a directory or written contact): the same mention chip, the initials without the user's colour.
      { type: "contactInline", attrs: { data: { version: 1, contacts: [{ name: "Petr Dvořák", kind: "external", organization: "Example s.r.o." }] } } },
      { type: "text", text: ", za dodavatele " },
      // A name several people have, nobody picked yet: the unresolved state of the same mention.
      { type: "contactInline", attrs: { data: { version: 1, contacts: [{ name: "Jan Novák", unresolved: true }] } } },
      { type: "text", text: " " },
      { type: "emoji", attrs: { name: "smile" } },
      { type: "text", text: ", přihlásí se jako " },
      { type: "secretInlineReference", attrs: { secretId: "5b2f6a3c-1d4e-4f5a-8b6c-7d8e9f0a1b2c", field: "username" } },
      { type: "text", text: " a kapacitu spočítá " },
      { type: "mathInline", attrs: { data: { version: 1, expression: "2n+1" } } },
      { type: "text", text: "." },
    ] },
    // P15: a template's place for a person (contacts plugin contactPlace): the unresolved chip named by its role until someone is picked.
    { type: "paragraph", content: [
      { type: "text", text: "PM projektu: " },
      { type: "contactInline", attrs: { data: { version: 1, contacts: [{ name: "PM", role: "PM", unresolved: true }] } } },
    ] },
    { type: "requirement", attrs: { id: "6f1c1d3e-8a4b-4c55-9d1e-000000000901", key: "vpn", state: "waiting_for_input", party: "Jiří Sýkora", date: "2026-11-04" },
      content: [{ type: "paragraph", content: [{ type: "text", text: "Certifikát pro VPN" }] }] },
  ] } };

// Package G: neutral placeholders (geometric shapes), never a company's real logo; the owner uploads those to the contacts.
const mention = (contact: Record<string, string | Record<string, string>>) => ({ type: "contactInline", attrs: { data: { version: 1, contacts: [contact] } } });
const text = (value: string) => ({ type: "text", text: value });
const ids = (n: number) => `a1b2c3d4-0000-4000-8000-0000000000${50 + n}`;
export const logos: MarkdownRichValue = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }], document: { type: "doc", content: [
  { type: "paragraph", content: [
    text("Organizace s logem "), mention({ name: "Nemocnice Dolní Ves", objectId: ids(1), photo: placeholderLogo }),
    text(", bez loga "), mention({ name: "Example s.r.o.", objectId: ids(2) }),
    text(", osoba s fotkou "), mention({ name: "Petra Malá", organization: "Example s.r.o.", objectId: ids(3), photo: placeholderPhoto }),
    text(" a bez ní "), mention({ name: "Jana Nováková", kind: "internal", internalUser: { id: "u1", name: "Jana Nováková" }, objectId: ids(4) }), text("."),
  ] },
  { type: "paragraph", content: [
    text("Stejná jména: "), mention({ name: "Jan Novák", organization: "Modelsoft", objectId: ids(5), photo: placeholderLogo2 }),
    text(" a "), mention({ name: "Jan Novák", organization: "Nemocnice Dolní Ves", objectId: ids(6) }), text("."),
  ] },
  { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Nemocnice Dolní Ves", website: "https://nemocnice.example", objectId: ids(1), photo: placeholderLogo }] } } },
] } };
</script>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { MarkdownEditor, MarkdownRenderer } from "@atlas/markdown/vue";
import { createMarkdownChip, formatMarkdownShortcut, type MarkdownChipOptions, type MarkdownLexicon } from "@atlas/markdown/core";
import { youTrackIssueChip } from "@atlas/markdown/plugins/youtrack";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/midnight.css";
import { atlasContentPlugins } from "../../documentWidgets";
import { editorChrome, useEditorTheme } from "../../editor/editorSetup";
import PageVerification from "../PageVerification.vue";
import type { PageVerification as Verification, VerificationState } from "../../verification/pageVerification";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

const theme = useEditorTheme();
const issues = [{ id: "2-1235", key: "PX2-1235", title: "Přístup VPN", url: "https://youtrack.example/issue/PX2-1235", state: "Open" }, { id: "2-1240", key: "PX2-1240", title: "Certifikát VPN", url: "https://youtrack.example/issue/PX2-1240", state: "Fixed" }];
const plugins = atlasContentPlugins({ kind: "edit", youTrack: { search: async (query: string) => issues.filter(issue => `${issue.key} ${issue.title}`.toLowerCase().includes(query.toLowerCase())) } });
const readPlugins = atlasContentPlugins({ kind: "history" });
const page = ref(sentence);

// Chips from typed text (core/recognition, P15): a sample of the place's lexicon (GET /api/spaces/{id}/lexicon in Atlas), local only.
const handbook = { id: "c0ffee00-0000-4000-8000-000000000001", title: "Provozní příručka", url: "#/pages/c0ffee00-0000-4000-8000-000000000001" };
const lexicon: MarkdownLexicon = { version: "gallery", truncated: false, youTrackProjects: ["PX2"], pages: [{ id: handbook.id, title: handbook.title }], people: [
  { key: "object:a1b2c3d4-0000-4000-8000-000000000041", contactId: "a1b2c3d4-0000-4000-8000-000000000041", name: "Petr Dvořák", aliases: ["PeDv"], org: "Modelsoft", uses: 3 },
  { key: "object:a1b2c3d4-0000-4000-8000-000000000042", contactId: "a1b2c3d4-0000-4000-8000-000000000042", name: "Jana Nováková", aliases: [], org: "Modelsoft", uses: 5 },
  { key: "object:a1b2c3d4-0000-4000-8000-000000000043", contactId: "a1b2c3d4-0000-4000-8000-000000000043", name: "Jana Nováková", aliases: [], org: "Nemocnice Dolní Ves", uses: 1 }] };
const lexiconSource = { read: () => lexicon };
const typingPlugins = atlasContentPlugins({ kind: "edit", pages: { search: async () => [handbook], resolve: async () => handbook },
  youTrack: { search: async () => issues, resolve: async (key: string) => issues.find(issue => issue.key === key) ?? null } });
const typed = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, document: { type: "doc", content: [
  { type: "paragraph", content: [{ type: "text", text: "Postup je v Provozní příručka; Petru Dvořákovi to pošle PeDv." }] }, { type: "paragraph" }] } });

// The states, drawn by the same primitive (createMarkdownChip) the chips above use.
const states = ref<HTMLElement>();
const darkStates = ref<HTMLElement>();
function avatar(initials: string, color: string, ink = "#fff") {
  const element = document.createElement("span");
  element.className = "chip-example-avatar"; element.textContent = initials; element.style.background = color; element.style.color = ink;
  return element;
}
const samples = (): { caption: string; chip: MarkdownChipOptions; selected?: boolean }[] => [
  { caption: "Úkol (tag)", chip: youTrackIssueChip({ id: "2-1235", key: "PX2-1235", title: "Přístup VPN", url: "https://youtrack.example/issue/PX2-1235", state: "Open" }, true) },
  { caption: "Osoba (zmínka)", chip: { variant: "mention", label: "Jana Nováková", media: avatar("JN", "#2f7d8c"), editable: true } },
  { caption: "Osoba bez účtu (externí)", chip: { variant: "mention", label: "Petr Dvořák", media: avatar("PD", "var(--accent-soft, #e3f4f7)", "var(--accent-strong, #26758a)"), editable: true } },
  { caption: "Nerozhodnutý kontakt", chip: { variant: "mention", label: "Jana Nováková", media: avatar("JN", "#8a9aa0"), state: "unresolved", editable: true,
    detail: [{ text: "nejspíš Modelsoft", className: "chip-example-likely" }, { text: " · nebo Example" }], ariaLabel: "Jana Nováková, nerozhodnutý kontakt" } },
  { caption: "Vybraný", chip: { label: "Provozní příručka", icon: "file-text", editable: true }, selected: true },
  { caption: "Odkaz na ověřenou stránku (5.2)", chip: { tag: "a", href: "#/gallery/chips", label: "Provozní příručka", icon: "shield-check", className: "md-page-reference" } },
  { caption: "Odkaz na stránku s vypršeným ověřením", chip: { tag: "a", href: "#/gallery/chips", label: "Starý postup", icon: "shield-alert", className: "md-page-reference" } },
  { caption: "Ztlumený (hotovo, nedostupná stránka)", chip: { label: "do 4. 11.", state: "muted" } },
  { caption: "Fokus (Tab sem)", chip: { tag: "button", label: "Provozní příručka", icon: "file-text", editable: true } },
];
function draw(into: HTMLElement | undefined) {
  if (!into) return;
  into.replaceChildren(...samples().map(({ caption, chip, selected }) => {
    const figure = document.createElement("figure"), label = document.createElement("figcaption");
    const element = createMarkdownChip(document, chip).element;
    if (selected) element.dataset.chipSelected = "";
    label.textContent = caption;
    figure.append(element, label);
    return figure;
  }));
}
onMounted(() => { draw(states.value); draw(darkStates.value); });

// Ověřený obsah (5.2): the page's own status chip with its popover (PageVerification, the component the page uses), in every state, over a local stub.
const day = 86_400_000, at = (days: number) => new Date(Date.now() + days * day).toISOString();
const sample = (state: VerificationState, over: Partial<Verification> = {}): Verification => ({ state, stewardId: "u1", stewardName: "Jana Nováková", verifiedAt: at(-60), verifiedUntil: at(120), verifiedVersion: 14, changedSinceVerification: false, verifiedByName: "Petr Dvořák", intervalMonths: null, spaceIntervalMonths: 6, ...over });
const verifications = ref<{ caption: string; value: Verification; canEdit: boolean }[]>([
  { caption: "Ověřeno (editor)", value: sample("verified", { changedSinceVerification: true }), canEdit: true },
  { caption: "Brzy vyprší", value: sample("expiring", { verifiedUntil: at(9) }), canEdit: true },
  { caption: "Ověření vypršelo (čtenář: Nahlásit neaktuální)", value: sample("expired", { verifiedUntil: at(-3) }), canEdit: false },
  { caption: "Neověřeno, bez garanta", value: sample("unverified", { stewardId: null, stewardName: null, verifiedAt: null, verifiedUntil: null, verifiedVersion: null, verifiedByName: null }), canEdit: true },
]);
const verificationApi = (current: () => Verification) => ({
  read: async () => current(),
  change: async (_page: string, change: { verify?: true; until?: string; stewardId?: string; clearSteward?: true }) => sample("verified", { ...current(), ...(change.stewardId || change.clearSteward ? { stewardId: change.stewardId ?? null, stewardName: change.stewardId ? "Petra Malá" : null } : {}), ...(change.verify ? { verifiedAt: at(0), verifiedUntil: change.until ?? at(180), changedSinceVerification: false } : {}) }),
  report: async () => {},
});
const verificationPeople = async () => [{ id: "u2", name: "Petra Malá", username: "pmala" }, { id: "u1", name: "Jana Nováková", username: "jnovakova" }];
const keys = (...steps: string[]) => formatMarkdownShortcut(steps);
</script>

<template>
  <SettingsPage title="Čip v textu" description="Jeden prvek pro všechno, co stojí uprostřed věty a odkazuje jinam: osoba, úkol YouTrack, stránka, údaje požadavku, odkaz na heslo. Jádro editoru (createMarkdownChip a markdownChipSpec v @atlas/markdown/core, tokeny --md-chip-* v chip.css, který se načítá se styly editoru) ho kreslí stejně všude: dva vzhledy, převzaté z oblíbené zmínky kontaktu a čipu úkolu, stejné stavy, fokus, vybraný stav a klávesy.">
    <SettingsSection title="Všechny čipy v jedné větě"
      description="Věta níže je skutečný editor se skutečnými pluginy (styly kontaktu i stránky, jak je načte stránka), ne kresba. Osoba je vždy tatáž zmínka: s účtem v Atlasu na barvě uživatele, bez účtu (externí kontakt) s iniciálami nebo fotkou, nerozhodnutá čárkovaná s „?“. Kontakt (zmínka: jméno s avatarem, rámeček až při najetí), úkol YouTrack a stránka (tag: jemné pozadí s rámečkem), údaje požadavku za názvem (tag s barvou stavu); emoji, odkaz na heslo a vzorec stojí v textu jako jeden celek. Klik, Enter nebo F2 otevře úpravu čipu tam, kde je (P1, P9): kontakt jeho formulář nebo výběr osoby, úkol a stránka adresu odkazu (Enter v poli ji načte a použije), údaj požadavku jeho pole, heslo v textu (stránka Odkaz na heslo) jeho zobrazení; druhý klik úpravu zavře. Cíl odkazu otevře Ctrl+klik nebo „Otevřít“ v liště čipu, při čtení prostý klik. Šipkami vyberete čip v textu (vybraný má prstenec), Enter ho otevře stejně jako klik. Zmínka kontaktu je tlačítko, ale vzhled bloku (rámeček, větší písmo tlačítek modulu) na ni nesahá, ani v editoru, ani při čtení; nerozhodnutá má iniciály na tlumené barvě stavu. Místo ze šablony (P15, šablona Projekt) je táž nerozhodnutá zmínka pojmenovaná rolí („PM“): Tab z textu přejde na další prázdné místo, klik, Enter nebo F2 nabídne známé lidi (ty, které role jmenuje, tedy projektové manažery, první) a „Někdo jiný…“ s výběrem osoby; vybraná osoba si roli ponechá a místo je pak obyčejný čip kontaktu. Na dotyku (D5) má čip neviditelnou plochu 44 px do šířky, na výšku jen po svůj řádek, aby nebral klepnutí řádkům nad a pod ním.">
      <div class="chip-example-editor">
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázka čipů v textu" :plugins="plugins"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="Čipy vznikají z textu"
      description="Provázání samo od sebe (P15): jeden engine jádra editoru (core/recognition) čte, co místo zná podle jména (slovník prostoru: lidé, názvy stránek, projekty YouTrack), a nikdy nečeká na síť. Zkuste v prázdném řádku napsat „Petr Dvořák “: celé jednoznačné jméno se po mezeře stane kontaktem. „Jana Nováková “ mají dvě osoby: vznikne nerozhodnutý kontakt, nikdo se nevybere potichu. „PX2-1235 “ je klíč projektu z YouTracku: po ověření (bez čekání v psaní) z něj je čip úkolu; „UTF-8“, „ISO-9001“ ani „XY-12“ se nepřevedou nikdy. Backspace hned potom vrátí text, Ctrl+Z jen převod. Co je jen odhad, se samo nepřevede, jen se jemně podtrhne: název existující stránky („Provozní příručka“), skloňované jméno („Petru Dvořákovi“) a naučená zkratka („PeDv“, D4). Klik na podtržení nebo Ctrl+Mezerník s kurzorem v něm nabídne převod v nabídce „Co s tím jde dělat“ (u lidí „Je to …?“), Enter ho použije. „Převést na text“ (Ctrl+Shift+Mezerník) čip vrátí na text natrvalo pro tuto relaci; teprve když text upravíte, může se nabídnout znovu. Proč ne nová komponenta: čip je čip jádra, podtržení jedna třída jádra, nabídka je ta jedna nabídka akcí.">
      <div class="chip-example-editor" data-example-chip-typing>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="typed" profile="rich" ariaLabel="Ukázka čipů z napsaného textu" :plugins="typingPlugins" :lexicon="lexiconSource"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="Stavy"
      description="Rozhodnutý (běžný), nerozhodnutý (čárkovaný rámeček s „?“ a za jménem tlumeně nejpravděpodobnější kandidát a ostatní; volí se klikem nebo Enterem), vybraný (prstenec zvýraznění, také první Backspace u údaje požadavku), ztlumený (hotový požadavek, nedostupná stránka) a fokus (prstenec při Tabu). Barvy a velikosti jsou tokeny --md-chip-*, takže sedí ve světlém i tmavém motivu; pohyb jen přes --md-motion-* a s omezeným pohybem nic nepřechází.">
      <div ref="states" class="md-editor chip-example-states" data-example-chip-states />
      <div ref="darkStates" class="md-editor md-theme-midnight chip-example-states chip-example-dark" data-example-chip-dark />
    </SettingsSection>
    <SettingsSection title="Stav ověření stránky"
      description="Stránka s ověřováním (Nastavení prostoru → Obecné) má u titulku stavový čip, tentýž StatusBadge jako ostatní stavy stránky, ne nový prvek. Ověřeno a Brzy vyprší (Vyprší za 9 dní) jsou tiché, Neověřeno je neutrální prostý štít a jen Ověření vypršelo má tón upozornění (jedno mapování verificationMark pro čip, hledání i odkazy); nic neblokuje čtení ani nepřidává pruh nad text. Klik nebo Enter otevře malé okno: garant, kdo a kdy ověřil, Změněno po ověření s odkazem na porovnání verzí. Editor ověří (klávesa V), ověří do data nebo změní garanta (hledání osob, první výsledek předvybraný, Enter vybere); čtenář nahlásí stránku jako neaktuální s volitelnou poznámkou. Pohyb: po ověření čip jednou potvrdí nový stav (StatusBadge confirmed, klíčovaný časem ověření), přepnutí pohledů okna (hlavní, garant, datum) plynule mění výšku místo skoku; při omezeném pohybu jen krátké prolnutí. Níže je skutečná komponenta nad místním zástupným API; odkazy na stránky s ověřením mají před titulkem štít (viz Stavy).">
      <div class="chip-example-states chip-example-live" data-example-verification>
        <figure v-for="item in verifications" :key="item.caption">
          <PageVerification :page-id="'11111111-1111-4111-8111-111111111111'" :verification="item.value" :can-edit="item.canEdit" is-published :api="verificationApi(() => item.value)" :people="verificationPeople" @change="value => { item.value = value; }" />
          <figcaption>{{ item.caption }}</figcaption>
        </figure>
      </div>
    </SettingsSection>
    <SettingsSection title="Logo a fotka v čipu"
      description="Tatáž zmínka kontaktu (žádný nový druh čipu): firma má před jménem své logo, osoba svou fotku, obojí volitelné. Bez obrázku zůstává dnešní avatar: iniciály, u osoby s účtem na barvě uživatele. Obrázek nahraje uživatel u kontaktu (karta kontaktu v adresáři, na tabuli nebo v panelu kontaktu na stránce: „Logo nebo fotka“) nebo AI přes atlas_upsert_contact (photoBase64); Atlas ho uloží jako malý čtverec (64 px, nejvýš 64 KB) a čip ho ukáže jedním líně načteným obrázkem pevné velikosti, prohlížeč si ho drží natrvalo. Stejné obrázky má kontakt v kartě, strana požadavku („od …“) a seznam Na co čekáme. Dvě různé osoby téhož jména na jedné stránce rozliší za jménem jejich organizace (jinak funkce, oddělení nebo e-mail); odkaz vede vždy na kontakt podle objectId. Obrázky níže jsou neutrální zástupné tvary, ne loga firem.">
      <!-- @vue-generic {'rich'} --><MarkdownRenderer :value="logos" profile="rich" ariaLabel="Ukázka log a fotek v čipech kontaktů" :plugins="readPlugins" :class="[theme, 'atlas-document-editor']" data-example-chip-logos />
    </SettingsSection>
    <SettingsSection title="Jen ke čtení"
      description="Čtenář (veřejná stránka, historie) vidí tytéž čipy bez kláves v popisku: zmínka otevře kartu kontaktu, úkol a stránka odkaz.">
      <!-- @vue-generic {'rich'} --><MarkdownRenderer :value="sentence" profile="rich" ariaLabel="Ukázka čipů jen ke čtení" :plugins="readPlugins" :class="[theme, 'atlas-document-editor']" />
    </SettingsSection>
    <SettingsSection title="Kdy ho použít"
      description="Čip je jen pro malou věc uvnitř řádku textu, která má vlastní identitu jinde (osoba, úkol, stránka) nebo je údajem bloku (termín požadavku). Bloky zůstávají bloky: vizitka kontaktu, karta přílohy, graf nebo tabule se čipem nestávají. Nový druh čipu nevzniká vlastním CSS, ale voláním createMarkdownChip (uzel v editoru, widget) nebo markdownChipSpec (renderHTML) s vlastními třídami jen pro své části (klíč úkolu).">
      <dl class="chip-example-keys">
        <dt><kbd>Backspace</kbd> hned po vzniku</dt><dd>Jako smazání znaku, který čip udělal (mezera, dvojtečka): čip je zpět textem, kurzor na jeho konci. Ten znak napsaný znovu čip udělá znovu.</dd>
        <dt><kbd>{{ keys("Mod", "Shift", "Space") }}</kbd></dt><dd>Převést vybraný čip (nebo čip těsně před kurzorem) na jeho text; totéž „Převést na text“ v liště čipu. Zůstane jen viditelný text.</dd>
        <dt><kbd>{{ keys("Mod", "Space") }}</kbd> / <kbd>Alt+Enter</kbd> / <kbd>{{ keys("Mod", ".") }}</kbd> / <kbd>Shift+F10</kbd></dt><dd>Nabídka „Co s tím jde dělat“ u čipu nebo textu; stejně klávesa nabídky a pravé tlačítko (to čip nejdřív vybere). Shift + pravé tlačítko nechá nabídku prohlížeče.</dd>
        <dt><kbd>←</kbd> <kbd>→</kbd>, <kbd>Enter</kbd> / <kbd>F2</kbd></dt><dd>Vybrat čip v textu a otevřít jeho úpravu; <kbd>Esc</kbd> vrátí do textu. Ctrl+klik otevře cíl odkazu. Vybraný čip a <kbd>Backspace</kbd> ho smaže (údaj požadavku nejdřív vybere, druhý Backspace ho odebere).</dd>
        <dt><kbd>Ctrl+Z</kbd></dt><dd>Vrátí jen vznik čipu, ne psaní před ním.</dd>
      </dl>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.chip-example-editor { border: 1px solid var(--line); border-radius: var(--radius-md, 10px); }
.chip-example-states { display: flex; flex-wrap: wrap; gap: 16px 24px; padding: 16px; border-radius: var(--radius-md, 10px); background: var(--paper, #fff); color: var(--text); }
.chip-example-dark { margin-top: 12px; }
/* The live chips sit on the page's own surface (the states above carry the editor's paper), so they follow the dark theme. */
.chip-example-live { padding: 0; background: transparent; }
/* min-width 0 and a shrinkable column: a long chip ellipsizes in its cell at 375 px, as it does in a paragraph. */
.chip-example-states :deep(figure) { display: grid; grid-template-columns: minmax(0, auto); gap: 6px; justify-items: start; min-width: 0; margin: 0; }
.chip-example-states :deep(figcaption) { color: var(--text-dim); font-size: 12px; }
.chip-example-states :deep(.chip-example-avatar) { display: inline-flex; align-items: center; justify-content: center; border-radius: 50%; color: #fff; font: 700 8px/1 var(--font-mono, monospace); }
.chip-example-states :deep(.chip-example-likely) { color: var(--text); font-weight: 600; }
.chip-example-keys { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin: 0; font-size: 13px; }
.chip-example-keys dd { margin: 0; color: var(--text-dim); }
kbd { font-family: var(--font-mono, ui-monospace, monospace); font-size: 12px; }
</style>
