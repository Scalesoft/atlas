<script setup lang="ts">
import { computed, nextTick, onMounted, ref, useId, watch } from "vue";
import { ownEscape, useSearchKeys } from "../lib/searchKeys";
import type { TypeAhead } from "../lib/typeAhead";
import { rankExactFirst } from "../lib/rankExactFirst";
import AtlasIcon from "../components/AtlasIcon.vue";
import { riseIn, RISE_IN_ROWS } from "../motion/aiMotion";
import LoadingSkeleton from "../components/LoadingSkeleton.vue";
import t from "../i18n/projector";
import type { CatalogDefinition } from "./projectorApi";
import { planSummary, refusalText, type CatalogModule, type SelectionPlan } from "./catalogSelection";
import { resolveRequirements } from "./requirementResolver";
import { directionLabel } from "./requirementSummary";

/**
 * Several catalog rows ticked at once, from the keyboard (owner, J2): the catalog panel in the editor and „Nasazení z katalogu“.
 * Typing filters and preselects the first row, arrows move, Space ticks the highlighted row (Shift+Space types a space), Ctrl+A ticks
 * every row shown (again: none), Enter inserts what is ticked or else the highlighted row (once something is typed or the arrows moved to
 * it; the untouched empty search takes nothing), Ctrl+Enter does so and keeps going, the first Esc empties the search. A
 * module (a section of a catalog page) is one row, „Celý modul NIA (4)“, and the modules stand under the heading of their category
 * („Jádro“, „Autentizace“…), which a search finds too and then shows whole; a heading is no row, the arrows skip it. Below, live for the whole selection: what will be
 * inserted, what comes along and why, and what is worth knowing. The owner keeps the selection (keys) and the plan of it
 * (planSelection), and inserts.
 * As a `tree` („Moduly produktu“, plan 3.5 C) it is two panes wide: the modules under their product and category, whose headings tick
 * all of theirs (a whole product too, O6), a search also showing the single requirements it finds under their module (O2); beside them
 * the highlighted row's module: where it stands, what it is (its catalog paragraph, O7) and requirement by requirement what comes, what
 * the page has, and what comes along from other modules and why.
 * Why not createSuggestionList (the editor's list): it picks one row. BoardPickList ticks several, but Enter ticks there and it
 * filters on the server; SearchableChoiceList is a radiogroup.
 */
const props = withDefaults(defineProps<{
  definitions: readonly CatalogDefinition[]; modules: readonly CatalogModule[]; plan?: SelectionPlan | { refused: { code: string; path?: string[] } };
  present?: ReadonlySet<string>; dismissed?: readonly string[]; modulesOnly?: boolean; pageTitle?: (pageId: string) => string | undefined;
  label: string; status?: "loading" | "ready" | "error"; error?: string; emptyText?: string; busy?: boolean; autofocus?: boolean; tree?: boolean;
  /** What was typed while the picker opened: the first search, and an Enter after it inserts its first match. */
  typeAhead?: TypeAhead;
}>(), { plan: undefined, present: () => new Set<string>(), dismissed: () => [], modulesOnly: false, tree: false, pageTitle: undefined, status: "ready", error: "",
  emptyText: undefined, busy: false, autofocus: true });
const selected = defineModel<string[]>("selected", { required: true });
/** `keys`: what is ticked, or, with nothing ticked, the highlighted row's (left unticked, so a refusal leaves the selection as it was). */
const emit = defineEmits<{ submit: [keep: boolean, keys: string[]] }>();
const uid = useId();
const query = ref("");
const input = ref<HTMLInputElement>();
const list = ref<HTMLElement>();

/** `heading`: a product's or category's row of the tree, which ticks its modules; `modules` the modules a row stands for. */
interface Row { id: string; module: boolean; category: string; product: string; title: string; description: string; keys: string[]; keywords: string[];
  heading?: "product" | "category"; modules: CatalogModule[] }

const byKey = computed(() => new Map(props.definitions.map(definition => [definition.key, definition])));
const pages = computed(() => new Set(props.definitions.map(definition => definition.pageId)));
const moduleName = (module: CatalogModule) => module.title || props.pageTitle?.(module.pageId) || t.picker.noSection;
const moduleOf = computed(() => new Map(props.modules.flatMap(module => module.keys.map(key => [key, module] as const))));
// Two modules of one name („Core“ of the portal and of MPI) say their keys' prefix.
const repeated = computed(() => { const seen = new Map<string, number>(); for (const module of props.modules) seen.set(module.title, (seen.get(module.title) ?? 0) + 1); return seen; });

