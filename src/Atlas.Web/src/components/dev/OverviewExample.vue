<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { MarkdownEditor, MarkdownRenderer } from "@atlas/markdown/vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import "@atlas/markdown/styles/base.css";
import type { YouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import { atlasContentPlugins, createDocumentWidgets } from "../../documentWidgets";
import { editorChrome, useEditorTheme } from "../../editor/editorSetup";
import { createOverviewReading } from "../../overview/overviewReading";
import { overviewKinds, watchOverviewLevels, type OverviewBoardItem } from "../../overview/overviewKinds";
import OverviewReadCard from "../../overview/OverviewReadCard.vue";
import type { ProjectorHost } from "../../projector/requirementsPlugin";
import SettingsSection from "../settings/SettingsSection.vue";

// Local sample data only: a fictional deployment page and a board holding the same kinds of components. Nothing leaves the gallery.
const theme = useEditorTheme();
const id = (n: number) => `6f1c1d3e-8a4b-4c55-9d1e-${String(n).padStart(12, "0")}`;
const p = (...content: unknown[]) => ({ type: "paragraph", content });
const text = (value: string) => ({ type: "text", text: value });
const requirement = (n: number, title: string, attrs: Record<string, string>) =>
  ({ type: "requirement", attrs: { id: id(n), direction: "internal", state: "planned", date: "", party: "", where: "", requires: "", ...attrs }, content: [p(text(title))] });
const person = (name: string, extra: Record<string, unknown>) => ({ type: "contactCard", attrs: { data: { version: 1, contacts: [{ name, ...extra }] } } });
const jana = person("Jana Nováková", { title: "Vedoucí provozu", organization: "Modelsoft", emails: ["jana@modelsoft.example"], phones: ["+420 777 123 456"], objectId: id(101) });
const ivana = person("Ivana Malá", { title: "Primářka", organization: "Nemocnice Horní Lhota", phones: ["+420 602 000 222"], objectId: id(102) });
const issue = (key: string, title: string, state: string): YouTrackIssue => ({ id: key, key, title, state, url: `https://youtrack.example/issue/${key}` });
const issueNode = (attrs: YouTrackIssue) => ({ type: "youtrackIssue", attrs });
const nia = issue("PX2-1235", "Přihlášení přes NIA vrací chybu 500", "Open"), sms = issue("PX2-1301", "Doplnit SMS bránu do testovacího prostředí", "In Progress");

// 1. A page: an overview of each kind and a second one of contacts (both share one level state), over the page's own components.
const overview = (n: number, kind: string) => ({ type: "overview", attrs: { id: id(n), kind } });
const pageValue = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }, { id: "atlas-requirements", version: 1 }, { id: "youtrack", version: 1 }],
  document: { type: "doc", content: [
    overview(1, "requirements"),
    { type: "heading", attrs: { level: 2 }, content: [text("Portál pacienta")] },
    requirement(2, "SSL certifikát", { key: "portal.ssl", direction: "inbound", state: "done", date: "2026-09-10", party: "IT nemocnice" }),
    requirement(3, "SMS brána", { key: "portal.sms", direction: "inbound", state: "waiting_for_input", date: "2026-10-12", party: "IT nemocnice" }),
    requirement(4, "Předání dokumentace", { key: "portal.docs", direction: "outbound", party: "Jana Nováková" }),
    p(text("Otevřené úkoly: "), issueNode(nia), text(" a "), issueNode(sms), text(".")),
    jana, ivana,
    overview(5, "contacts"), overview(6, "issues"),
    p(text("Druhý přehled kontaktů na konci stránky: posuvník úrovně „Detail“ je s prvním jeden.")),
    overview(7, "contacts"),
  ] } } as unknown as MarkdownRichValue;
const page = ref<MarkdownRichValue>(structuredClone(pageValue));
const pageId = "gallery-overview-page";
const host: ProjectorHost = {
  spaceId: () => "gallery", pageId: () => pageId, open: () => {},
  api: { catalog: async () => ({ definitions: [], warnings: [] }), definitions: async () => ({ definitions: [], warnings: [] }),
    setCatalogPage: async (target, isCatalog) => ({ pageId: target, isCatalog }), createDeployment: async (_page, request) => ({ id: "gallery-new-page", title: request.title }) },
};
const editing = atlasContentPlugins({ kind: "edit", projector: host, youTrack: { search: async () => [nia, sms] } });
// The reading view with the issue chips (a public copy has none); the board's contact cards need no YouTrack.
const pageReading = [...atlasContentPlugins({ kind: "history" }), createOverviewReading(() => pageId)];
const reading = createDocumentWidgets({ kind: "public" });

