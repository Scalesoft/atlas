<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import type { MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import type { Editor } from "@tiptap/core";
import "@atlas/markdown/styles/base.css";
import { createDocumentWidgets } from "../../documentWidgets";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import { createHeadingFoldPlugin } from "../../editor/headingFoldPlugin";
import { editorChrome, editorOutline, useEditorTheme } from "../../editor/editorSetup";
import { createRequirementsPlugin, type ProjectorHost } from "../../projector/requirementsPlugin";
import { requirementStates, type RequirementState } from "../../projector/requirementTypes";
import { requirementLevels, setViewPrefs, type RequirementSort } from "../../projector/requirementViewMode";
import { setOverviewLevel } from "../../overview/overviewKinds";
import OptionPills from "../settings/OptionPills.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { Button } from "../ui/button";
import fixture from "./requirementsModelPage.json";

/**
 * The interaction model of requirements and Přehled in the one editor
 * on a page of real size: the structure of the biggest deployment page, anonymised (texts, keys, parties and ids replaced; states, dates
 * and nesting spread by a fixed rule), as requirement headings (a module with nested blocks a
 * plain heading, the dependency line and the facts line under each requirement). The same editor writes and reads (`editable`), a collaborator is simulated by remote transactions,
 * and every interaction is measured against the model's budgets. Why not RequirementsExample: that one shows each part on a few blocks;
 * this one is the whole page at once, for the model and its speed.
 */
// A page id of the shape a page has: the fixture's dependency lines link to its sections by it.
const pageId = "00000000-0000-4000-8000-0000000000aa";
const theme = useEditorTheme();
const writing = ref("write");
const summary = ref("auto");
const value = ref(fixture as unknown as MarkdownRichValue);
const version = ref(0);
const empty = async () => ({ definitions: [], warnings: [] });
const host = {
  spaceId: () => "gallery", pageId: () => pageId, parentId: () => null, pageTitle: () => "Nasazení (anonymizovaná kopie největší stránky)",
  open: () => {}, isCatalog: () => false,
  api: { catalog: empty, definitions: empty, setCatalogPage: async (id: string, isCatalog: boolean) => ({ pageId: id, isCatalog }),
    createDeployment: async () => ({ id: "x", title: "x" }), pageRequirements: async () => ({ blocks: [] }), prerequisites: async () => [],
    dependents: async () => ({ key: "", rows: [] }), cycle: async () => ({ path: null }), searchRequirements: async () => [] },
} as unknown as ProjectorHost;
let editor: Editor | undefined;
const probe: MarkdownPlugin = { id: "requirements-model-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
const plugins = [createHeadingAnchorsPlugin(), createHeadingFoldPlugin(), ...createDocumentWidgets({ kind: "edit" }).filter(plugin => plugin.id !== "atlas-requirements"),
  createRequirementsPlugin({ host, offered: () => true }), probe];

/** The page with its summary pinned as a block at the top, or without one (the automatic overview stands there). */
function setSummary(next: string) {
  summary.value = next;
  const content = (fixture.document.content as unknown[]).slice();
  if (next === "pinned") content.splice(3, 0, { type: "overview", attrs: { id: "00000000-0000-4000-8000-0000000000ff", kind: "requirements" } });
  value.value = { ...(fixture as unknown as MarkdownRichValue), document: { type: "doc", content } } as MarkdownRichValue;
  version.value++;
}

// A collaborator: changes come as y-sync's own transactions (remoteChange.ts reads that meta), not in this reader's undo.
const blocks = () => { const found: number[] = []; editor?.state.doc.forEach((node, pos) => { if (node.type.name === "heading" && node.attrs.state) found.push(pos); }); return found; };
let turn = 0;
function remoteChange(count = 1) {
  if (!editor) return;
  const positions = blocks(), tr = editor.state.tr;
  for (let index = 0; index < count; index++) {
    const pos = positions[(turn * 37 + index * 11) % positions.length]!, state = requirementStates[(turn + index) % requirementStates.length] as RequirementState;
    tr.setNodeAttribute(pos, "state", state);
  }
  turn++;
  editor.view.dispatch(tr.setMeta("y-sync$", { isChangeOrigin: true }).setMeta("addToHistory", false));
}
/** A burst: ten single changes 30 ms apart, as a collaborator's quick clicks reach the page. */
function remoteBurst() { for (let index = 0; index < 10; index++) setTimeout(() => remoteChange(1), index * 30); }

// The budgets (model §9): an interaction's input → next paint (Event Timing), and every main-thread task over 50 ms (Long Tasks).
interface Row { label: string; ms: number; script: number; long: number; budget: number }
const rows = shallowRef<Row[]>([]);
const longTasks: PerformanceEntry[] = [];
const events = shallowRef<{ name: string; ms: number }[]>([]);
let observers: PerformanceObserver[] = [];
const root = ref<HTMLElement>();
onMounted(() => {
  const observe = (type: string, take: (entry: PerformanceEntry) => void, extra: Record<string, unknown> = {}) => {
    try { const observer = new PerformanceObserver(list => list.getEntries().forEach(take)); observer.observe({ type, buffered: false, ...extra } as PerformanceObserverInit); observers.push(observer); } catch { /* not in this browser */ }
  };
  observe("longtask", entry => longTasks.push(entry));
  observe("event", entry => {
    const target = (entry as PerformanceEventTiming).target;
    if (target && root.value?.contains(target as Node) && ["click", "keydown", "pointerdown", "pointerup"].includes(entry.name))
      events.value = [{ name: entry.name, ms: Math.round(entry.duration) }, ...events.value].slice(0, 8);
  }, { durationThreshold: 16 });
});
onBeforeUnmount(() => observers.forEach(observer => observer.disconnect()));
const frame = () => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
/** One change, timed from the call to the frame after it painted, with the long tasks in between. */
async function time(label: string, change: () => void, budget = 100) {
  await frame();
  const start = performance.now(), before = longTasks.length;
  change();
  // Vue's watchers run in the same task's microtasks: the script's part ends when they have.
  await Promise.resolve();
  const script = performance.now() - start;
  await frame();
  const ms = performance.now() - start, long = longTasks.slice(before).filter(task => task.duration > 50).length;
  rows.value = [...rows.value, { label, ms: Math.round(ms), script: Math.round(script), long, budget }];
}
const running = ref(false);
async function measureAll() {
  running.value = true;
  rows.value = [];
  try {
    for (const level of ["s", "none", "m"]) await time(`úroveň ${level.toUpperCase()}`, () => setOverviewLevel("requirements", pageId, level));
    await time("filtr: čeká na vstup", () => setViewPrefs(pageId, { state: "waiting_for_input" }));
    await time("filtr: vše", () => setViewPrefs(pageId, { state: "" }));
    for (const sort of ["state", "date", ""] as RequirementSort[]) await time(`řazení v Přehledu: ${sort || "neřadit"}`, () => setViewPrefs(pageId, { sort }));
    await time("kolega: 1 změna", () => remoteChange(1));
    await time("kolega: 10 změn najednou", () => remoteChange(10));
  } finally { running.value = false; }
}
(window as unknown as { atlasRequirementsModel?: unknown }).atlasRequirementsModel = { measureAll, rows, remoteChange };
const writeOptions = [{ value: "write", label: "Píše (editable)" }, { value: "read", label: "Čte (editable = false)" }];
const summaryOptions = [{ value: "auto", label: "Automatický přehled" }, { value: "pinned", label: "Připnutý souhrn" }];
</script>

<template>
  <div ref="root" class="requirements-model" data-requirements-model>
    <SettingsSection title="Požadavky: model v jednom editoru"
      description="Prototyp modelu interakce na stránce skutečné velikosti: struktura největší stránky nasazení jako nadpisy požadavků (122 požadavků, 66 obyčejných nadpisů, 632 bloků), texty, klíče, strany a id nahrazené, stavy, termíny a vnoření rozložené pevným pravidlem. Tentýž editor píše i čte (přepínač editable, žádný režim). Úroveň (NONE skryje požadavky, S je sbalí, M rozbalí) a filtr jsou pohled čtenáře v dokumentu, řazení je seřazený seznam odkazů v Přehledu, kolega se simuluje vzdálenými transakcemi. Měřič ukazuje dobu od vstupu do dalšího snímku (rozpočet ≤ 100 ms při 1×) a úlohy hlavního vlákna nad 50 ms. Proč ne ukázka „Požadavky ve stránce“: ta ukazuje jednotlivé části na pár blocích; tady je celá stránka najednou kvůli modelu a rychlosti.">
      <div class="requirements-model-bar">
        <OptionPills v-model="writing" :options="writeOptions" label="Kdo" />
        <OptionPills :model-value="summary" :options="summaryOptions" label="Souhrn" @update:model-value="setSummary" />
        <Button variant="outline" type="button" data-model-remote @click="remoteChange(1)">Kolega změní stav</Button>
        <Button variant="outline" type="button" data-model-burst @click="remoteBurst">Kolega: 10 změn za sebou</Button>
        <Button variant="outline" type="button" :disabled="running" data-model-measure @click="measureAll">Změřit přepnutí</Button>
        <span class="st-muted">Úrovně: {{ requirementLevels.map(level => level.label).join(" · ") }}</span>
      </div>
      <div class="requirements-model-meter" aria-live="polite">
        <table v-if="rows.length" data-model-results>
          <thead><tr><th>Změna</th><th>Do snímku</th><th>Z toho skript</th><th>Úlohy &gt; 50 ms</th></tr></thead>
          <tbody><tr v-for="row in rows" :key="row.label" :data-over="row.ms > row.budget || row.long ? '' : undefined"><td>{{ row.label }}</td><td>{{ row.ms }} ms</td><td>{{ row.script }} ms</td><td>{{ row.long }}</td></tr></tbody>
        </table>
        <p v-if="events.length" class="st-muted" data-model-events>Poslední interakce nad 16 ms: {{ events.map(event => `${event.name} ${event.ms} ms`).join(" · ") }}</p>
      </div>
      <div class="requirements-example">
        <!-- @vue-generic {'rich'} --><MarkdownEditor :key="version" v-model="value" profile="rich" ariaLabel="Model požadavků v jednom editoru" :plugins="plugins"
          :outline="editorOutline" :readonly="writing === 'read'" v-bind="editorChrome" :fullscreenable="false" :class="[theme, 'atlas-document-editor']" />
      </div>
    </SettingsSection>
  </div>
</template>

<style scoped>
.requirements-model-bar { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem 1rem; margin-bottom: .75rem; }
.requirements-model-meter { margin-bottom: .75rem; font-size: .8125rem; }
.requirements-model-meter table { border-collapse: collapse; }
.requirements-model-meter :is(th, td) { padding: .125rem .75rem .125rem 0; text-align: left; font-variant-numeric: tabular-nums; }
.requirements-model-meter tr[data-over] td { color: var(--rq-tone-blocked, #c0392b); font-weight: 600; }
</style>