// What a row brings besides itself: a requirement names the blocks, a module the other modules; once per row.
const hints = new Map<string, string>();
function hint(id: string, keys: string[], module: CatalogModule | null) {
  let text = hints.get(id);
  if (text === undefined) {
    const resolution = resolveRequirements(props.definitions, keys, [...props.present], props.dismissed);
    const extra = "items" in resolution ? resolution.items.filter(item => !keys.includes(item.definition.key)) : [];
    const names = module ? [...new Set(extra.map(item => moduleOf.value.get(item.definition.key)).filter(other => other && other !== module).map(other => moduleName(other!)))]
      : extra.map(item => item.definition.title);
    text = names.length ? `+ ${names.slice(0, 2).join(", ")}${names.length > 2 ? ` ${t.picker.andMore(names.length - 2)}` : ""}` : "";
    hints.set(id, text);
  }
  return text;
}
watch(() => [props.definitions, props.present], () => hints.clear());

const rows = computed<Row[]>(() => props.modules.flatMap(module => {
  const name = moduleName(module), left = module.keys.filter(key => !props.present.has(key));
  const own: Row = { id: `module:${module.id}`, module: true, category: module.category, product: module.product, modules: [module],
    title: props.modulesOnly || props.tree ? name : t.picker.wholeModule(name, module.keys.length), keys: left,
    // Listed on its own, a module is also found by what it holds; beside its rows (the tree's search shows them too), by its name alone
    // (Ctrl+A ticks what matched).
    keywords: [name, module.prefix, module.category, module.product, ...(props.modulesOnly ? module.keys.map(key => byKey.value.get(key)!.title) : [])],
    description: !left.length ? t.picker.moduleAllPresent
      : [hint(`module:${module.id}`, left, module),
        // Partly on the page: how much is there, not the module's size, which reads as that many to come (PR-7).
        left.length < module.keys.length ? t.picker.partPresent(module.keys.length - left.length, module.keys.length)
          : t.counts.requirements(module.keys.length),
        (repeated.value.get(module.title) ?? 0) > 1 ? module.prefix : ""].filter(Boolean).join(" · ") };
  if (props.modulesOnly) return [own];
  // Requirements before a catalog page's first heading belong to no module.
  return [...(module.title ? [own] : []), ...module.keys.map(key => {
    const definition = byKey.value.get(key)!, present = props.present.has(key);
    return { id: key, module: false, category: module.category, product: module.product, modules: [module], title: definition.title, keys: present ? [] : [key], keywords: [key, definition.bundle, module.category],
      description: present ? t.picker.present : [hint(key, [key], null), definition.bundle, pages.value.size > 1 ? props.pageTitle?.(definition.pageId) : "", key].filter(Boolean).join(" · ") };
  })];
}));
// A search ranks the rows; each category then keeps its rows together, categories in the order the best match of each came (catalog order
// untouched); so does each product, its categories within it, and in the tree each module, its row before its requirements. The tree
// without a search lists the modules alone, and the requirements no module heading holds.
const matches = computed(() => rankExactFirst(query.value, props.tree && !query.value.trim() ? rows.value.filter(row => row.module || !row.modules[0]!.title) : rows.value,
  row => ({ title: row.title, keywords: row.keywords })));
const ranked = computed(() => {
  const found = [...matches.value], order = new Map<string, number>();
  const group = (row: Row) => `${row.product}\u0000${row.category}`, module = (row: Row) => props.tree ? row.modules[0]!.id : "";
  for (const row of found) for (const key of [row.product, group(row), module(row)]) if (!order.has(key)) order.set(key, order.size);
  return found.sort((a, b) => order.get(a.product)! - order.get(b.product)! || order.get(group(a))! - order.get(group(b))!
    || order.get(module(a))! - order.get(module(b))! || Number(b.module) - Number(a.module));
});