// 2. A board without a server: its items as the board stores them (a block holds a node), one read card per kind over all of them.
const boardId = "gallery-board";
const block = (node: unknown): OverviewBoardItem => ({ kind: "block", props: { block: node } });
const boardItems: OverviewBoardItem[] = [block(jana), block(ivana), block(issueNode(nia)), block(issueNode(sms)),
  block(requirement(11, "Napojení na registr pacientů", { key: "portal.registry", state: "done", date: "2026-10-20" })),
  block(requirement(12, "Registrace v NIA", { key: "portal.nia", direction: "inbound", state: "blocked", party: "Správa ICT" }))];
const board = { items: () => boardItems };
// The board's own contact cards, drawn as its blocks are: they change with the slider through the canvas' data-overview-contacts.
const boardContacts = { format: "atlas-rich", version: 1, plugins: [{ id: "contacts", version: 2 }], document: { type: "doc", content: [jana, ivana] } } as unknown as MarkdownRichValue;
const canvas = ref<HTMLElement>();
let stopLevels: (() => void) | undefined;
onMounted(() => { if (canvas.value) stopLevels = watchOverviewLevels(canvas.value, boardId, () => true); });
onBeforeUnmount(() => stopLevels?.());
const kinds = overviewKinds().map(kind => `${kind.label} (${kind.kind})`).join(", ");
</script>

<template>
  <SettingsSection title="Přehled na stránce i na tabuli"
    description="Přehled je jedna komponenta s druhy (blok overview s atributem kind; registr src/overview/overviewKinds.ts, nový druh je jedno volání registerOverviewKind). Tři druhy: Stav nasazení (requirements), Kontakty (contacts) a Úkoly YouTrack (issues). Stejný druh sbírá komponenty ze stránky, nebo z celé tabule (tabule = její bloky, živé karty kontaktů, zmínky v textech, karty produktů a služeb). Každá karta má stejné rozvržení: název a vpravo v hlavičce posuvník úrovně „Detail“, uprostřed obsah, v patě hromadné akce. Stránka nebo tabule může mít několik přehledů téhož druhu a ty sdílejí jeden stav úrovně: posunutí jednoho posuvníku posune ostatní a přepne komponenty (data-overview-druh na kořeni), s jednou společnou animací (FLIP). Do Markdownu se zapíše jako blok ```a-overview s řádkem kind: contacts (požadavky jako čitelný řádek **Stav nasazení:** …); AI ho vkládá a čte přes MCP (atlas_insert_section, atlas_edit_board s blokem {&quot;type&quot;:&quot;overview&quot;,&quot;attrs&quot;:{&quot;kind&quot;:&quot;…&quot;}}, obsah atlas_get_overview, tabuli skládá projector_compose_board). Kdy ho použít: chce-li čtenář vidět a ovládat všechny komponenty jednoho druhu na stránce nebo tabuli najednou. Kdy ne: kontakty celého prostoru nebo wiki čte ContactOverview, souhrn napříč stránkami je Přehled nasazení prostoru; jedna komponenta se ovládá sama.">
    <p class="st-muted" data-example-overview-kinds>Registrované druhy: {{ kinds }}</p>
    <h4>Na stránce: editor a čtení</h4>
    <div class="overview-example-pair" data-example-overview-page>
      <!-- @vue-generic {'rich'} --><MarkdownEditor v-model="page" profile="rich" ariaLabel="Ukázková stránka s přehledy" :plugins="editing"
        v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      <!-- @vue-generic {'rich'} --><MarkdownRenderer :value="pageValue" profile="rich" ariaLabel="Táž stránka při čtení" :plugins="pageReading" :class="[theme, 'atlas-document-editor']" />
    </div>
    <h4>Na tabuli: karta druhu nad všemi bloky tabule</h4>
    <div ref="canvas" class="overview-example-board" data-example-overview-board>
      <OverviewReadCard v-for="kind in ['requirements', 'contacts', 'issues']" :key="kind" :kind="kind" :scope="boardId" :board="board" />
      <!-- @vue-generic {'rich'} --><MarkdownRenderer :value="boardContacts" profile="rich" ariaLabel="Karty kontaktů na tabuli" :plugins="reading" :class="[theme, 'atlas-document-editor']" />
    </div>
  </SettingsSection>
</template>

<style scoped>
.overview-example-pair { display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(min(100%, 360px), 1fr)); }
.overview-example-board { display: grid; gap: 16px; margin-top: 8px; grid-template-columns: repeat(auto-fit, minmax(min(100%, 360px), 1fr)); }
</style>
