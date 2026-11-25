<script setup lang="ts">
import { loadMarkdownDialect } from "@atlas/markdown/core";
import { computed, ref, shallowRef } from "vue";
import { boardColors, type BoardBlockNode, type BoardColor, type BoardProps, type BoardShape } from "../../boards/boardApi";
import { youtrackNode } from "../../boards/boardBlockNodes";
import { blockSizeOf } from "../../boards/boardFlow";
import { createBoardModel, type BoardItem, type BoardModel, type UndoStep } from "../../boards/boardModel";
import type { PageTargetInfo } from "../../boards/boardSession";
import { defaultBlockNode, useEditorBlockModules, type BoardBlockHost } from "../../boards/editorBlocks";
import type { BoardLinksApi } from "../../boards/boardLinks";
import type { ContentRow } from "../../objects/relationsApi";
import { liveObjects } from "../../objects/liveObjects";
import BoardCanvas from "../boards/BoardCanvas.vue";
import BoardBlock from "../boards/blocks/BoardBlock.vue";
import { colorLabel } from "../boards/canvas/boardPalette";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";

// Everything a board can hold, on one board: each kind of item and every component of the page editor a board inserts (read from the
// same registry as the board's „Komponenta“ menu, so a new plugin appears here without a change). The real board model with a pretend
// server, as in BoardsExample; nothing leaves the gallery.
const items: BoardItem[] = [];
let z = 1;
const add = (item: Omit<BoardItem, "z">) => { items.push({ ...item, z: z++ } as BoardItem); return item.id; };
const frame = (id: string, text: string, x: number, y: number, width: number, height: number) => add({ id, kind: "frame", x, y, width, height, text });
const note = (id: string, x: number, y: number, text: string, color: BoardColor, parent?: string) =>
  add({ id, kind: "note", x, y, width: 160, height: 160, text, color, ...(parent && { parent }) });
const link = (id: string, from: string, to: string, props: BoardProps, text?: string) =>
  add({ id, kind: "connector", x: 0, y: 0, width: 8, height: 8, from, to, props, ...(text && { text }) });

// Poznámky: every colour of the palette.
const colourNames: Record<BoardColor, string> = { yellow: "Žlutá", orange: "Oranžová", red: "Červená", pink: "Růžová", purple: "Fialová", blue: "Modrá",
  teal: "Tyrkysová", green: "Zelená", gray: "Šedá", white: "Bílá", black: "Černá" };
frame("f-notes", "Poznámky ve všech barvách", 0, 0, 1080, 440);
boardColors.forEach((color, index) => note(`n-${color}`, 24 + (index % 6) * 176, 48 + Math.floor(index / 6) * 184, `**${colourNames[color]}** poznámka`, color, "f-notes"));

// Text a tvary: the four sizes of a text, the four shapes.
frame("f-shapes", "Text a tvary", 1140, 0, 760, 440);
(["s", "m", "l", "xl"] as const).forEach((fontSize, index) =>
  add({ id: `t-${fontSize}`, kind: "text", x: 1164, y: 48 + index * 56, width: 300, height: 48, text: `Text velikosti **${fontSize.toUpperCase()}**`, props: { fontSize, align: "left" }, parent: "f-shapes" }));
(["rect", "rounded", "ellipse", "diamond"] as BoardShape[]).forEach((shape, index) =>
  add({ id: `s-${shape}`, kind: "shape", x: 1500 + (index % 2) * 190, y: 48 + Math.floor(index / 2) * 180, width: 160, height: 140,
    text: { rect: "Obdélník", rounded: "Zaoblený", ellipse: "Elipsa", diamond: "Kosočtverec" }[shape], color: (["blue", "teal", "purple", "orange"] as const)[index], props: { shape, fontSize: "m", align: "center" }, parent: "f-shapes" }));
add({ id: "t-align", kind: "text", x: 1164, y: 300, width: 300, height: 110, text: "Text je **Markdown**: _kurzíva_, [odkaz](https://atlas.example) a seznam\n\n- jedna\n- dvě", props: { fontSize: "s", align: "left" }, parent: "f-shapes" });