// shortcut: a big catalog draws a hundred rows at a time, more as the list scrolls or the keyboard reaches its end; window it if
// catalogs grow into thousands.
const PAGE = 100;
const limit = ref(PAGE);
const shown = computed(() => ranked.value.slice(0, limit.value));
// The rows by category (ranked keeps a category's rows together): one group of the listbox each, named by its category for a screen reader.
const groups = computed(() => shown.value.reduce<{ category: string; rows: Row[] }[]>((all, row) => {
  if (all.at(-1)?.category === row.category) all.at(-1)!.rows.push(row); else all.push({ category: row.category, rows: [row] });
  return all;
}, []));
// The tree: a product's and a category's heading row before their first module shown; each ticks all its modules, also those a search hides.
const headingRow = (heading: "product" | "category", product: string, category = ""): Row => {
  const modules = rows.value.filter(row => row.product === product && (heading === "product" || row.category === category)).flatMap(row => row.modules);
  const keys = modules.flatMap(module => module.keys.filter(key => !props.present.has(key))), size = modules.reduce((all, module) => all + module.keys.length, 0);
  return { id: `${heading}:${product} › ${category}`, module: true, heading, product, category, modules, keys, keywords: [], title: heading === "product" ? product : category,
    description: !keys.length ? t.picker.headingAllPresent : `${t.counts.modules(modules.length)} · ${t.counts.requirements(size)}` };
};
const byId = computed(() => new Map(rows.value.map(row => [row.id, row])));
// A requirement found without its module's row gets that row before it, so it always stands under its module.
const display = computed(() => !props.tree ? shown.value : shown.value.flatMap((row, index, all) => {
  const before = all[index - 1], newProduct = !!row.product && row.product !== before?.product;
  const owner = !row.module && before?.modules[0] !== row.modules[0] ? byId.value.get(`module:${row.modules[0]!.id}`) : undefined;
  return [...(newProduct ? [headingRow("product", row.product)] : []),
    ...(row.category && (newProduct || row.category !== before?.category) ? [headingRow("category", row.product, row.category)] : []), ...(owner ? [owner] : []), row];
}));
const enabled = (row: Row | undefined) => !!row?.keys.length;
const active = ref("");
let lastQuery = "";
// The first row a pick can take is preselected whenever the search changes (the best match; in the tree never a heading, which ranks
// nothing); otherwise (an insertion) the highlight stays.
watch(ranked, rows => {
  if (query.value !== lastQuery || !enabled(rows.find(row => row.id === active.value))) active.value = matches.value.find(enabled)?.id ?? "";
  lastQuery = query.value;
}, { immediate: true });
watch(query, () => { limit.value = PAGE; });
const activeRow = computed(() => display.value.find(row => row.id === active.value) ?? ranked.value.find(row => row.id === active.value));

const chosen = computed(() => new Set(selected.value));
const checked = (row: Row) => enabled(row) && row.keys.every(key => chosen.value.has(key));
const mixed = (row: Row) => row.module && !checked(row) && row.keys.some(key => chosen.value.has(key));
const previewBody = ref<HTMLElement>();
/** The tree's preview of the highlighted row: where it stands, each of its modules (a requirement's: its own) with what the module is and
 * requirement by requirement, and what comes along from other modules and for which of its requirements. */
