<script setup lang="ts">
import { computed, ref } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import type { MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import { createEditorToolsPlugin } from "@atlas/markdown/plugins/editor-tools";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import type { Editor } from "@tiptap/core";
import { createHeadingFoldPlugin } from "../../editor/headingFoldPlugin";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import type { ContactData } from "../../contacts/contactData";
import placeholderLogo from "./assets/placeholder-logo.svg?no-inline";
import { createDocumentWidgets } from "../../documentWidgets";
import { editorChrome, editorOutline, useEditorTheme } from "../../editor/editorSetup";
import DeploymentOverview from "../../projector/overview/DeploymentOverview.vue";
import type { RequirementBlock, RequirementOverview, WaitingList as WaitingListShape } from "../../projector/overview/overviewApi";
import WaitingList from "../../projector/overview/WaitingList.vue";
import type { CatalogDefinition } from "../../projector/projectorApi";
import CatalogPicker from "../../projector/CatalogPicker.vue";
import { catalogModules, planSelection } from "../../projector/catalogSelection";
import PartyInput from "../../projector/PartyInput.vue";
import { partySuggestions } from "../../projector/partySuggestions";
import type { PersonHit } from "../../contacts/contactSearch";
import { learnAlias } from "../../contacts/contactSources";
import { overviewKinds } from "../../overview/overviewKinds";
import { Switch } from "../ui/switch";
import { createRequirementsPlugin, openCatalogPanel, openSourcePanel, type ProjectorHost } from "../../projector/requirementsPlugin";
import { Button } from "../ui/button";
import CommentComposer from "../comments/CommentComposer.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

// Local sample data only: the catalog answers from this file (the panel resolves a pick on it), a new deployment is pretended.
// Nothing leaves the gallery.
const theme = useEditorTheme();
const lastNote = ref("");
const id = (n: number) => `6f1c1d3e-8a4b-4c55-9d1e-${String(n).padStart(12, "0")}`;
const p = (text: string) => ({ type: "paragraph", ...(text ? { content: [{ type: "text", text }] } : {}) });
// A requirement's history is text (plan 3.4 §3): list lines of its notes that start with a day and maybe a time, the newest first.
const history = (...lines: string[]) => ({ type: "bulletList", content: lines.map(line => ({ type: "listItem", content: [p(line)] })) });
// A catalog definition's body as the server's API delivers it (the block view of a catalog requirement); inserted, it becomes a heading.
const requirement = (n: number, title: string, attrs: Record<string, string>, ...rest: unknown[]) =>
  ({ type: "requirement", attrs: { id: id(n), direction: "internal", state: "planned", date: "", party: "", where: "", requires: "", ...attrs }, content: [p(title), ...rest] });
// The heading one level above a module on the catalog page: the picker groups modules under it, and a search finds it.
const categoryOf: Record<string, string> = { "Portál": "Jádro", "Identita občana": "Autentizace", "Mobilní aplikace": "Nástroje pro pacienta" };
// What a module is: the paragraph under its heading on the catalog page (O7), shown in the configurator's preview.
const summaryOf: Record<string, string> = { "Portál": "Základní instalace portálu na serveru nemocnice, dvě veřejné URL (pacient, zdravotník).",
  "Identita občana": "Přihlášení pacienta přes NIA (Identita občana).", "Mobilní aplikace": "Aplikace portálu v App Store a Google Play pod účtem nemocnice." };
const definition = (key: string, title: string, direction: CatalogDefinition["direction"], requires: string[] = [], bundle = "Portál", attrs: Record<string, string> = {}): CatalogDefinition =>
  ({ key, title, titleMarkdown: title, direction, requires, pageId: "catalog", bundle, category: categoryOf[bundle], product: "Portál pacienta", summary: summaryOf[bundle], contentJson: requirement(90, title, { key, direction, requires: requires.join(","), ...attrs }) as CatalogDefinition["contentJson"] });
// Three modules (sections of the catalog page): the portal's core, identity that needs it, and mobile apps.
const catalog = [definition("portal.vpn", "VPN přístup", "inbound"), definition("portal.monitoring", "Monitoring", "internal", ["portal.vpn"]),
  definition("portal.handover", "Předávací protokol", "outbound", ["portal.monitoring"]),
  definition("portal.nia.cert", "NIA — podpisový certifikát", "inbound", [], "Identita občana", { system: "NIA" }), definition("portal.nia", "Modul identity nasazen", "internal", ["portal.nia.cert", "portal.monitoring"], "Identita občana"),
  definition("portal.apps.apple", "Apple Developer účet", "inbound", [], "Mobilní aplikace"), definition("portal.apps", "Modul Mobilní aplikace nasazen", "internal", ["portal.apps.apple", "portal.vpn"], "Mobilní aplikace")];
// „Nasazení z katalogu“'s checklist on the same catalog: modules only, the plan of the selection live.
const modules = catalogModules(catalog);
const pickedModules = ref<string[]>([]);
const modulePlan = computed(() => planSelection(catalog, modules, pickedModules.value));
// „Moduly produktu“ on the same catalog: the tree beside the preview, as the panel shows it; the page has the portal's VPN already.
const productPicks = ref<string[]>([]);
const productPlan = computed(() => planSelection(catalog, modules, productPicks.value, ["portal.vpn"]));
const notice = ref("");
// The page's own flag, as the page DTO's isCatalog: the summary's menu sets it, the summary shows the catalog's look.
const catalogPage = ref(false);
// The gallery page's id is a page id (a UUID), as a section link's must be: a same-page dependency counts only with one.
const galleryPage = "6f1c1d3e-8a4b-4c55-9d1e-0000000000a0", otherPage = "6f1c1d3e-8a4b-4c55-9d1e-0000000000c0", janaContact = "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1";
const host: ProjectorHost = {
  spaceId: () => "gallery", pageId: () => galleryPage, parentId: () => null, pageTitle: () => "Nemocnice Horní Lhota — portál",
  titleOf: page => page === "catalog" ? "Katalog požadavků portálu" : undefined,
  open: page => { notice.value = `Otevřela by se stránka ${page}.`; },
  isCatalog: () => catalogPage.value, setCatalog: isCatalog => { catalogPage.value = isCatalog; },
  api: {
    // The list without the blocks, as the server's ?bodies=false; the blocks of the picked keys at the insertion.
    catalog: async () => ({ definitions: catalog.map(({ contentJson: _block, ...definition }) => definition), warnings: [] }),
    definitions: async (_spaceId, keys) => ({ definitions: catalog.filter(definition => keys.includes(definition.key)), warnings: [] }),
    setCatalogPage: async (pageId, isCatalog) => ({ pageId, isCatalog }),
    createDeployment: async (_pageId, request) => ({ id: "gallery-new-page", title: request.title }),
    // „Požadavek z nasazení“ and „Požadavky ze sady“: a set (a space template) and another deployment, both answering the sample catalog's blocks.
    presets: async () => [{ pageId: "gallery-preset", title: "Sada: Portál pacienta", requirements: catalog.length }],
    deployments: async () => [{ pageId: galleryPage, title: "Tato stránka" }, { pageId: "ndv", title: "Nemocnice Dolní Ves" }],
    pageDefinitions: async () => ({ definitions: catalog, warnings: [] }),
    // M4b: the other deployment's requirements a dependency names, what waits for one there.
    prerequisites: async refs => refs.map(reference => reference === `infra.vpn@${otherPage}`
      ? { reference, pageId: otherPage, key: "infra.vpn", readable: true, title: "VPN koncentrátor nemocnic", state: "in_progress", pageTitle: "Sdílená infrastruktura" }
      : { reference, pageId: reference.split("@")[1]!, key: reference.split("@")[0]!, readable: false }),
    dependents: async () => ({ key: "portal.core", rows: [{ pageId: otherPage, pageTitle: "Sdílená infrastruktura", requirement: { blockId: "x", key: "infra.monitoring", title: "Monitoring portálů", state: "planned" } }] }),
    cycle: async () => ({ path: null }),
    searchRequirements: async () => [
      { pageId: otherPage, pageTitle: "Sdílená infrastruktura", requirement: { blockId: "a", key: "infra.vpn", title: "VPN koncentrátor nemocnic", state: "in_progress" } },
      { pageId: otherPage, pageTitle: "Sdílená infrastruktura", requirement: { blockId: "b", key: "infra.dns", title: "DNS záznamy", state: "done" } }],
  },
  // A party that is a contact of the directory reads under the contact's name as it is now.
  // A party that is a contact with a logo (package G): a neutral placeholder here, the real logo comes from its contact.
  contact: async contactId => (contactId === janaContact ? { name: "Jana Nováková (Modelsoft)", photo: placeholderLogo } : undefined),
};
// The live editor of the first example, so the buttons below open the catalog's panels in it.
let editor: Editor | undefined;
const probe: MarkdownPlugin = { id: "requirements-example-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
const plugins = [createEditorToolsPlugin(), ...createDocumentWidgets({ kind: "edit" }).filter(plugin => plugin.id !== "atlas-requirements"),
  createRequirementsPlugin({ host, offered: () => true }), createHeadingAnchorsPlugin(), createHeadingFoldPlugin(() => galleryPage)];
const headingPlugins = [...plugins, probe];
function openFromDeployment() {
  if (!editor) return;
  editor.commands.setTextSelection(editor.state.doc.content.size - 1);
  openSourcePanel(editor, host, false);
}
function openCatalog() {
  if (!editor) return;
  editor.commands.setTextSelection(editor.state.doc.content.size - 1);
  openCatalogPanel(editor, host);
}

// „Přehled nasazení“ and „Na co čekáme“ on sample data: three deployments of one space, nothing asked of a server.
const cell = (blockId: string, key: string, title: string, fields: Partial<RequirementBlock> = {}): RequirementBlock =>
  ({ blockId, key, title, direction: "internal", state: "planned", date: "", party: "", where: "", requires: [], parentBlockId: null, position: 0,
    section: key === "portal.docs" ? ["Předání"] : ["Portál pacienta", "Základ"], ...fields });
const deployments: RequirementOverview = { keys: ["portal.docs", "portal.orders", "portal.sms", "portal.ssl", "portal.vpn"], pages: [
  { pageId: "nhl", title: "Nemocnice Horní Lhota", percent: 40, cells: [cell("a1", "portal.docs", "Předání dokumentace", { direction: "outbound" }),
    cell("a2", "portal.orders", "Objednávky", { state: "in_progress" }), cell("a3", "portal.sms", "SMS brána", { direction: "inbound", state: "waiting_for_input", date: "2026-10-12", party: "IT nemocnice" }),
    cell("a4", "portal.ssl", "SSL certifikát", { direction: "inbound", state: "done", date: "2026-09-10" }), cell("a5", "portal.vpn", "VPN přístup", { direction: "inbound", state: "done", date: "2026-09-02" })] },
  { pageId: "ndv", title: "Nemocnice Dolní Ves", percent: 25, cells: [cell("b1", "portal.orders", "Objednávky", { state: "blocked" }),
    cell("b2", "portal.sms", "SMS brána", { direction: "inbound" }), cell("b3", "portal.ssl", "SSL certifikát", { direction: "inbound", state: "done", date: "2026-10-01" }),
    cell("b4", "portal.vpn", "VPN přístup", { direction: "inbound", party: "IT Dolní Ves" }), cell("b5", "portal.docs", "Předání dokumentace", { direction: "outbound", state: "waiting_for_input", party: "Jana Nováková" })] },
  { pageId: "pbr", title: "Poliklinika Dolnice", percent: 100, cells: [cell("c1", "portal.ssl", "SSL certifikát", { direction: "inbound", state: "done", date: "2026-08-20" })] },
] };
const overviewSource = {
  overview: async () => deployments,
  catalog: async () => ({ definitions: deployments.keys.map(key => ({ key, title: deployments.pages[0]!.cells.find(item => item.key === key)!.title,
    bundle: key === "portal.vpn" ? "Síť" : "Portál", moduleKey: key === "portal.vpn" ? "heading-sit" : "heading-portal" })) }),
};
const waitingSource = {
  waiting: async (): Promise<WaitingListShape> => ({ parties: [
    { party: "IT nemocnice", items: [{ pageId: "nhl", pageTitle: "Nemocnice Horní Lhota", blockId: "a3", key: "portal.sms", title: "SMS brána", direction: "inbound", state: "waiting_for_input", date: "2026-10-12" }] },
    { party: "Jana Nováková", items: [{ pageId: "ndv", pageTitle: "Nemocnice Dolní Ves", blockId: "b5", key: "portal.docs", title: "Předání dokumentace", direction: "outbound", state: "waiting_for_input", date: "" }] },
  ] }),
  contacts: async (parties: string[]) => new Map(parties.filter(party => party === "Jana Nováková").map(party => [party, { name: party, organization: "Nemocnice Dolní Ves", photo: placeholderLogo } as ContactData])),
};
// The party field on its own, over sample people of the directory (Marek Musil also „Mára“, learned before): „MMu“ finds him by
// initials; „Použít“ applies the party and runs Atlas's own learning (partySuggestions.ts `learnParty` → `learnAlias`), whose
// directory change is shown here instead of sent.
const partyPeople: PersonHit[] = [
  { key: "obj:mm", objectId: "6f1c1d3e-8a4b-4c55-9d1e-0000000000d1", contact: { name: "Marek Musil", title: "Vývojář" }, source: "directory", origin: "Adresář", aliases: ["Mára"] },
  { key: "obj:ma", objectId: "6f1c1d3e-8a4b-4c55-9d1e-0000000000d2", contact: { name: "Michal Macek", title: "Konzultant" }, source: "directory", origin: "Adresář" }];
// `cached`: the names known already are the first rows, at once (P17); the answer only refines them (partySource.ts).
const knownParties = (text: string) => partySuggestions(text, { page: ["IT nemocnice"], space: ["Správa ICT"], people: partyPeople });
const suggestParty = Object.assign(async (text: string) => knownParties(text), { cached: knownParties });
const party = ref(""), partyContact = ref(""), partyApplied = ref("nic"), partyLearned = ref("nic");
const learnPartyAlias = (hit: Pick<PersonHit, "contact" | "objectId">, query: string) => {
  partyLearned.value = `nic („${query.trim()}“ najde jméno samo)`;
  learnAlias(hit, query, { change: async (_id, payload) => { partyLearned.value = `„${String(payload.learnAlias)}“ → ${hit.contact.name}`; return { version: 1, duplicate: false }; } });
};
function applyParty() { partyApplied.value = party.value.trim() || "nic"; partyLearned.value = partyContact.value ? partyLearned.value : "nic"; }

// The kinds of the page overview registered in this build (the requirements one by requirementsPlugin.ts, imported above).
const kinds = overviewKinds();

// „Požadavek jako nadpis“ (model 2026-11-03, shape 2026-11-04): requirement headings of the real plugin (requirementHeading.ts,
// requirementHeadingView.ts). A section link is a page reference with ?heading= (this page, or another one: the host's prerequisites
// answer its title); the party is an attribute of the heading (shown in its kind chip), the facts line holds the deadline alone.
type DepTarget = [heading: string, title: string, page?: string];
const depLine = (...targets: DepTarget[]) => ({ type: "paragraph", attrs: { dependencies: true }, content: targets.flatMap(([target, title, pageId = galleryPage], i) =>
  [...(i ? [{ type: "text", text: ", " }] : []), { type: "pageReference", attrs: { id: pageId, title, url: `#/pages/${pageId}?heading=${target}` } }]) });
const headingRequirement = (headingId: string, title: string, attrs: Record<string, string>, deps: DepTarget[], ...body: unknown[]) =>
  [{ type: "heading", attrs: { level: 3, headingId, state: "planned", ...attrs }, content: [{ type: "text", text: title }] }, ...(deps.length ? [depLine(...deps)] : []), ...body];
const headingContent = () => [
  { type: "heading", attrs: { level: 2, headingId: "identita" }, content: [{ type: "text", text: "Identita občana" }] },
  p("Přihlášení pacienta přes NIA. Požadavky níže jsou nadpisy se stavem; Přehled je sečte."),
  ...headingRequirement("nia-cert", "Certifikát NIA", { state: "waiting_for_input", direction: "inbound", system: "NIA", party: "IT nemocnice" }, [], p("do 21. 7. 2026"), p("Kde uloženo: Keepass, složka NIA."),
    history("12. 7. 2026 — Vyzváno: IT nemocnice", "2. 7. 2026 — Žádost odeslána")),
  ...headingRequirement("vpn", "VPN přístup", { state: "done", direction: "inbound", party: "IT nemocnice" }, [], p("Přístup funguje od 29. 9. 2026, účet je v Keepass.")),
  ...headingRequirement("uzis-test", "Testovací prostředí ÚZIS", { state: "blocked", direction: "inbound", party: "ÚZIS" }, [],
    p("Žádost o testovací prostředí leží bez odpovědi; bez něj nejde napojení ověřit.")),
  // Blocked without a reason note: „Kde uloženo: …“ (a converted page's line) is no reason, the heading shows none.
  ...headingRequirement("sms", "SMS brána", { state: "blocked", direction: "inbound", party: "IT nemocnice" }, [], p("Kde uloženo: Keepass, složka SMS.")),
  ...headingRequirement("uzis", "Napojení na ÚZIS", { state: "in_progress", system: "ÚZIS", party: "Jana Nováková" }, [["nia-cert", "Certifikát NIA"], ["vpn", "VPN přístup"], ["uzis-test", "Testovací prostředí ÚZIS"],
    ["infra.vpn", "VPN koncentrátor", otherPage]], p("do 15. 11. 2026"),
    p("Testovací přístup je hotový, ostrý čeká na certifikát."), history("28. 10. 2026 — Test prošel")),
  ...headingRequirement("handover", "Předávací protokol", { direction: "outbound", party: "Nemocnice Horní Lhota" }, [["uzis", "Napojení na ÚZIS"]], p("Podepíše PM po akceptaci.")),
  { type: "heading", attrs: { level: 2, headingId: "provoz" }, content: [{ type: "text", text: "Provoz" }] },
  p("Běžný text stránky pod obyčejným nadpisem."),
];
const pageOf = (content: unknown[]) => ({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }, { id: "pages", version: 1 },
  { id: "contacts", version: 2 }], document: { type: "doc", content } }) as MarkdownRichValue;