// Spojnice: the three routes, the three states, arrows, dash, flow and thickness.
frame("f-links", "Spojnice", 0, 500, 1080, 520);
const linkRows: { label: string; props: BoardProps; text?: string }[] = [
  { label: "Pravoúhlá", props: { route: "elbow", arrowEnd: "arrow" } },
  { label: "Oblouk", props: { route: "curved", arrowEnd: "arrow" }, text: "s **popiskem**" },
  { label: "Přímá, obousměrná", props: { route: "straight", arrowStart: "arrow", arrowEnd: "arrow" } },
  { label: "Čeká, čárkovaně", props: { route: "elbow", arrowEnd: "arrow", dash: "dashed", state: "waiting" } },
  { label: "Hotovo, animovaná", props: { route: "elbow", arrowEnd: "arrow", state: "done", animated: true } },
  { label: "Blokuje, silná", props: { route: "elbow", arrowEnd: "arrow", state: "blocked", thickness: "l" }, text: "blokuje" },
];
linkRows.forEach((row, index) => {
  const x = 24 + (index % 2) * 540, y = 548 + Math.floor(index / 2) * 156;
  add({ id: `l-${index}-a`, kind: "shape", x, y, width: 140, height: 100, text: row.label, color: "white", props: { shape: "rounded", fontSize: "s", align: "center" }, parent: "f-links" });
  add({ id: `l-${index}-b`, kind: "shape", x: x + 320, y: y + (index % 3) * 12, width: 140, height: 100, text: "Cíl", color: "gray", props: { shape: "rounded", fontSize: "s", align: "center" }, parent: "f-links" });
  link(`c-${index}`, `l-${index}-a`, `l-${index}-b`, { arrowStart: "none", dash: "solid", ...row.props }, row.text);
});

// Skupiny: an open group, a collapsed one standing for its members.
frame("f-groups", "Skupiny", 1140, 500, 760, 520);
add({ id: "g-open", kind: "group", x: 1164, y: 548, width: 352, height: 192, text: "Otevřená skupina", color: "purple", parent: "f-groups" });
note("g-n1", 1180, 564, "Člen skupiny", "purple", "g-open");
note("g-n2", 1340, 564, "Další člen", "pink", "g-open");
add({ id: "g-closed", kind: "group", x: 1164, y: 780, width: 352, height: 192, text: "Sbalená skupina", color: "green", props: { collapsed: true }, parent: "f-groups" });
note("g-n3", 1180, 796, "Hotovo", "green", "g-closed");
note("g-n4", 1340, 796, "Také hotovo", "green", "g-closed");
note("g-n5", 1640, 660, "Spojnice ke sbalené skupině se sloučí", "yellow", "f-groups");
link("c-g1", "g-n5", "g-n3", { route: "elbow", arrowEnd: "arrow" });
link("c-g2", "g-n5", "g-n4", { route: "elbow", arrowEnd: "arrow" });

// Karty: a page, a page the reader may not see, a directory contact as a card and compact, a contact that is gone.
frame("f-cards", "Karty stránek a objektů", 0, 1080, 1900, 280);
add({ id: "p-page", kind: "page", x: 24, y: 1128, width: 280, height: 140, targetResourceId: "page-handbook", color: "teal", props: { display: "card" }, parent: "f-cards" });
add({ id: "p-locked", kind: "page", x: 336, y: 1128, width: 280, height: 140, targetResourceId: "page-secret", props: { display: "card" }, parent: "f-cards" });
add({ id: "o-card", kind: "object", x: 648, y: 1128, width: 300, height: 168, targetResourceId: "gallery-contact-jana", props: { objectId: "gallery-contact-jana", typeId: "atlas.contact", view: "card" }, parent: "f-cards" });
add({ id: "o-compact", kind: "object", x: 980, y: 1128, width: 300, height: 88, targetResourceId: "gallery-contact-petr", color: "blue", props: { objectId: "gallery-contact-petr", typeId: "atlas.contact", view: "compact" }, parent: "f-cards" });
add({ id: "o-locked", kind: "object", x: 1312, y: 1128, width: 300, height: 168, targetResourceId: "gallery-contact-gone", props: { objectId: "gallery-contact-gone", typeId: "atlas.contact", view: "card" }, parent: "f-cards" });
// The contacts as a board's targets seed the tab's live objects; read-only, so no edit goes to the server.
liveObjects.seed({ id: "gallery-contact-jana", typeId: "atlas.contact", version: 1, title: "Jana Nováková", canEdit: false, available: true,
  data: { name: "Jana Nováková", title: "Vedoucí provozu", organization: "Modelsoft", emails: ["jana.novakova@example.cz"], phones: ["+420 777 123 456"] } });
liveObjects.seed({ id: "gallery-contact-petr", typeId: "atlas.contact", version: 1, title: "Petr Svoboda", canEdit: false, available: true,
  data: { name: "Petr Svoboda", title: "Jednatel", emails: ["petr@example.cz"] } });