const preview = computed(() => {
  const row = activeRow.value;
  if (!props.tree || !row) return undefined;
  const dismissed = new Set(props.dismissed), own = new Set(row.modules.flatMap(module => module.keys));
  const modules = row.modules.slice(0, 6).map(module => ({ module, name: moduleName(module), summary: module.summary, items: module.keys.map(key => {
    const definition = byKey.value.get(key)!;
    return { key, title: definition.title, direction: definition.direction ? directionLabel(definition.direction) : "",
      status: props.present.has(key) ? "present" as const : dismissed.has(key) ? "dismissed" as const : "new" as const };
  }) }));
  const resolution = resolveRequirements(props.definitions, row.keys, [...props.present], props.dismissed);
  const along = "items" in resolution ? resolution.items.filter(item => !own.has(item.definition.key)).map(item => {
    const module = moduleOf.value.get(item.definition.key);
    return { key: item.definition.key, title: `${module ? `${moduleName(module)} › ` : ""}${item.definition.title}`, why: neededBy(item.requiredBy) };
  }) : [];
  const path = row.heading === "product" ? "" : row.heading === "category" ? row.product : [row.product, row.category].filter(Boolean).join(" › ");
  return { row, path, title: row.module ? row.title : moduleName(row.modules[0]!), modules, more: row.modules.length - modules.length, coming: row.keys.length,
    present: [...own].filter(key => props.present.has(key)).length, along: along.slice(0, ALONG), alongMore: Math.max(0, along.length - ALONG) };
});
watch(() => preview.value?.row.id, () => void nextTick(() => { if (previewBody.value) riseIn([previewBody.value], RISE_IN_ROWS); }));
const ALONG = 6;
const STATUS = { new: t.picker.status.new, present: t.picker.status.present, dismissed: t.picker.status.dismissed } as const;
function toggle(row: Row | undefined) {
  if (!row || !enabled(row) || props.busy) return;
  active.value = row.id;
  searchKeys.move();
  const keys = new Set(selected.value);
  if (checked(row)) for (const key of row.keys) keys.delete(key); else for (const key of row.keys) keys.add(key);
  selected.value = props.definitions.map(definition => definition.key).filter(key => keys.has(key));
}
/** Ctrl+A: every row shown ticked, or, when they all are, none of them. */
function toggleShown() {
  const rows = ranked.value.filter(enabled), keys = new Set(selected.value), all = rows.every(checked);
  for (const row of rows) for (const key of row.keys) if (all) keys.delete(key); else keys.add(key);
  selected.value = props.definitions.map(definition => definition.key).filter(key => keys.has(key));
}
function move(step: 1 | -1) {
  const rows = display.value.filter(enabled);
  if (!rows.length) return;
  // The untouched list shows no highlight, so the first arrow lands on its first row instead of skipping it.
  const at = lit.value ? rows.findIndex(row => row.id === active.value) : -1;
  const next = rows[at < 0 ? 0 : Math.min(rows.length - 1, Math.max(0, at + step))]!;
  active.value = next.id;
  searchKeys.move();
  if (step > 0 && next === rows.at(-1)) more();
  void nextTick(() => document.getElementById(`${uid}-${next.id}`)?.scrollIntoView?.({ block: "nearest" }));
}
function more() { if (limit.value < ranked.value.length) limit.value += PAGE; }
function onScroll() { const element = list.value; if (element && element.scrollTop + element.clientHeight >= element.scrollHeight - 48) more(); }
/** Enter with nothing ticked takes the highlighted row. */
/** An Enter while the catalog loads: what it asked (keep going or not), done once the catalog is there. */
let submitWhenReady: boolean | undefined;
/** A Ctrl+A while the catalog loads: ticks every row once they are there, before the Enter after it (PR2-2). */
let tickAllWhenReady = false;
watch(() => props.status, status => {
  const keep = submitWhenReady, all = tickAllWhenReady;
  submitWhenReady = undefined;
  tickAllWhenReady = false;
  if (status !== "ready" || (keep === undefined && !all)) return;
  void nextTick(() => {
    if (all) toggleShown();
    if (keep === undefined) return;
    if (all) void nextTick(() => submit(keep)); else submit(keep);
  });
});
function submit(keep: boolean) {
  if (props.status === "loading") submitWhenReady = keep;
  if (props.busy || props.status !== "ready") return;
  const keys = selected.value.length ? selected.value : activeRow.value?.keys ?? [];
  if (keys.length) emit("submit", keep, [...keys]);
}
// The first Esc empties the search, the next one closes the panel; Enter inserts what is ticked, else the highlighted row once something
// is typed or the arrows moved to it, and does nothing in the empty search of a list nobody has touched, which highlights no row (P3, P8).
// Tab leaves: a tick is no suggestion.
const searchKeys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, tab: false, take: () => submit(false),
  empty: () => { if (selected.value.length || tickAllWhenReady) submit(false); return true; } });
const lit = searchKeys.lit;
function onKeydown(event: KeyboardEvent) {
  if (event.isComposing || event.keyCode === 229) return;
  if (searchKeys.onKeydown(event)) return;
  const modified = event.ctrlKey || event.metaKey;
  if (event.key === "ArrowDown" || event.key === "ArrowUp") { event.preventDefault(); move(event.key === "ArrowDown" ? 1 : -1); }
  else if (event.key === " " && !event.shiftKey && !modified && !event.altKey && lit.value && enabled(activeRow.value)) {
    event.preventDefault();
    toggle(activeRow.value);
    // The filter stays to tick more of it; typing starts the next search.
    input.value?.select();
  } else if (event.key === "Enter" && modified && !event.shiftKey && !event.altKey) { event.preventDefault(); submit(true); }
  else if (event.key.toLowerCase() === "a" && modified && !event.shiftKey && !event.altKey && props.status === "loading") { event.preventDefault(); tickAllWhenReady = !tickAllWhenReady; }
  else if (event.key.toLowerCase() === "a" && modified && !event.shiftKey && !event.altKey && ranked.value.some(enabled)) { event.preventDefault(); toggleShown(); }
}