const headingPage = ref(pageOf(headingContent()));
const headingEditable = ref(true);
// The deployment summary over the same requirements: the overview block at the top, and a page without it (its automatic overview).
const page = ref(pageOf([p("Nemocnice Horní Lhota · smlouva 2026-042 · spuštění 1. 12. 2026"), { type: "overview", attrs: { id: id(1), kind: "requirements" } }, ...headingContent()]));
const pageWithoutSummary = ref(pageOf(headingContent()));

</script>

<template>
  <SettingsPage title="Požadavky ve stránce" description="Modul Projector (src/projector): požadavky jako nadpisy se stavem a souhrn nasazení, živě v editoru stránky s ukázkovými daty.">
    <SettingsSection title="Požadavek jako nadpis"
      description="Atlas 3.6 (model 2026-11-03, tvar 2026-11-04): požadavek je nadpis se stavem, žádná karta ani vlastní blok. Řádek nadpisu vypadá jako řádek seznamu požadavků z 3.5.2: kulaté zaškrtávátko (zaškrtnuto = hotovo, plné se značkou; odškrtnutí vrátí předchozí stav, jinak neřešeno; Mezerník na něm přepne; zaškrtnutí udělá radost: zelená zaplní kroužek, rozběhne se kolem něj jemná záře a značka se napíše a lehce pružně usadí, 320 ms na tokenech motion.css (vyzkoušejte se „Zpomalit 3×“), odškrtnutí zelenou jen tiše vypustí, s omezenými animacemi se stav změní hned; zaškrtávátko ani název se přitom nepohnou),název písmem nadpisu své úrovně (jako každý nadpis), za ním tiché štítky editoru (druh se stranou „↓ od …“, „↑ pro …“, u úkolu lidé; služba; „čeká na blokované: …“ a „čeká na N prerekvizity“; u blokovaného „Blokuje: …“ s nejnovějším záznamem „Blokováno: …“ z historie (zapíše ho akce B), jinak s první poznámkou — řádky převodu jako „Kde uloženo: …“ důvodem nejsou, SMS brána je blokovaná bez důvodu), na konci řádku kulatý stav a hned za ním šipka sbalení (sbalí vše do dalšího nadpisu stejné nebo vyšší úrovně, Alt+↑ / Alt+↓; u každého nadpisu na konci řádku). Hotový i netýká se jsou ztlumené, ne přeškrtnuté. Alt+F10 a Tab jdou v pořadí řádku: zaškrtávátko, štítky, stav, šipka, další Tab nadpis opustí. Na dotyku má zaškrtávátko, štítek i šipka plochu 44 px, řádek se nehne. Sekce rozpracovaného, čekajícího a blokovaného požadavku je karta podbarvená stavem (blokovaný i s tenkým červeným okrajem); sbalená je to jednořádková karta se stejnými štítky, takže stupeň S ukáže, co chybí. Podbarvení je jen pod textem: kurzor, výběr a poznámky pod nadpisem zůstávají obyčejným textem. Obyčejný nadpis vedle zůstává tučný bez karty. Klik nebo Enter na čipu otevře výběr (stav i vlastní slovo, „Čeká na vstup“ se zeptá na koho, „Hotovo“ s chybějícími prerekvizitami se zeptá; druh; služba), „čeká na N“ seznam toho, na co čeká, s odkazem. Na čipu stavu V, U, H, B zapíšou akce PM do historie sekce. Ctrl+. v nadpisu nabídne totéž a navíc Termín…, Čekáme na…, + závislost a ID… (přejmenování přepíše závislosti a odkazy ?heading= na stránce, staré ID zůstane jako alias). Pod nadpisem obyčejný text: řádek závislostí (odstavec s odkazy na sekce, popisek „Závislosti:“ kreslí CSS, vidět je vždy; bez odkazů řádek sám zmizí, ne však s kurzorem v něm; odkaz ukazuje živý název cílového nadpisu — z jiné stránky, jak ho přečte server, do té doby uložený název — a klik při čtení, Ctrl+klik nebo „Otevřít“ při psaní skočí na nadpis, na jiné stránce ji otevře u nadpisu), řádek údajů (jen „do 15. 11. 2026“, strana je ve štítku druhu), poznámky, historie. „čeká na N“ i dotaz u „Hotovo“ počítají i závislosti na jiných stránkách (nečitelné nebo zatím neznámé ne); „+ závislost“ po napsání nabídne i požadavky jiných stránek prostoru. Kopie nadpisů (vložení, upuštěná kopie, Alt+Shift+↓) dostanou nová ID bez aliasů a odkazy mezi zkopírovanými nadpisy se přepíšou na ně, odkazy jinam zůstanou; přesun ID nechá. ID smí mít tečku (portal.vpn). Enter na konci nadpisu dá odstavec až za řádek závislostí a údajů, uprostřed z druhé půlky udělá obyčejný nadpis; „Změnit na odstavec“ stav zahodí. /Požadavek vloží požadavek o úroveň pod nadpis nad kurzorem (vedle požadavku na jeho úroveň). Bez práva psát (přepínač) zůstávají stejná místa: zaškrtávátko je nečinné a není zastávkou Tabu, čipy druhu, služby a stavu otevřou kartu jen ke čtení, „čeká na N“ a odkazy fungují. Proč ne nová komponenta: je to nadpis editoru s dekoracemi a čipy editoru (createMarkdownChip); zaškrtávátko je zaškrtávátko seznamu úkolů, jen kulaté (prostý seznam úkolů zůstává hranatý). Starý blok požadavku zůstává jen uzlem schématu (uložené dokumenty a historie se načtou); server stránku v jeho tvaru při otevření převede na nadpisy.">
      <label class="requirements-example-switch"><Switch v-model="headingEditable" data-example-heading-editable />Upravovat</label>
      <div class="requirements-example" data-example-section>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="headingPage" profile="rich" ariaLabel="Požadavek jako nadpis" :plugins="headingPlugins" :readonly="!headingEditable"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
    <SettingsSection title="Souhrn nasazení"
      description="Souhrn nasazení je přehled stránky druhu requirements (blok overview) nad jejími požadavky: procento a lišta podle stavů, oddíly podle cesty nadpisů s připraveností, co blokuje, na koho čekáme a nedodané vstupy; počítá se z nadpisů stejnými pravidly jako server, nic neukládá. Stránka s požadavky bez bloku souhrnu ukáže tentýž přehled sama nahoře; „Připnout souhrn do stránky“ z něj udělá blok a automatický přehled zmizí. Nikdy nejsou dva. Je to tatáž komponenta RequirementSummaryCard, ne další přehled.">
      <div class="requirements-example" data-example-requirements>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázková stránka nasazení" :plugins="plugins" :outline="editorOutline"
          v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
      <div class="requirements-example" data-example-requirements-auto>
        <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="pageWithoutSummary" profile="rich" ariaLabel="Ukázková stránka nasazení bez souhrnu" :plugins="plugins"
          :outline="editorOutline" v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
      <p v-if="notice" class="st-muted" role="status">{{ notice }}</p>
    </SettingsSection>
    <SettingsSection title="Přehled stránky (blok overview)"
      description="Souhrn nasazení v ukázce výše je jeden druh obecného přehledu stránky (plán 3.4 R6): blok overview s atributem kind, který sečte komponenty svého druhu na stránce, přepíná úroveň jejich zobrazení (volba čtenáře v prohlížeči, na kořeni editoru jako data-overview-druh) a nabízí hromadné akce jako tlačítka karty. Vkládá se lomítkem „Přehled: …“ (seznam registrovaných druhů, první předvybraný, Enter) nebo zkratkou „Souhrn nasazení“; přehled jde na stránku jednou za druh, další vložení jen přiveze ten, který tam je. Kdy ho použít: chce-li čtenář vidět a ovládat všechny komponenty jednoho druhu na stránce najednou. Kdy ne: souhrn napříč stránkami je Přehled nasazení prostoru, jedna komponenta se ovládá sama. Nový druh = jedno volání registerOverviewKind (src/overview/overviewKinds.ts, popis polí u definice) při načtení modulu, jako ho volá requirementsPlugin.ts: kind, label, collect (komponenty druhu na stránce), card (Vue karta s editor a host) a volitelně text (řádek Markdownu), levels s animate a actions; žádný nový uzel ani migrace. Neznámý druh ukáže tichý zástupný text. Starý blok requirementSummary z Atlasu 3.3 se čte jako přehled druhu requirements.">
      <p class="st-muted" data-example-overview-kinds>Registrované druhy: {{ kinds.map(kind => `${kind.kind} (${kind.label}${kind.levels ? ", úrovně " + kind.levels.map(level => level.label).join(" / ") : ""})`).join(", ") }}</p>
    </SettingsSection>
    <SettingsSection title="Chybějící prerekvizity a Tabule nasazení"
      description="Plán 3.5 D, v ukázce výše („Registrace v NIA“ závisí na Monitoringu, který na stránce není). Závislost na požadavku, který na stránce chybí, hlásí karta přehledu seznamem „Chybějící prerekvizity“ (klíč a kolik bloků ho potřebuje, klik sjede na první z nich) a tlačítko „Doplnit prerekvizity“ v patě karty vloží chybějící bloky z katalogu i s tím, co samy potřebují, každý pod nadpis, který má v katalogu (jeho modul), jinak za první blok, který ho potřebuje. Co katalog nezná, jen ohlásí. MCP: projector_update_requirement(s), projector_create_requirement a projector_insert_requirements vracejí v warnings tytéž chybějící cíle. Tabule nasazení: položka „Tabule nasazení“ v menu stránky (skupina Nasazení, i při čtení; také v příkazové paletě) i stejnojmenné tlačítko v patě karty přehledu: editor stránky tabuli stránky poskládá (doplní, co na ní chybí, nebo ji založí) a otevře, čtenář ji jen otevře a položku vidí, až tabule je. Je to tabule stránky (účel projector.board): do stromu vlevo nepřibude, u stránky je jen tichá ikonka (Ctrl+Enter na řádku); čte ji každý, kdo smí číst stránku, upravuje ten, kdo smí stránku upravovat. Stránka, které 3.5.0 poskládala tabuli pod sebe do stromu, otevírá dál tu. MCP: projector_compose_board. Proč ne nový přehled nebo nový blok: je to pohled na tytéž atributy požadavků (requires) a ta samá tabule jako ve 3.4." />
    <SettingsSection title="Údaje psaním v názvu požadavku"
      description="Plán 3.1 §2.3a, v ukázce „Požadavek jako nadpis“. V názvu požadavku „čekáme na “ (také „čeká na “, „waiting “) otevře výběr osoby se stejnými návrhy jako pole strany, první je předvybraný, Enter nebo Tab ho vezme, Esc nechá text; vybraná osoba se zapíše jako strana nadpisu (ukáže ji štítek druhu) a stav bude „čeká na vstup“. „do pátku “, „do 15. 11. “, „do zítřka “, „do konce měsíce “ se hned změní v termín řádku údajů „do …“ a oznámení s „Zpět“ to řekne („do 3.1“ bez mezery ani tečky na konci je verze, ne datum); Ctrl+Z vrátí napsaný text. Proč ne nový formulář: zapisuje se do atributů nadpisu a obyčejného textu řádku údajů, výběry jsou návrhy editoru (createSuggestionList)." />
    <SettingsSection title="Katalog a vkládání"
      description="„/Moduly produktu“ a „/Požadavek z nasazení“ (tlačítka níže vkládají do ukázky „Požadavek jako nadpis“): RequirementCatalogPanel je CatalogPicker nad katalogem prostoru jako strom (viz „Moduly produktu“ níže). Psaní filtruje a předvybere první řádek, Mezerník zaškrtne, Ctrl+A zaškrtne vše zobrazené, Enter vloží, Ctrl+Enter vloží a panel nechá otevřený; první Esc smaže hledání, druhý panel zavře. Vložený požadavek je nadpis o úroveň pod nadpisem nad ním (celý modul pod svým nadpisem), s klíčem katalogu jako ID, stavem neřešeno a řádkem závislostí; na stránce s nadpisy produktu › kategorie › modulu jde pod ně a chybějící se doplní.">
      <div class="requirements-example-actions" data-example-requirement-popovers>
        <Button variant="outline" type="button" data-example-open-catalog @click="openCatalog">Moduly produktu</Button>
        <Button variant="outline" type="button" data-example-open-source @click="openFromDeployment">Požadavek z nasazení</Button>
      </div>
    </SettingsSection>
    <SettingsSection title="Strana jako kontakt, kdo upravil a návaznosti napříč stránkami"
      description="Plán 3.1 M4b. Strana („od“, „pro“ ve štítku druhu požadavku, „čekáme na“ v názvu) může být osoba z adresáře: pole strany (PartyInput) nabízí lidi z kontaktů s „z adresáře“, výběr uloží i id kontaktu do atributu nadpisu; přepsání textem kontakt odpojí, volný text zůstává dovolený. Pole najde lidi i podle iniciál („MMu“) a naučených zkratek („Mára“); osoba z adresáře vybraná po napsání něčeho, co není začátek jména ani jiného pole kontaktu, tu zkratku kontakt naučí, až se strana použije („Použít“, Enter, výběr v „čekáme na“). Vyzkoušejte v poli: „MMu“, Enter, „Použít“. Závislost může být požadavek jiného nasazení: „+ závislost“ po napsání nabídne i požadavky ostatních stránek prostoru a „čeká na N“ je počítá. Proč ne nový výběr osoby: PartyInput už kontakty nabízí, nese jen jejich id.">
      <form class="md-context-fields requirements-example-party" data-example-party @submit.prevent="applyParty">
        <label class="md-context-field md-context-field-wide">Na koho čekáme
          <PartyInput v-model="party" v-model:contact-id="partyContact" :suggest="suggestParty" :learn="learnPartyAlias" placeholder="Zkuste „MMu“ nebo „Mára“" />
        </label>
        <Button type="submit" data-example-party-apply>Použít</Button>
      </form>
      <p class="st-muted" data-example-party-learned aria-live="polite">Strana: {{ partyApplied }} · adresář by se naučil: {{ partyLearned }}</p>
    </SettingsSection>
    <SettingsSection title="Poznámky požadavku"
      description="Plán 3.5.1 B9. Poznámka požadavku v detailu je rychlá: CommentComposer (Enter přidá, Shift+Enter nový řádek, jako všechna pole komentářů) s `dayChip` (čip „dnes ▾“ vybere jiný den, po přidání se den vrátí na dnešek). Pole se píše v editoru stránky, takže B, I a odrážky platí. Nová komponenta to není: je to opt-in volba existujícího pole komentáře.">
      <div class="requirements-example-note" data-example-note>
        <CommentComposer day-chip placeholder="Napište poznámku… Enter přidá, Shift+Enter nový řádek" submit-label="Přidat" hint="Enter přidá" aria-label="Nová poznámka"
          @submit="(markdown, day) => { lastNote = `${day}: ${markdown}`; }" />
      </div>
      <p class="st-muted" data-example-note-last aria-live="polite">Poslední poznámka: {{ lastNote || "žádná" }}</p>
    </SettingsSection>
    <SettingsSection title="Rychlé vkládání: z jiného nasazení a ze sady"
      description="„/Požadavek z nasazení“ (tlačítko výše) a „/Požadavky ze sady“: RequirementSourcePanel nejdřív nabídne stránku, odkud brát — sady (šablony prostoru s bloky požadavků, ikona šablony) a ostatní nasazení prostoru — v SearchableChoiceList: psaní filtruje, první shoda je předvybraná a Enter ji vezme (i když se psalo hned za lomítkem). Pak tatáž RequirementCatalogPanel nad bloky a moduly vybrané stránky: zaškrtnout Mezerníkem, vidět, co přibude a proč, Enter vloží i s tím, na čem vybrané závisí, a každý vložený blok dostane nové id a vynulovaný stav, termín, stranu a místo (jako kopie nasazení); klíč, směr a závislosti zůstanou. „← Jiné nasazení“ se vrátí k výběru stránky. Sada je jen šablona prostoru: v novém nasazení (PageCreationFlow) se šablona s požadavky ukáže jako „Sada · N požadavků“ a server požadavky stránky ze šablony vynuluje. Žádné nové úložiště. Proč ne další výběr stránky: seznam je SearchableChoiceList, bloky CatalogPicker; nové je jen spojení dvou kroků." />
    <SettingsSection title="Výběr modulů: Nové nasazení"
      description="CatalogPicker (projector/CatalogPicker.vue) jen s moduly, jak ho má jedna obrazovka „Nasazení z katalogu“ (PageCreationFlow; v nabídce stránky „Nové nasazení…“): název předvyplněný ze zákazníka (stránka, pod kterou nasazení vzniká; psaní ho přepíše), Enter přejde k modulům, umístění první shodou a Enterem, moduly Mezerníkem, souhrn živě, Enter stránku vytvoří — souhrn nahoře, nadpis za každý modul, bloky v pořadí závislostí — a otevře ji v úpravách s kurzorem v prvním bloku. Moduly stojí pod nadpisy kategorií („Jádro“, „Autentizace“, „Nástroje pro pacienta“: nadpis nad nadpisem modulu na stránce katalogu, bez něj „Jádro“) v pořadí katalogu; nadpis není řádek, šipky ho přeskočí a první řádek zůstává předvybraný. Hledání najde i název kategorie („jádro“, „autent“) a ukáže ji celou i s moduly. Modul se hledá i podle svých požadavků. První Esc v hledání smaže napsané, druhý panel zavře; Enter v nedotčeném prázdném hledání nic nevloží, dokud na řádek nepřejdou šipky, a čekající načtení katalogu se dočká. Proč nová komponenta: createSuggestionList editoru vybírá jeden řádek, BoardPickList zaškrtává několik, ale Enter u něj zaškrtává a hledá na serveru, SearchableChoiceList je radiogroup. Pro výběr jedné stránky nebo osoby zůstávají ony; CatalogPicker je jen nad katalogem požadavků.">
      <div class="requirements-example-picker" data-example-catalog-picker>
        <p class="st-note">Šipky označují aktivní řádek souvislým vnitřním obrysem; zaškrtnutí nezávisle ukazuje vybrané moduly.</p>
        <CatalogPicker v-model:selected="pickedModules" :definitions="catalog" :modules="modules" :plan="modulePlan" modules-only label="Moduly z katalogu" :autofocus="false"
          @submit="notice = `Vytvořilo by se nasazení: ${'items' in modulePlan ? modulePlan.items.length : 0} požadavků.`" />
      </div>
    </SettingsSection>
    <SettingsSection title="Moduly produktu (konfigurátor)"
      description="Plán 3.5 C, jediná položka katalogu v editoru („/Moduly produktu“ i nabídka stránky › Nasazení; panel „Moduly produktu“, 920 px). Tentýž CatalogPicker jako strom (tree): vlevo produkt › kategorie › modul (nadpisy 1., 2. a 3. úrovně katalogové stránky), hledání najde i jednotlivé požadavky a ukáže je pod jejich modulem (zkuste „apple“); vpravo náhled zvýrazněného řádku — kde modul stojí, co je (odstavec pod nadpisem v katalogu), požadavek po požadavku co přibude a co už stránka má, a co se dotáhne z jiných modulů a kvůli kterému požadavku. Řádek produktu i kategorie zaškrtne všechny své moduly (i ty, které hledání skrylo) a řekne, kolik požadavků to je; částečný výběr ukáže pomlčkou. Klávesnice: psaní filtruje, nejlepší shoda je předvybraná, Mezerník zaškrtne, Ctrl+A vše zobrazené, Enter vloží, Ctrl+Enter vloží a pokračuje, Esc smaže hledání a pak zavře. Vložení jde pod nadpisy stránky: na konec oddílu produktu › kategorie › modulu, jak ho stránka zakázky má, chybějící nadpisy se doplní; stránka bez nich dostane výběr ke kurzoru. Už vložené se nezdvojí. Totéž umí MCP (projector_insert_requirements bez position). Kdy jinou: Nové nasazení, když stránka ještě neexistuje; Požadavek z nasazení, když se bere z jiné zakázky. Proč ne nová komponenta: výběr, plán závislostí i klávesy jsou CatalogPickeru; nový je jen dvoupanelový režim a umístění podle nadpisů.">
      <div class="requirements-example-tree" data-example-product-picker>
        <CatalogPicker v-model:selected="productPicks" :definitions="catalog" :modules="modules" :plan="productPicks.length ? productPlan : undefined" :present="new Set(['portal.vpn'])"
          tree label="Moduly produktu" :autofocus="false" @submit="notice = `Vložilo by se: ${'items' in productPlan ? productPlan.items.length : 0} požadavků pod nadpisy produktu.`" />
      </div>
    </SettingsSection>
    <SettingsSection title="Přehled nasazení"
      description="DeploymentOverview: stránky nasazení prostoru × klíče požadavků. Jedna odpověď serveru slouží všem třem filtrům; filtr skryje stránku, které v něm nic nezbývá. Sloupce se seskupí podle cesty oddílu bloku (jinak podle balíčku katalogu), názvy se čtou vodorovně; dva sloupce stejného názvu ve skupině řeknou klíč. U stránky stojí, co lze nasadit a co blokuje, podle téhož pravidla jako přehled stránky. Buňka je jeden odkaz na blok (ikona stavu, datum nebo dny čekání, šipka směru). Pro souhrn jedné stránky volte blok Souhrn nasazení; přehled je napříč nasazeními prostoru. „Produkty“ ukáže tutéž odpověď jako nasazení × produkty: produkt je modul katalogu (nadpis katalogové stránky) se stabilním klíčem — kotvou nadpisu, která přežije přejmenování; žádná nová entita (P1). Buňka řekne, zda nasazení produkt má celý a hotový (✓), celý (má), zčásti (3/5), nebo vůbec (–), a vede na jeho první požadavek na stránce. Šipky chodí po buňkách jako v matici klíčů.">
      <DeploymentOverview space-id="gallery" :api="overviewSource" data-example-overview />
    </SettingsSection>
    <SettingsSection title="Na co čekáme"
      description="WaitingList: požadavky prostoru ve stavu „čeká na vstup“ podle strany, na kterou čekáme, nejdéle čekající nahoře; strana, která je kontaktem adresáře, má iniciály a organizaci. Řádek „Čekáme na“ v souhrnu ukazuje totéž pro jednu stránku.">
      <WaitingList space-id="gallery" :api="waitingSource" data-example-waiting />
    </SettingsSection>
    <SettingsSection title="Proč ne seznam úkolů (taskList)"
      description="Úkol zná jen zaškrtnuto. Požadavek nese stav, směr, datum, stranu, místo, klíč a závislosti. Z nich žije souhrn, „čeká na“, katalog i přehled nasazení. Úkol volte pro seznam v textu, požadavek pro položku nasazení, na kterou se čeká nebo kterou vykazujete." />
  </SettingsPage>
</template>

<style scoped>
.requirements-example { min-width: 0; padding: 8px 0; }
.requirements-example-switch { display: inline-flex; align-items: center; gap: .5rem; margin-bottom: .75rem; }
.requirements-example-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.requirements-example-picker { max-width: 520px; }
.requirements-example-tree { max-width: 920px; }
.requirements-example-party { display: flex; flex-wrap: wrap; align-items: end; gap: 8px; max-width: 520px; margin-top: 12px; }
.requirements-example-party .md-context-field { flex: 1 1 240px; }
</style>