// The pages a derived list may follow, so its scope reads by name without a server.
for (const [id, title] of [["gallery-page-plan", "Plán vydání 3.2"], ["gallery-page-portal", "Portál pro zákazníky"]])
  liveObjects.seed({ id, typeId: "atlas.page", version: 1, title, canEdit: false, available: true, data: null });
liveObjects.seed({ id: "gallery-contact-gone", typeId: "atlas.contact", version: 0, title: "", data: null, canEdit: false, available: false });

// Seznamy z vazeb (P15): what the board composes from the relations, over sample answers of the relation reads.
frame("f-derived", "Seznamy z vazeb (sestaví se samy)", 1960, 0, 1140, 440);
add({ id: "d-people", kind: "links", x: 1984, y: 48, width: 340, height: 360, props: { show: "people" }, parent: "f-derived" });
add({ id: "d-waiting", kind: "links", x: 2348, y: 48, width: 340, height: 360, props: { show: "waiting" }, parent: "f-derived" });
add({ id: "d-usage", kind: "links", x: 2712, y: 48, width: 364, height: 360, props: { show: "usage", objectId: "gallery-contact-jana" }, parent: "f-derived" });
const contentRow = (id: string, typeId: string, title: string, usedCount: number): ContentRow =>
  ({ id, typeId, title, path: typeId === "atlas.page" ? "Projekty" : "", scope: "space", spaceId: "gallery-space", usedCount, linksIn: 0, linksOut: 0, comments: 0, updatedAt: "2026-10-31", canEdit: true });
const derivedRows = [contentRow("gallery-contact-jana", "atlas.contact", "Jana Nováková", 9), contentRow("gallery-contact-petr", "atlas.contact", "Petr Svoboda", 4),
  contentRow("gallery-contact-karel", "atlas.contact", "Karel Novák", 2), contentRow("gallery-page-plan", "atlas.page", "Plán vydání 3.2", 6), contentRow("gallery-page-portal", "atlas.page", "Portál pro zákazníky", 3)];
const daysAgo = (days: number) => new Date(Date.now() - days * 86_400_000).toISOString().slice(0, 10);
const waitingItem = (blockId: string, title: string, pageTitle: string, days: number) =>
  ({ pageId: "gallery-page-plan", pageTitle, blockId, key: blockId, title, direction: "inbound" as const, state: "waiting_for_input" as const, date: daysAgo(days) });
const derivedApi: BoardLinksApi = {
  relations: {
    contents: async (_space, query) => ({ items: derivedRows.filter(row => (!query.type || row.typeId === query.type) && (!query.q || row.title.toLowerCase().includes(query.q.toLowerCase()))),
      summary: { types: {}, relations: { unused: 0, noIncoming: 0, broken: 0, attached: 0 } }, nextCursor: null }),
    relations: async () => null,
  },
  usage: {
    usage: async objectId => ({ objectId, total: 3, nextCursor: null, items: [
      { placeId: "gallery-page-plan", placeTypeId: "atlas.page", placeTitle: "Plán vydání 3.2", spaceId: "gallery-space", spaceName: "Provoz", kind: "mention", count: 2, draft: false, excerpt: "PM projektu je Jana Nováková, čekáme na její schválení." },
      { placeId: "gallery-board", placeTypeId: "atlas.board", placeTitle: "Vydání 3.2", spaceId: "gallery-space", spaceName: "Provoz", kind: "placement", count: 1, draft: false },
      { placeId: "gallery-page-portal", placeTypeId: "atlas.page", placeTitle: "Portál pro zákazníky", spaceId: "gallery-space", spaceName: "Provoz", kind: "party", count: 1, draft: true },
    ] }),
    count: async objectId => ({ objectId, total: 3, hidden: true }),
  },
  waiting: {
    waiting: async () => ({ parties: [
      { party: "Jana Nováková", items: [waitingItem("schvaleni", "Schválení rozsahu vydání", "Plán vydání 3.2", 12), waitingItem("texty", "Texty pro portál", "Portál pro zákazníky", 3)] },
      { party: "Zákazník", items: [waitingItem("vpn", "Přístup do VPN", "Portál pro zákazníky", 21)] },
    ] }),
    contacts: async parties => new Map(parties.filter(party => party === "Jana Nováková").map(party => [party, { name: party, organization: "Modelsoft" }])),
  },
};