const plan = computed(() => props.plan && "items" in props.plan ? props.plan : undefined);
const refusal = computed(() => (props.plan && "refused" in props.plan ? refusalText(props.plan.refused) : ""));
/** Who needs a block the selection brings along: „potřebuje ho: Portál: aplikace“. */
const neededBy = (key: string | null) => (key && byKey.value.get(key)?.title) ?? key ?? "";
const ALSO = 8;
/** What a selection of modules brings, by module: „Core (10)“. */
const addedModules = computed(() => {
  const counts = new Map<CatalogModule, number>();
  for (const item of plan.value?.added ?? []) { const module = moduleOf.value.get(item.definition.key); if (module) counts.set(module, (counts.get(module) ?? 0) + 1); }
  return [...counts].map(([module, size]) => `${moduleName(module)} (${size})`);
});

defineExpose({ focus: () => input.value?.focus(), clear: () => { query.value = ""; } });
onMounted(() => {
  const ahead = props.typeAhead?.take();
  if (!ahead?.text) return;
  query.value = ahead.text;
  if (ahead.enter) void nextTick(() => submit(false));
});
// The search takes the keys at once, also while the catalog loads (the slash pick types on): the list ranks what was typed when it comes.
watch(() => props.status, status => { if (status !== "error" && props.autofocus) void nextTick(() => input.value?.focus()); }, { immediate: true });
</script>

