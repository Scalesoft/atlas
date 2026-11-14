<script setup lang="ts">
import type { Editor } from "@tiptap/core";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef } from "vue";
import { errorMessage } from "../api";
import t from "../i18n/projector";
import type { CatalogDefinition, ProjectorCatalog, RequirementJson, ResolvedRequirement } from "./projectorApi";
import CatalogPicker from "./CatalogPicker.vue";
import { catalogModules, planSelection, refusalText } from "./catalogSelection";
import { catalogBlocks, catalogOf } from "./requirementCatalog";
import { runtimeOf } from "./requirementRuntime";
import type { TypeAhead } from "../lib/typeAhead";
import type { ProjectorHost } from "./requirementsPlugin";

/**
 * „Moduly produktu“ (spec §1.4, §5; owner J2): the space's catalog in CatalogPicker, several requirements or whole modules at
 * once from the keyboard, with what they bring along shown live for the whole selection. The pick resolves on the catalog the panel
 * holds, listed without the blocks; only the blocks to insert are fetched, at the insertion. „Vložit a pokračovat“ (Ctrl+Enter)
 * keeps the panel open for the next pick, the inserted rows then read „Už je na stránce“. With a `source` (another deployment or a
 * preset, RequirementSourcePanel.vue) the same picker lists that page's blocks, which come with their bodies. A whole module none of
 * which the page has yet comes with its heading (plan 3.4 §6), one level under the heading it goes under: a module under its product.
 * As a `tree` („Moduly produktu“, plan 3.5 C; the editor's one catalog entry) it lists the modules under their product and category
 * with a preview of each, a search also the single requirements, and a pick goes under the page's heading of its product › category ›
 * module wherever the caret is (insertResolved's `place`), at the caret on a page without them.
 */
type Insert = (items: (ResolvedRequirement & { definition: { contentJson: RequirementJson }; heading?: string; path?: string[] })[],
  options?: { keepFocus?: boolean; place?: boolean }) => void;
const props = defineProps<{ host: ProjectorHost; editor: Editor; insert: Insert; close: () => void; typeAhead?: TypeAhead; tree?: boolean;
  /** Another page's blocks instead of the space's catalog: its name and its definitions with their blocks. */
  source?: { label: string; load: () => Promise<ProjectorCatalog> } }>();
const definitions = shallowRef<CatalogDefinition[]>([]);
const status = ref<"loading" | "ready" | "error">("loading");
const loadError = ref("");
const error = ref("");
const busy = ref(false);
const selected = ref<string[]>([]);
const picker = ref<InstanceType<typeof CatalogPicker>>();
const index = runtimeOf(props.editor).index;
const present = computed(() => new Set(index.value.items.map(item => item.attrs.key).filter(Boolean)));
const dismissed = computed(() => index.value.items.filter(item => item.attrs.state === "not_applicable" && item.attrs.key).map(item => item.attrs.key));
const modules = computed(() => catalogModules(definitions.value));
// A dependency missing on another deployment is missing there, not in the catalog.
const missingOn = props.source ? "page" as const : undefined;
const plan = computed(() => planSelection(definitions.value, modules.value, selected.value, [...present.value], dismissed.value, missingOn));
let closed = false;

// One catalog page names the list; with several, each row says which page it comes from.
const pageTitle = (pageId: string) => props.source ? undefined : props.host.titleOf?.(pageId);
const label = computed(() => { if (props.source) return props.source.label; const pages = new Set(definitions.value.map(definition => definition.pageId)); return (pages.size === 1 && pageTitle([...pages][0]!)) || t.catalogPanel.title; });
const count = computed(() => ("items" in plan.value ? plan.value.items.length : 0));
const insertLabel = computed(() => count.value <= 1 ? t.catalogPanel.insert : t.catalogPanel.insertMany(count.value));

/** The picked blocks are fetched now (the list came without them), then inserted; a definition gone meanwhile is said, not skipped. */
async function insert(keep: boolean, keys = selected.value) {
  const current = keys === selected.value ? plan.value : planSelection(definitions.value, modules.value, keys, [...present.value], dismissed.value, missingOn);
  if (busy.value || closed) return;
  error.value = "refused" in current ? refusalText(current.refused) : "";
  if (!("items" in current) || !current.items.length) return;
  busy.value = true;
  try {
    // By module, a module before what needs it (planSelection), as a new deployment lays them out; a whole new module under its heading.
    const whole = (section: (typeof current.sections)[number]) => !!section.module.title && section.items.length === section.module.keys.length
      && !section.module.keys.some(key => present.value.has(key));
    // „Jádro“ with no product above it means no category heading (the server's default), never one to make (the server's placement too).
    const path = ({ product, category, title }: (typeof current.sections)[number]["module"]) => [product, product || category !== "Jádro" ? category : "", title].filter(Boolean);
    const items = current.sections.flatMap(section => section.items.map((item, at) =>
      ({ ...item, ...(whole(section) ? { inModule: true, ...(at === 0 ? { heading: section.module.title } : {}) } : {}), ...(props.tree ? { path: path(section.module) } : {}) })));
    const blocks = props.source ? new Map(definitions.value.flatMap(definition => definition.contentJson ? [[definition.key, definition.contentJson] as const] : []))
      : await catalogBlocks(props.host, items.map(item => item.definition.key));
    if (closed) return;
    const gone = items.find(item => !blocks.has(item.definition.key));
    if (gone) { error.value = t.catalogPanel.gone(gone.definition.title); return; }
    props.insert(items.map(item => ({ ...item, definition: { ...item.definition, contentJson: blocks.get(item.definition.key)! } })), { keepFocus: keep, place: props.tree });
    selected.value = [];
    if (!keep) { closed = true; props.close(); return; }
    picker.value?.clear();
  } catch (failure) {
    if (!closed) error.value = errorMessage(failure);
  } finally {
    busy.value = false;
    if (keep && !closed) await nextTick(() => picker.value?.focus());
  }
}

onMounted(async () => {
  try {
    const catalog = await (props.source ? props.source.load() : catalogOf(props.editor, props.host));
    if (closed) return;
    definitions.value = catalog.definitions;
    status.value = "ready";
  } catch (failure) {
    if (!closed) { loadError.value = errorMessage(failure); status.value = "error"; }
  }
});
onBeforeUnmount(() => { closed = true; });
</script>

<template>
  <div class="catalog" data-requirement-catalog>
    <CatalogPicker ref="picker" v-model:selected="selected" :definitions="definitions" :modules="modules" :plan="selected.length ? plan : undefined" :present="present"
      :dismissed="dismissed" :page-title="pageTitle" :label="label" :tree="tree" :status="status" :error="loadError" :busy="busy" :type-ahead="typeAhead" @submit="insert" />
    <p v-if="error" class="catalog-error" role="alert">{{ error }}</p>
    <div class="catalog-actions">
      <button type="button" class="md-btn" :disabled="busy || !count" aria-keyshortcuts="Control+Enter" data-catalog-continue @click="insert(true)">{{ t.catalogPanel.insertContinue }}</button>
      <button type="button" class="md-btn-primary" :aria-busy="busy" :disabled="busy || !count" aria-keyshortcuts="Enter" data-catalog-confirm @click="insert(false)">{{ insertLabel }}</button>
    </div>
  </div>
</template>

<style scoped>
.catalog { display: grid; gap: 8px; min-width: 0; }
.catalog-actions { display: flex; justify-content: flex-end; gap: 6px; }
.catalog-error { margin: 0; color: var(--error); }
</style>