// Komponenty editoru: every slash entry a board inserts with its defaults, plus the ones that pick their content (YouTrack, a contact).
// The contact and chart components write `a-` components, so the editor's dialect loads first, as on a board (BoardView).
const issue = { id: "2-1", key: "ATL-123", title: "Oprava přihlášení přes OIDC", url: "https://youtrack.example/issue/ATL-123", state: "In Progress" };
// A component that starts empty gets sample content in the shape of its default node, so the board shows what it looks like in use.
type Child = { type?: string; content?: Child[] };
const p = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const tableText = [["Krok", "Kdo", "Termín"], ["Příprava větve", "Jana", "Po"], ["Kontrola změn", "Petr", "Út"], ["Nasazení", "Karel", "St"]];
function withSample(block: BoardBlockNode): BoardBlockNode {
  switch (block.type) {
    case "callout": return { ...block, content: [p("Informační box: důležité upozornění k vydání.")] };
    case "concealedContent": return { ...block, content: [p("Skrytý text, který se odkryje setřením.")] };
    case "codeBlock": return { ...block, attrs: { ...block.attrs, language: "typescript" }, content: [{ type: "text", text: "const board = await boardApi.create(spaceId, \"Plán vydání\");" }] };
    case "table": return { ...block, content: (block.content as Child[]).map((row, r) =>
      ({ ...row, content: row.content?.map((cell, c) => ({ ...cell, content: [tableText[r]?.[c] ? p(tableText[r]![c]!) : { type: "paragraph" }] })) })) };
    default: return block;
  }
}
const blockCount = ref(0);
/** The same components outside a board: as a page renders them (the board's layout applies only inside a board node). */
const pageBlocks = shallowRef<{ label: string; block: BoardBlockNode }[]>([]);
const model = shallowRef<BoardModel>();
void loadMarkdownDialect().then(() => {
  const modules = useEditorBlockModules();
  const blocks: { label: string; block: BoardBlockNode }[] = [
    ...modules.flatMap(module => { const block = defaultBlockNode(module); return block ? [{ label: module.label, block: withSample(block) }] : []; }),
    { label: "Úkol YouTrack", block: youtrackNode(issue)! },
    { label: "Kontakt (blok)", block: { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Karel Novák", title: "Správce sítě", emails: ["karel.novak@example.cz"] }] } } } },
  ];
  // Each component at the size the board gives it when inserted, in rows; a component that flows is as tall as its content.
  const lay = (frameId: string, title: string, top: number, entries: { label: string; block: BoardBlockNode; color?: BoardColor }[], prefix: string) => {
    let x = 24, y = top + 48, rowHeight = 0;
    const placed = entries.map(entry => {
      const size = blockSizeOf(entry.block.type);
      if (x + size.width > 1840) { x = 24; y += rowHeight + 84; rowHeight = 0; }
      const at = { x, y };
      x += size.width + 48;
      rowHeight = Math.max(rowHeight, size.height);
      return { ...entry, ...at, ...size };
    });
    frame(frameId, title, 0, top, 1900, y + rowHeight + 64 - top);
    placed.forEach(({ label, block, color, x: left, y: top, width, height }, index) => {
      add({ id: `${prefix}-label-${index}`, kind: "text", x: left, y: top, width, height: 32, text: `**${label}**`, props: { fontSize: "s", align: "left" }, parent: frameId });
      add({ id: `${prefix}-${index}`, kind: "block", x: left, y: top + 36, width, height, ...(color && { color }), props: { block }, parent: frameId });
    });
    return y + rowHeight + 64;
  };
  const end = lay("f-blocks", `Komponenty editoru (${blocks.length})`, 1420, blocks, "b");
  // Every component with a background, all five tones and white and black among them: light tones keep dark text, dark ones turn it
  // light. One a board does not lay without a card (a requirement) has no background and is left out.
  const coloured: Record<string, BoardColor> = { chart: "teal-1", table: "slate-1", contactCard: "indigo-4", youtrackIssue: "amber-2", mathBlock: "violet-5",
    codeBlock: "slate-5", callout: "green-3", diagram: "blue-4", concealedContent: "black" };
  lay("f-colours", "S pozadím", end + 60, blocks.flatMap(entry => {
    const color = coloured[entry.block.type];
    return color ? [{ ...entry, label: `${entry.label} · ${colorLabel(color)}`, color }] : [];
  }), "bc");
  blockCount.value = blocks.length;
  pageBlocks.value = blocks;
  model.value = createBoardModel({ title: "Vše, co tabule umí", sequence: 0, settings: { background: "dots", snapToGrid: false, gridSize: 24 }, items });
});
const targets = new Map<string, PageTargetInfo>([
  ["page-handbook", { title: "Provozní příručka", available: true, spaceTitle: "Provoz", excerpt: "Jak se u nás vydává nová verze: příprava větve, kontrola změn a nasazení." }],
  ["page-secret", { title: null, available: false }],
]);
const host: BoardBlockHost = { youTrack: { search: async () => [issue], resolve: async reference => (reference === issue.key ? issue : null) },
  links: { spaceId: () => "gallery-space", api: derivedApi } };

const revision = ref(0);
const viewItems = computed(() => (revision.value, (model.value?.items ?? []).map(item => (item.kind === "page" && item.targetResourceId ? { ...item, target: targets.get(item.targetResourceId) } : item))));
const settings = computed(() => (revision.value, model.value!.settings));
function confirm(batch: UndoStep | null) {
  if (batch) model.value!.acknowledge(batch.operationId, { sequence: model.value!.sequence + 1, duplicate: false });
  revision.value++;
}
</script>

<template>
  <SettingsPage title="Tabule: všechny prvky" description="Jedna tabule se vším, co na ni jde dát, s ukázkovými daty: poznámky ve všech barvách, text a tvary, spojnice, skupiny, karty stránek a objektů a všechny komponenty editoru.">
    <SettingsSection title="Vše na jedné tabuli"
      :description="`Přehled k porovnání vzhledu, ne nová komponenta: BoardCanvas z ukázky Tabule s modelem v paměti. Komponenty editoru (${blockCount}) jdou ze stejného registru jako nabídka Komponenta; co tabule nepřijímá, říká boardBlockExclusions (editorBlocks.ts). Leží bez karty (boardFlow.blockLayoutOf, boardBlockLayouts.css): graf, diagram, vzorec a skrytý obsah vyplní položku, ostatní berou její šířku a výšku podle obsahu. Pozadí je volitelné, z palety prostorů. Požadavek a neznámý plugin zůstávají kartou. Seznamy z vazeb (BoardLinks, druh links, v nabídce Komponenta): Lidé projektu, Na co čekáme a Kde se používá se skládají z existujících vazeb (Obsah prostoru, Projector, Použito na), ne ručně, a každý čtenář vidí jen to, co smí číst. Zvolte je místo ručního seznamu jmen nebo úkolů, kdykoli má seznam odpovídat stránkám: ruční poznámka zastará, tenhle se obnoví sám (návrat na kartu a jednou za minutu). Lidé projektu = kdo se ve stránkách prostoru (nebo jedné stránky) zmiňuje, nejpoužívanější první; Na co čekáme = požadavky ve stavu čeká na vstup, s kým a od kdy (jen s modulem Projector); Kde se používá = každý odkaz, zmínka a karta, které vedou na jednu stránku, tabuli nebo kontakt. Proč nová komponenta: řádky jsou sdílené (WaitingList, UsagePanel, SubjectAvatar), nová je jen karta, která je drží na tabuli a volí jejich rozsah. Ovládání: klik vybere, Enter, F2 nebo dvojklik volí rozsah (prostor či stránka, u Kde se používá sledovaná věc) ve sdíleném seznamu s hledáním, první výsledek předvybraný, Enter ho vezme, první Esc smaže hledání, další úpravu zavře jako klik mimo; Ctrl+mezerník nebo pravé tlačítko otevře nabídku akcí; klik nebo Enter na řádku otevře, kam vede.`">
      <LoadingSkeleton v-if="!model" :rows="3" label="Načítání tabule…" />
      <div v-else class="board-all-stage" data-board-all-items>
        <BoardCanvas class="board-all-canvas" :items="viewItems" label="Vše, co tabule umí" can-edit :settings="settings" :block-host="host"
          :can-undo="model.canUndo" :can-redo="model.canRedo"
          @change="(changes, coalesce) => confirm(model!.apply(changes, coalesce))" @undo="confirm(model!.undo())" @redo="confirm(model!.redo())" />
      </div>
    </SettingsSection>
    <SettingsSection title="Ve stránce" description="Tytéž komponenty mimo tabuli, jak je vykreslí stránka (sloupec 720 px). Vzhled na tabuli platí jen uvnitř položky tabule, stránky se nemění; tady je to vidět vedle sebe.">
      <div class="board-all-page" data-board-all-page>
        <div v-for="entry in pageBlocks" :key="entry.label" class="board-all-page-block">
          <span class="st-muted">{{ entry.label }}</span>
          <BoardBlock :block="entry.block" :host="host" :aria-label="entry.label" readonly />
        </div>
      </div>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.board-all-page { display: grid; gap: 20px; max-width: 720px; }
.board-all-page-block { display: grid; gap: 6px; }
.board-all-stage { display: flex; min-width: 0; border-radius: var(--atlas-radius-lg); overflow: hidden; }
.board-all-canvas { flex: 1; height: 760px; }
</style>