<template>
  <div class="catalog-picker" :class="{ 'is-tree': tree }" data-catalog-picker>
    <label class="catalog-picker-query">
      <AtlasIcon name="search" :size="16" />
      <input ref="input" v-model="query" role="combobox" :aria-label="t.picker.searchLabel(label)" aria-autocomplete="list" aria-expanded="true" :aria-controls="`${uid}-list`"
        :aria-activedescendant="lit && active ? `${uid}-${active}` : undefined" :aria-describedby="`${uid}-keys`" :placeholder="modulesOnly ? t.picker.searchModulePlaceholder : t.picker.searchRequirementPlaceholder"
        autocomplete="off" spellcheck="false" :disabled="busy" data-catalog-query :data-md-own-escape="ownEscape(query)" @keydown="onKeydown" />
    </label>
    <p :id="`${uid}-keys`" class="catalog-picker-keys"><kbd>{{ t.picker.keySpace }}</kbd> {{ t.picker.selects }} · <kbd>Enter</kbd> {{ modulesOnly && !tree ? t.picker.creates : t.picker.inserts }}<template v-if="!modulesOnly || tree"> · <kbd>Ctrl+Enter</kbd> {{ t.picker.insertsContinues }}</template> · <kbd>Ctrl+A</kbd> {{ t.picker.all }}</p>
    <LoadingSkeleton v-if="status === 'loading'" :rows="5" row-height="41px" :label="t.picker.loading" class="catalog-picker-loading" />
    <p v-else-if="status === 'error'" class="catalog-picker-error" role="alert">{{ error }}</p>
    <div v-else-if="tree && shown.length" class="catalog-picker-panes">
      <ul :id="`${uid}-list`" ref="list" class="catalog-picker-list atlas-stagger" role="listbox" aria-multiselectable="true" :aria-label="label" data-catalog-list
        data-testid="requirement-catalog-list" @scroll="onScroll">
        <li v-for="row in display" :id="`${uid}-${row.id}`" :key="row.id" role="option" class="catalog-picker-row" :class="row.heading ? `is-${row.heading}` : row.module ? 'is-leaf' : 'is-requirement'"
          :aria-selected="checked(row)" :aria-disabled="!enabled(row) || undefined" :data-active="(lit && row.id === active) || undefined" :data-mixed="mixed(row) || undefined"
          :data-catalog-row="row.id" @mousedown.prevent @click="toggle(row)">
          <span class="catalog-picker-box" aria-hidden="true"><AtlasIcon :name="mixed(row) ? 'minus' : 'check'" :size="12" /></span>
          <AtlasIcon :name="row.heading === 'product' ? 'box' : row.heading === 'category' ? 'folder' : row.module ? 'layout-dashboard' : 'list-checks'" :size="16" class="catalog-picker-icon" />
          <span class="catalog-picker-text"><span class="catalog-picker-title">{{ row.title }}</span><small v-if="row.description">{{ row.description }}</small></span>
        </li>
      </ul>
      <aside class="catalog-picker-preview" data-catalog-preview>
        <!-- Keyed, so each new row's preview rises in (riseIn, not a Transition: it never waits for a frame or the old one to leave). -->
        <div v-if="preview" :key="preview.row.id" ref="previewBody" class="catalog-preview-body">
            <header class="catalog-preview-head">
              <span v-if="preview.path" class="catalog-preview-path" data-catalog-preview-path>{{ preview.path }}</span>
              <strong>{{ preview.title }}</strong>
              <span>{{ preview.coming ? t.picker.coming(preview.coming) : t.picker.nothingComing }}<template v-if="preview.present"> · {{ t.picker.onPage(preview.present) }}</template></span>
            </header>
            <section v-for="entry in preview.modules" :key="entry.module.id" class="catalog-preview-module">
              <h4 v-if="preview.row.heading">{{ entry.name }}</h4>
              <p v-if="entry.summary && preview.modules.length === 1" class="catalog-preview-summary" data-catalog-preview-summary>{{ entry.summary }}</p>
              <ul>
                <li v-for="item in entry.items" :key="item.key" :data-status="item.status" :data-current="item.key === preview.row.id || undefined" data-catalog-preview-item>
                  <AtlasIcon :name="item.status === 'new' ? 'plus' : item.status === 'present' ? 'check' : 'ban'" :size="13" />
                  <span class="catalog-preview-title">{{ item.title }}</span>
                  <span class="catalog-preview-tag">{{ STATUS[item.status] }}</span>
                  <small>{{ item.direction }}</small>
                </li>
              </ul>
            </section>
            <p v-if="preview.more" class="catalog-preview-note">{{ t.picker.andMoreModules(preview.more) }}</p>
            <div v-if="preview.along.length" class="catalog-preview-along" data-catalog-preview-along>
              <b><AtlasIcon name="link" :size="12" />{{ t.picker.alongTitle }}</b>
              <p v-for="item in preview.along" :key="item.key">{{ item.title }} <small v-if="item.why">— {{ t.picker.because(item.why) }}</small></p>
              <p v-if="preview.alongMore"><small>{{ t.picker.andMore(preview.alongMore) }}</small></p>
            </div>
          </div>
      </aside>
    </div>
    <ul v-else-if="shown.length" :id="`${uid}-list`" ref="list" class="catalog-picker-list atlas-stagger" role="listbox" aria-multiselectable="true" :aria-label="label" data-catalog-list data-testid="requirement-catalog-list" @scroll="onScroll">
      <li v-for="(group, index) in groups" :key="`${index}:${group.category}`" role="group" :aria-label="group.category || undefined" class="catalog-picker-group">
        <span v-if="group.category" class="catalog-picker-category" aria-hidden="true" data-catalog-category>{{ group.category }}</span>
        <ul role="none" class="catalog-picker-rows">
          <li v-for="row in group.rows" :id="`${uid}-${row.id}`" :key="row.id" role="option" class="catalog-picker-row" :class="{ 'is-module': row.module && !modulesOnly }"
            :aria-selected="checked(row)" :aria-disabled="!enabled(row) || undefined" :data-active="(lit && row.id === active) || undefined" :data-mixed="mixed(row) || undefined"
            :data-catalog-row="row.id" @mousedown.prevent @click="toggle(row)">
            <span class="catalog-picker-box" aria-hidden="true"><AtlasIcon :name="mixed(row) ? 'minus' : 'check'" :size="12" /></span>
            <AtlasIcon :name="row.module ? 'layout-dashboard' : 'list-checks'" :size="16" class="catalog-picker-icon" />
            <span class="catalog-picker-text"><span class="catalog-picker-title">{{ row.title }}</span><small v-if="row.description">{{ row.description }}</small></span>
          </li>
        </ul>
      </li>
    </ul>
    <p v-else class="catalog-picker-empty" role="status"><AtlasIcon name="search" :size="16" />{{ emptyText ?? t.picker.empty }}</p>
    <div class="catalog-picker-plan" aria-live="polite">
      <p v-if="plan" class="catalog-picker-summary" data-catalog-summary>{{ planSummary(plan) }}</p>
      <p v-if="modulesOnly && plan?.added.length" class="catalog-also" data-catalog-also>{{ t.picker.alsoAdded }}: {{ addedModules.join(", ") }}</p>
      <div v-else-if="plan?.added.length" class="catalog-also" data-catalog-also>
        <p>{{ t.picker.alsoAdded }}: {{ plan.added.slice(0, 3).map(item => item.definition.title).join(", ") }}{{ plan.added.length > 3 ? ` ${t.picker.andMore(plan.added.length - 3)}` : "" }}</p>
        <ul>
          <li v-for="item in plan.added.slice(0, ALSO)" :key="item.definition.key"><AtlasIcon name="link" :size="12" /><b>{{ item.definition.title }}</b> · {{ t.picker.neededBy(neededBy(item.requiredBy)) }}</li>
          <li v-if="plan.added.length > ALSO">{{ t.picker.andMore(plan.added.length - ALSO) }}</li>
        </ul>
      </div>
      <ul v-if="plan?.warnings.length" class="catalog-warnings" data-catalog-warnings>
        <li v-for="warning in plan.warnings" :key="warning"><AtlasIcon name="info" :size="14" />{{ warning }}</li>
      </ul>
      <p v-if="refusal" class="catalog-picker-error" role="alert">{{ refusal }}</p>
    </div>
  </div>
</template>

<style scoped>
.catalog-picker { display: grid; gap: 8px; min-width: 0; }
.catalog-picker-query { display: flex; align-items: center; gap: 8px; padding: 0 10px; border: 1px solid var(--input-line, var(--line)); border-radius: var(--atlas-radius-md, 8px);
  background: var(--surface, #fff); color: var(--muted); transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.catalog-picker-query:focus-within { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
/* The caret takes the text's colour, never the editor field's light fill (J2). */
/* Over the editor popover's own field look (its :is() selector is as specific as a plain one): the label is the field. */
.catalog-picker .catalog-picker-query input { flex: 1; min-width: 0; height: 36px; padding: 0; border: 0; outline: 0; background: transparent; box-shadow: none; color: var(--text); caret-color: var(--text); font: inherit; }
.catalog-picker-keys { margin: 0; color: var(--muted); font-size: 11.5px; }
.catalog-picker-keys kbd { padding: 0 4px; border: 1px solid var(--line); border-radius: 4px; background: var(--canvas, transparent); font: inherit; font-size: 11px; }
.catalog-picker-list { display: grid; gap: 1px; max-height: min(320px, 45vh); margin: 0; padding: 2px; overflow-y: auto; overscroll-behavior: contain; list-style: none; }
.catalog-picker-row { display: flex; align-items: center; gap: 9px; min-height: 40px; padding: 5px 8px; border-radius: 7px; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
.catalog-picker-group, .catalog-picker-rows { display: grid; gap: 1px; margin: 0; padding: 0; list-style: none; }
.catalog-picker-category { padding: 8px 8px 2px; color: var(--muted); font-size: 11.5px; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; }
.catalog-picker-group:first-child > .catalog-picker-category { padding-top: 2px; }
.catalog-picker-row.is-module { font-weight: 600; }
.catalog-picker-row:not(.is-module) + .catalog-picker-row.is-module { margin-top: 4px; }
.catalog-picker-row[data-active] { background: var(--state-active-row-bg); outline: var(--state-focus-ring); outline-offset: -2px; }
.catalog-picker-row[aria-selected="true"] { color: var(--accent-ink, var(--accent)); }
.catalog-picker-row[aria-disabled="true"] { color: var(--muted); cursor: default; }
/* Already on the page: its box does not read as one to tick (PR-7). */
.catalog-picker-row[aria-disabled="true"] .catalog-picker-box { border-style: dashed; opacity: .55; }
@media (hover: hover) and (pointer: fine) { .catalog-picker-row:not([aria-disabled="true"]):hover { background: var(--hover, color-mix(in srgb, var(--text) 8%, transparent)); } }
.catalog-picker-box { display: grid; place-items: center; flex: none; width: 16px; height: 16px; border: 1.5px solid var(--input-line); border-radius: 4px; color: transparent;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
.catalog-picker-box :deep(svg) { scale: var(--st-pop-from, .6); transition: scale var(--atlas-motion-base) var(--atlas-ease-out); }
.catalog-picker-row:is([aria-selected="true"], [data-mixed]) .catalog-picker-box { border-color: var(--accent); background: var(--accent); color: var(--on-accent, #fff); }
.catalog-picker-row:is([aria-selected="true"], [data-mixed]) .catalog-picker-box :deep(svg) { scale: 1; }
.catalog-picker-icon { flex: none; color: var(--muted); }
.catalog-picker-text { display: grid; min-width: 0; }
.catalog-picker-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.catalog-picker-text small { overflow: hidden; color: var(--muted); font-size: 12px; font-weight: 400; text-overflow: ellipsis; white-space: nowrap; }
/* The skeleton of the rows to come: the picker's row (40 px, a 16 px box), so the list does not jump when the catalog arrives. */
.catalog-picker-loading :deep(.loading-skeleton-row) { padding: 5px 8px; }
.catalog-picker-loading :deep(.loading-skeleton-icon) { flex-basis: 16px; height: 16px; border-radius: 4px; }
.catalog-picker-empty { display: flex; align-items: center; gap: 8px; margin: 0; padding: 10px 4px; color: var(--muted); font-size: 13px; }
.catalog-picker-error { margin: 0; color: var(--error); }
.catalog-picker-plan { display: grid; gap: 6px; }
.catalog-picker-summary { margin: 0; font-weight: 600; }
.catalog-also p { margin: 0 0 4px; }
.catalog-also ul, .catalog-warnings { display: grid; gap: 2px; margin: 0; padding: 0; list-style: none; color: var(--text-dim, var(--muted)); font-size: 12px; }
.catalog-also li, .catalog-warnings li { display: flex; align-items: center; gap: 6px; }
.catalog-warnings li { align-items: flex-start; color: var(--text); }
.catalog-warnings :deep(svg) { flex: none; margin-top: 1px; color: var(--warning, #b7791f); }
/* „Moduly produktu“: the tree beside the preview of its highlighted row; one column on a phone, the preview under the list. */
.catalog-picker-panes { display: grid; grid-template-columns: minmax(0, 5fr) minmax(0, 6fr); gap: 12px; min-height: 0; }
.is-tree .catalog-picker-list { max-height: min(460px, 60vh); }
.catalog-picker-row.is-product { font-weight: 700; }
.catalog-picker-row.is-category { padding-left: 22px; font-weight: 600; }
.catalog-picker-row.is-leaf { padding-left: 40px; }
.catalog-picker-row.is-requirement { min-height: 34px; padding-left: 58px; font-size: 13px; }
.catalog-picker-row.is-product:not(:first-child) { margin-top: 6px; }
.catalog-picker-preview { min-width: 0; max-height: min(460px, 60vh); padding: 10px 12px; overflow-y: auto; overscroll-behavior: contain; border: 1px solid var(--line);
  border-radius: var(--atlas-radius-md, 8px); background: var(--canvas, transparent); }
.catalog-preview-body { display: grid; gap: 10px; }
.catalog-preview-head { display: grid; gap: 2px; }
.catalog-preview-head span, .catalog-preview-note { color: var(--muted); font-size: 12px; }
.catalog-preview-head strong { font-size: 15px; }
.catalog-preview-head .catalog-preview-path { font-size: 11.5px; letter-spacing: .02em; }
.catalog-preview-summary { margin: 0 0 8px; color: var(--text-dim, var(--muted)); font-size: 12.5px; line-height: 1.45; }
.catalog-preview-module h4 { margin: 0 0 4px; font-size: 12.5px; }
.catalog-preview-module ul { display: grid; gap: 2px; margin: 0; padding: 0; list-style: none; }
.catalog-preview-module li { display: grid; grid-template-columns: 16px minmax(0, 1fr) auto; column-gap: 6px; align-items: baseline; padding: 3px 4px; border-radius: 6px; }
.catalog-preview-module li[data-current] { background: var(--state-active-row-bg); }
.catalog-preview-tag { padding: 0 6px; border-radius: 999px; background: color-mix(in srgb, var(--accent) 14%, transparent); color: var(--accent-ink, var(--accent)); font-size: 11px; white-space: nowrap; }
.catalog-preview-module li:not([data-status="new"]) .catalog-preview-tag { background: color-mix(in srgb, var(--text) 8%, transparent); color: var(--muted); }
.catalog-preview-module li :deep(svg) { align-self: center; color: var(--accent); }
.catalog-preview-module li small { grid-column: 2; color: var(--muted); font-size: 11.5px; }
.catalog-preview-module li[data-status="present"], .catalog-preview-module li[data-status="dismissed"] { color: var(--muted); }
.catalog-preview-module li:not([data-status="new"]) :deep(svg) { color: var(--muted); }
.catalog-preview-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.catalog-preview-note { display: flex; align-items: center; gap: 6px; margin: 0; }
.catalog-preview-along { display: grid; gap: 2px; padding: 8px 10px; border-radius: 7px; background: color-mix(in srgb, var(--warning, #b7791f) 9%, transparent); font-size: 12.5px; }
.catalog-preview-along b { display: flex; align-items: center; gap: 6px; }
.catalog-preview-along p { margin: 0; }
.catalog-preview-along small { color: var(--muted); }
@media (max-width: 720px) { .catalog-picker-panes { grid-template-columns: minmax(0, 1fr); } .is-tree .catalog-picker-list, .catalog-picker-preview { max-height: 38vh; } }
</style>
