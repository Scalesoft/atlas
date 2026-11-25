<script setup lang="ts">
import { computed, h, nextTick, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import { errorMessage, post } from '../api';
import { formatDateTime, formatShortDate } from '../formatDate';
import t from '../i18n/components-spaces';
import { formatNumber } from '../i18n';
import { isPickEnter } from '../lib/pickEnter';
import { startObjectDrag } from '../boards/boardDrop';
import { detail, hideObjectDetail, openDetailId, showObjectDetail } from '../objects/objectDetail';
import { plainTitle, relationsApi, typeInfo, type ContentRelation, type ContentRow, type ContentSummary, type DetailRow, type RelationsApi, type UsageApi } from '../objects/relationsApi';
import UsagePanel from './objects/UsagePanel.vue';
import { useRelations } from '../objects/useRelations';
import { publishRequest } from '../publishing/publication';
import { contentItemLink, isPlainNavigationClick } from '../pageLinks';
import { isBoardNode, type TreeNodeType, type TreePage } from '../treeMove';
import AtlasIcon from './AtlasIcon.vue';
import PageIcon from './PageIcon.vue';
import LoadingRegion from './LoadingRegion.vue';
import ErrorState from './settings/ErrorState.vue';
import OptionPills from './settings/OptionPills.vue';
import StatTile from './settings/StatTile.vue';
import EmptyState from './settings/EmptyState.vue';
import FilterMenu from './settings/FilterMenu.vue';
import BulkSelectBar from './settings/BulkSelectBar.vue';
import { useBulkSelection } from './settings/bulkSelection';
import SearchField from './settings/SearchField.vue';
import type { ChoiceOption } from './settings/types';
import { disclosureHooks } from './settings/disclosure';
import { Button } from './ui/button';
import { Checkbox } from './ui/checkbox';
import RowActionsMenu from './RowActionsMenu.vue';
import type { ActionMenuItem } from './ActionMenu.vue';
import LoadingSkeleton from './LoadingSkeleton.vue';
import { lazyComponent } from '../lazyComponent';
import { rovingInit, rowKeys } from '../keyboard/navigation';
// The rows enter one after another with the settings design system's step (--st-stagger).
import './settings/settings.css';

// The graph (Vue Flow and the board canvas) loads when it is first shown, not with the list. While its chunk comes it is the region's
// own skeleton again (one canvas-high row, without a second fade-in), so the placeholder does not blink.
const RelationsGraph = lazyComponent(() => import('./objects/RelationsGraph.vue'), {
  loading: () => h(LoadingSkeleton, { rows: 1, rowHeight: 'var(--content-graph-height)', instant: true }),
});

/**
 * Obsah of a space (Atlas 3): every object of the space as one searchable table,
 * read from GET /api/spaces/{id}/contents, with tiles per type and per relation flag, a Vztah filter, the columns
 * Použito · Odkazy · Komentáře · Změněno, the dock with an object's Vazby and Použito na (also unfolded under its row: UsagePanel), and the switch to the graph of
 * its relations (BoardCanvas, read-only). Pages, boards (with the module Boards) and contacts are rows of the same list: no section of
 * their own, a type tile is enough. Obsah replaces the old Stránky tab: that one knew pages and boards from the tree and nothing of
 * what leads where, so a contact placed nowhere, a board nobody links and a link to a page in the trash were invisible.
 * Writers tick drafts (the pages of the tree `pages` knows) and publish them in one go: the selection of every list (BulkSelectBar), no mode.
 */
const props = defineProps<{ pages: TreePage[]; canWrite: boolean; initialType?: '' | TreeNodeType; spaceId: string; relations?: RelationsApi; usage?: UsageApi }>();
const emit = defineEmits<{ open: [id: string, type: TreeNodeType]; published: []; refresh: []; /** What the type tiles add up to: the tab shows it. */ counted: [count: number] }>();
const published = ref(new Set<string>());
const busy = ref(false), completed = ref(0), total = ref(0), error = ref('');
const query = ref('');
const relation = ref<ContentRelation | ''>('');
// The rows (below); declared here because the watchers that reset on a filter read them while the setup runs.
const contentRows = shallowRef<ContentRow[]>([]);
const summary = shallowRef<ContentSummary | null>(null);
const nextCursor = ref<string | null>(null);
// The type filter is an object type (`atlas.page`); the address of a space's boards asks for `board`.
const type = ref<string>(props.initialType ? `atlas.${props.initialType}` : '');
const byId = computed(() => new Map(props.pages.map(item => [item.id, item])));
const isLive = (item: TreePage) => item.isPublished || published.value.has(item.id);
const drafts = computed(() => props.pages.filter(item => !isLive(item)));
// The list holds objects of every type; only a page of the tree can be a draft.
const visibleDrafts = computed(() => contentRows.value.map(row => byId.value.get(row.id)).filter((item): item is TreePage => !!item && !isBoardNode(item) && !isLive(item)));
const filtered = computed(() => !!query.value.trim() || !!type.value || !!relation.value);
// Nothing unused and no name looked for: good news, not a failed search.
const allUsed = computed(() => relation.value === 'unused' && !query.value.trim());
// Ticking a shown draft starts the selection; its column stands while the space has drafts, so ticking moves nothing.
const pickable = computed(() => props.canWrite && drafts.value.length > 0);
const selection = useBulkSelection(() => (props.canWrite ? visibleDrafts.value.map(item => item.id) : []));
// In the tree's order (a parent before its subpages), as publishing always went.
const chosen = computed(() => drafts.value.filter(item => selection.has(item.id)));
const inSpace = computed(() => Object.values(summary.value?.types ?? {}).reduce((sum, count) => sum + count, 0));
watch(summary, counts => { if (counts) emit('counted', inSpace.value); });
const shownText = computed(() => {
  return t.content.shown(formatNumber(contentRows.value.length) + (nextCursor.value ? '+' : ''), inSpace.value ? formatNumber(inSpace.value) : '', relation.value === 'mostUsed');
});
watch([query, type, relation], () => selection.clear());
const loadMore = () => { void load(true); };
let disposed = false;
onBeforeUnmount(() => { disposed = true; clearTimeout(typingTimer); request?.abort(); hideObjectDetail(); });
watch(() => props.pages, () => {
  // A publication or a move changed what Obsah counts.
  if (loaded.value) void load();
});

// ---- The objects of the space, read from the server (spec §4, §13.3).
const loading = ref(false), loaded = ref(false), loadError = ref('');
const needle = ref('');
/** The object whose neighbourhood the graph draws: the one picked in the list or asked for, else the first row (preselected). */
const subjectId = ref('');
const root = ref<HTMLElement>();
/** The row Enter opens: the first one while something is typed, ↑/↓ move it. */
const active = ref(0);
let request: AbortController | undefined, typingTimer: ReturnType<typeof setTimeout> | undefined;
let enterPending = false;
async function load(more = false) {
  request?.abort();
  const current = request = new AbortController();
  loading.value = true; loadError.value = '';
  try {
    const page = await (props.relations ?? relationsApi).contents(props.spaceId, { type: type.value, relation: relation.value, q: needle.value, cursor: more ? nextCursor.value : null }, current.signal);
    if (current.signal.aborted) return;
    contentRows.value = more ? [...contentRows.value, ...page.items] : page.items;
    nextCursor.value = page.nextCursor;
    // The tiles count the whole space: only an unfiltered read renews them.
    if (!summary.value || !(type.value || relation.value || needle.value)) summary.value = page.summary;
    loaded.value = true;
    if (!more) active.value = 0;
    if (enterPending) { enterPending = false; const first = contentRows.value[0]; if (first) openRow(undefined, first); }
  } catch (failure) {
    if (!current.signal.aborted) { loadError.value = errorMessage(failure); enterPending = false; }
  } finally {
    if (!current.signal.aborted) loading.value = false;
  }
}
// Typing asks the server after a short pause; Enter does not wait for it (`searchKeys`).
watch(query, text => { enterPending = false; clearTimeout(typingTimer); typingTimer = setTimeout(() => { needle.value = text.trim(); }, 200); });
watch([needle, type, relation, () => props.spaceId], () => { subjectId.value = ''; void load(); }, { immediate: true });
watch(contentRows, () => { if (active.value >= contentRows.value.length) active.value = 0; });
const typeIds = computed(() => {
  // Until the summary is here, the types the tree already knows, so the pills stand in the first frame (F3).
  const known = summary.value ? Object.keys(summary.value.types) : [...new Set(props.pages.map(item => (isBoardNode(item) ? 'atlas.board' : 'atlas.page')))];
  const order = ['atlas.page', 'atlas.board', 'atlas.contact'];
  return [...order.filter(id => known.includes(id)), ...known.filter(id => !order.includes(id)).sort()];
});
const typePills = computed<ChoiceOption[]>(() => [{ value: '', label: t.content.all, icon: 'list' }, ...typeIds.value.map(id => ({ value: id, label: typeInfo(id).plural, icon: typeInfo(id).icon }))]);
const relationOptions = (): ChoiceOption[] => [
  { value: '', label: t.content.all, icon: 'list' },
  { value: 'unused', label: t.content.unused, icon: 'layers' },
  { value: 'noIncoming', label: t.content.noIncoming, icon: 'arrow-left' },
  { value: 'broken', label: t.content.broken, icon: 'triangle-alert' },
  { value: 'attached', label: t.content.attached, icon: 'workflow' },
  { value: 'mostUsed', label: t.content.mostUsed, icon: 'star' },
];
/** The tiles: a count per type and per relation flag; a click filters, the same click again clears. */
const tiles = computed(() => {
  const counts = summary.value;
  if (!counts) return [];
  const total = typeIds.value.reduce((sum, id) => sum + (counts.types[id] ?? 0), 0);
  const flags = [
    { key: 'unused', label: t.content.unused, icon: 'layers', hint: t.content.unusedHint },
    { key: 'noIncoming', label: t.content.noIncoming, icon: 'arrow-left', hint: t.content.noIncomingHint },
    { key: 'broken', label: t.content.broken, icon: 'triangle-alert', hint: t.content.brokenHint },
    { key: 'attached', label: t.content.attached, icon: 'workflow', hint: t.content.attachedHint },
  ] as const;
  return [
    ...typeIds.value.map(id => ({ key: `type:${id}`, label: typeInfo(id).plural, icon: typeInfo(id).icon, value: counts.types[id] ?? 0, tone: undefined, hint: t.content.byKind, active: type.value === id, toggle: () => { type.value = type.value === id ? '' : id; } })),
    // A flag that holds every object tells nothing as a filter (e.g. no links yet); a broken one is always worth showing.
    ...flags.filter(flag => flag.key === 'broken' || relation.value === flag.key || (counts.relations[flag.key] ?? 0) < total).map(flag => ({ key: `relation:${flag.key}`, label: flag.label, icon: flag.icon, value: counts.relations[flag.key] ?? 0, tone: flag.key === 'broken' && counts.relations.broken > 0 ? 'danger' as const : undefined,
      hint: flag.hint, active: relation.value === flag.key, toggle: () => { relation.value = relation.value === flag.key ? '' : flag.key; } })),
  ];
});
const pathOf = (row: ContentRow) => (row.hostId && row.path ? t.content.belongsTo(row.path) : row.path ?? '');
const openable = (row: ContentRow) => row.typeId === 'atlas.page' || row.typeId === 'atlas.board';
const nodeType = (row: ContentRow): TreeNodeType => (row.typeId === 'atlas.board' ? 'board' : 'page');
const rowLink = (row: ContentRow) => contentItemLink({ id: row.id, type: nodeType(row) });
const isDraftRow = (row: ContentRow) => { const item = byId.value.get(row.id); return !!item && !isBoardNode(item) && !isLive(item); };
const draggedRow = (row: ContentRow) => ({ id: row.id.toLowerCase(), typeId: row.typeId, title: plainTitle(row.title) });
/** A page's own icon (decoration) when the tree knows it, else the type's. */
const decorationOf = (row: ContentRow) => (row.typeId === 'atlas.page' ? byId.value.get(row.id)?.decoration : undefined);

// A row opens the object (the title) or the dock (anywhere else); an object with no page of its own (a contact) shows its dock.
function openRow(event: MouseEvent | undefined, row: ContentRow) {
  if (event && !isPlainNavigationClick(event)) return;
  event?.preventDefault();
  if (openable(row)) emit('open', row.id, nodeType(row)); else showDetail(row);
}
function onRowClick(event: MouseEvent, row: ContentRow) {
  if ((event.target as Element).closest('a, button, input, label')) return;
  showDetail(row);
}
function searchKeys(event: KeyboardEvent) {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    if (!contentRows.value.length) return;
    event.preventDefault();
    active.value = Math.min(contentRows.value.length - 1, Math.max(0, active.value + (event.key === 'ArrowDown' ? 1 : -1)));
    void nextTick(() => table.value?.querySelector('.content-row.is-active')?.scrollIntoView({ block: 'nearest' }));
    return;
  }
  if (!isPickEnter(event) || !query.value.trim()) return;
  event.preventDefault();
  // Typed and pressed at once, or an answer still running: the rows are the previous text's, so the first of the answer is what opens.
  if (query.value.trim() !== needle.value) { clearTimeout(typingTimer); needle.value = query.value.trim(); enterPending = true; return; }
  if (loading.value) { enterPending = true; return; }
  const row = contentRows.value[active.value];
  if (row) openRow(undefined, row);
}
// „Použito na“ unfolds under its row (UsagePanel); one at a time, and a new filter folds it.
const expandedId = ref('');
const toggleUsage = (row: ContentRow, open = expandedId.value !== row.id) => { expandedId.value = open ? row.id : ''; };
watch([needle, type, relation, () => props.spaceId], () => { expandedId.value = ''; });
// ↑ ↓ Home End walk the rows as in every list (rowKeys); → opens the dock of the row; Alt+↓ / Alt+↑ unfold and fold its usage.
function contentKeys(event: KeyboardEvent) {
  if (event.altKey && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
    const title = (event.target as Element).closest?.<HTMLElement>('.space-page-title');
    const at = title ? [...(table.value?.querySelectorAll<HTMLElement>('.content-row .space-page-title') ?? [])].indexOf(title) : -1;
    const row = contentRows.value[at];
    if (row) { event.preventDefault(); toggleUsage(row, event.key === 'ArrowDown'); }
    return;
  }
  if (event.key !== 'ArrowRight') return rowKeys(event, '.content-row');
  const title = (event.target as Element).closest?.<HTMLElement>('.space-page-title');
  const at = title ? [...(table.value?.querySelectorAll<HTMLElement>('.content-row .space-page-title') ?? [])].indexOf(title) : -1;
  const row = contentRows.value[at];
  if (row) { event.preventDefault(); showDetail(row); }
}

// ---- The dock: Přehled · Vazby · Kde je použito · Diskuse · Historie of one object, the dock of the board's cards.
const detailHandlers = {
  api: props.relations,
  usage: props.usage,
  onOpen: (id: string, typeId: string) => {
    const row = contentRows.value.find(item => item.id.toLowerCase() === id.toLowerCase());
    if (row) openRow(undefined, row);
    else if (typeId === 'atlas.page' || typeId === 'atlas.board') emit('open', id, typeId === 'atlas.board' ? 'board' : 'page');
  },
  onFocus: (id: string) => { subjectId.value = id; subjectNode.value = detail.row?.id === id ? { ...detail.row } : null; view.value = 'graph'; },
};
/** `focus` false keeps the keyboard where it is (walking the graph with the arrows shows each node's dock). */
const showDetail = (row: DetailRow, focus = true) => showObjectDetail(row, detailHandlers, focus);

// ---- Seznam / Vazby: the same objects as a graph of what leads where.
const view = ref<'list' | 'graph'>('list');
const viewOptions = (): ChoiceOption[] => [{ value: 'list', label: t.content.viewList, icon: 'list' }, { value: 'graph', label: t.content.viewGraph, icon: 'network' }];
const depth = ref<'1' | '2'>('1');
const depthOptions = (): ChoiceOption[] => [{ value: '1', label: t.content.step1, icon: 'arrow-right' }, { value: '2', label: t.content.step2, icon: 'workflow' }];
/** A node of the graph the list has no row for, when the dock asked for its neighbourhood. */
const subjectNode = shallowRef<DetailRow | null>(null);
watch(view, now => { if (now === 'list') subjectId.value = ''; });
// Asked for, else the object whose dock is open, else the one with the most links (the first row is often one nothing leads to).
const busiest = computed(() => contentRows.value.reduce<ContentRow | undefined>((best, row) => (!best || row.linksIn + row.linksOut > best.linksIn + best.linksOut ? row : best), undefined));
const subject = computed<DetailRow | undefined>(() => {
  const docked = openDetailId();
  return contentRows.value.find(row => row.id === subjectId.value) ?? (subjectNode.value?.id === subjectId.value ? subjectNode.value : undefined)
    ?? (docked ? contentRows.value.find(row => row.id === docked) ?? (detail.row?.id === docked ? detail.row : undefined) : undefined) ?? busiest.value;
});
const { graph, loading: graphLoading, failed: graphFailed, reload: reloadGraph } = useRelations(() => (view.value === 'graph' ? subject.value?.id : null), () => (depth.value === '2' ? 2 : 1), props.relations);
// The branch of the tree an object lives under, for folding a big graph into groups.
const branches = computed(() => new Map(contentRows.value.filter(row => row.path).map(row => [row.id.toLowerCase(), row.path!.split(' › ')[0]!])));
const branchOf = (node: { id: string }) => branches.value.get(node.id.toLowerCase());
function graphSelect(id: string) {
  const known = contentRows.value.find(row => row.id.toLowerCase() === id);
  const node = graph.value?.nodes.find(item => item.id.toLowerCase() === id);
  // A click in the graph shows the dock but keeps the graph where it is.
  if (!subjectId.value && subject.value) { subjectId.value = subject.value.id; subjectNode.value = subject.value; }
  if (known) showDetail(known, false);
  else if (node) showDetail({ id: node.id, typeId: node.typeId, title: node.title ?? '', spaceId: node.spaceId }, false);
}
function graphOpen(id: string, typeId: string) {
  if (typeId === 'atlas.page' || typeId === 'atlas.board') emit('open', id, typeId === 'atlas.board' ? 'board' : 'page');
}
function graphBack() { root.value?.querySelector<HTMLElement>('[data-content-view="graph"]')?.focus(); }
const table = ref<HTMLElement>();
/* One Tab stop for the whole list (SH2-14): ↑ ↓ Home End walk the rows and Space ticks a draft (rowKeys), the stop follows the focus. */
const TITLES = '.space-page-title';
watch([table, contentRows], () => rovingInit(table.value, TITLES), { flush: 'post' });
function roveTo(event: FocusEvent) {
  const title = event.target instanceof HTMLElement && event.target.matches(TITLES) ? event.target : null;
  if (!title) return;
  for (const other of table.value?.querySelectorAll<HTMLElement>(TITLES) ?? []) other.tabIndex = other === title ? 0 : -1;
}
/** „Co s tím jde dělat“ of a row (P2, RowActionsMenu): what the row's title, its detail and usage buttons and its box do. */
const rowMenu = (row: ContentRow): ActionMenuItem[] => [
  ...(openable(row) ? [{ id: 'open', label: t.content.menuOpen, icon: 'file-text' }] : []),
  { id: 'detail', label: t.content.menuDetail, icon: 'info' },
  { id: 'usage', label: t.content.menuUsage, icon: 'layers', checked: expandedId.value === row.id },
  ...(props.canWrite && isDraftRow(row) ? [{ id: 'pick', label: t.content.menuPick, icon: 'check', checked: selection.has(row.id), disabled: busy.value }] : []),
];
function rowAction(row: ContentRow, id: string) {
  if (id === 'open') openRow(undefined, row);
  else if (id === 'detail') showDetail(row);
  else if (id === 'usage') toggleUsage(row);
  else if (id === 'pick') selection.toggle(row.id);
}
async function publish() {
  if (busy.value || !props.canWrite || !chosen.value.length) return;
  const batch = chosen.value.map(item => ({ ...item }));
  busy.value = true; error.value = ''; completed.value = 0; total.value = batch.length;
  try {
    for (const item of batch) {
      if (disposed || !props.canWrite) break;
      try {
        // Unsaved live edits refuse, never published without (UX EP2-8).
        await post(`/pages/${item.id}/publish`, publishRequest(item.version, null, true));
        published.value.add(item.id);
        selection.toggle(item.id, false);
        completed.value++;
      } catch (reason) {
        error.value = t.content.publishFailed(item.title, errorMessage(reason));
        break;
      }
    }
  } finally {
    busy.value = false;
    if (!disposed && completed.value) emit('published');
  }
}
</script>

<template>
  <div ref="root" class="space-pages settings-ui" :aria-busy="busy">
    <!-- Obsah: a tile per type and per relation flag; a click filters, the same click clears. -->
    <div v-if="tiles.length" class="content-tiles atlas-stagger" role="group" :aria-label="t.content.summaryLabel">
      <StatTile v-for="tile in tiles" :key="tile.key" class="content-tile" :class="{ 'is-active': tile.active }" role="button" tabindex="0" :aria-pressed="tile.active"
        :label="tile.label" :value="formatNumber(tile.value)" :icon="tile.icon" :tone="tile.tone" :data-content-tile="tile.key"
        @click="tile.toggle()" @keydown.enter.prevent="tile.toggle()" @keydown.space.prevent="tile.toggle()">{{ tile.active ? t.content.clearFilter : tile.hint }}</StatTile>
    </div>
    <!-- The summary on its way: the same tiles, blank, hold the row's height from the first frame (F3: the list was pushed ~113 px). -->
    <div v-else-if="!loadError" class="content-tiles is-loading" aria-hidden="true" data-content-tiles-loading>
      <StatTile v-for="n in 5" :key="n" class="content-tile" :label="' '" :value="' '">&nbsp;</StatTile>
    </div>
    <div class="space-list-head">
      <div class="space-list-controls" role="search" :aria-label="t.content.filterLabel">
        <SearchField v-model="query" :label="t.content.search" :placeholder="t.content.searchPlaceholder" :disabled="busy" data-content-search @keydown="searchKeys" />
        <OptionPills v-model="type" :options="typePills" :label="t.content.type" hide-label class="content-type-pills" />
        <FilterMenu v-model="relation" :options="relationOptions()" :label="t.content.relation" icon="network" data-content-relation />
        <OptionPills v-model="view" segmented class="content-view-switch" :options="viewOptions()" :label="t.content.viewLabel" hide-label />
      </div>
      <BulkSelectBar v-if="view === 'list'" class="content-bulk" :selection="selection" :disabled="busy">
        <Button data-publish variant="default" size="sm" :disabled="busy || !chosen.length" @click="publish"><AtlasIcon name="published" :size="15" />{{ busy ? t.content.publishing(completed, total) : t.content.publishSelected(chosen.length) }}</Button>
        <p class="content-bulk-note">{{ t.content.publishNote }}</p>
      </BulkSelectBar>
    </div>
    <!-- The notes unfold in height and opacity (the disclosure hooks); the page is a flex column so the gap above them unfolds with them. -->
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="total" role="status" class="space-list-note">{{ t.content.publishProgress(busy, completed, total) }}</p>
    </Transition>
    <Transition :css="false" v-bind="disclosureHooks">
      <div v-if="error" role="alert" class="space-list-note is-error"><AtlasIcon name="triangle-alert" :size="15" />{{ error }} <Button variant="outline" size="sm" :disabled="busy" @click="emit('refresh')">{{ t.content.refreshList }}</Button></div>
    </Transition>

      <ErrorState v-if="loadError" :title="t.content.loadFailed" :text="loadError" :busy="loading" @retry="load()" />
      <LoadingRegion v-else :loading="loading" :ready="loaded" :rows="6" :label="t.content.loading" class="content-region">
        <Transition name="content-view" mode="out-in">
          <div v-if="view === 'list'" key="list" class="content-view">
            <div v-if="contentRows.length" ref="table" class="space-page-table is-obsah" :class="{ 'is-pickable': pickable, 'bulk-selecting': selection.count > 0 }" role="table" :aria-label="t.content.tableLabel" @keydown="contentKeys" @focusin="roveTo">
              <div class="space-page-head" role="row">
                <span v-if="pickable" role="columnheader" class="space-head-pick"><span class="sr-only">{{ t.content.colSelect }}</span></span>
                <span role="columnheader" class="space-head-main">{{ t.content.colContent }}</span>
                <span role="columnheader" class="content-head-used">{{ t.content.colUsed }}</span>
                <span role="columnheader" class="content-head-links">{{ t.content.colLinks }}</span>
                <span role="columnheader" class="content-head-comments">{{ t.content.colComments }}</span>
                <span role="columnheader" class="content-head-changed">{{ t.content.colChanged }}</span>
              </div>
              <template v-for="(row, index) in contentRows" :key="row.id">
              <div class="space-page-row content-row bulk-row" :class="{ 'is-active': index === active && !!query.trim(), 'is-open': openDetailId() === row.id }"
                role="row" :aria-current="openDetailId() === row.id || undefined" data-row-actions draggable="true" :data-content-id="row.id" :style="{ '--i': Math.min(index % 100, 8) }"
                @click="onRowClick($event, row)" @dragstart="startObjectDrag($event, draggedRow(row))">
                <span v-if="pickable" role="cell" class="page-selection"><Checkbox v-if="isDraftRow(row)" class="bulk-box" tabindex="-1" :model-value="selection.has(row.id)" :aria-label="t.content.select(plainTitle(row.title))" :disabled="busy" @update:model-value="selection.toggle(row.id)" /></span>
                <span role="cell" class="space-page-main">
                  <PageIcon v-if="decorationOf(row)?.icon" :decoration="decorationOf(row)!" /><AtlasIcon v-else :name="typeInfo(row.typeId).icon" :size="16" />
                  <span class="content-title-block">
                    <component :is="openable(row) ? 'a' : 'button'" class="space-page-title" :href="openable(row) ? rowLink(row) : undefined" :type="openable(row) ? undefined : 'button'"
                      :title="plainTitle(row.title)" :data-content-row="row.typeId.replace(/^atlas./, '')" @click="openRow($event, row)">{{ plainTitle(row.title) || t.content.untitled }}</component>
                    <span v-if="pathOf(row) || isDraftRow(row)" class="content-sub" :title="pathOf(row) || undefined">{{ pathOf(row) }}<template v-if="isDraftRow(row)">{{ pathOf(row) ? ' · ' : '' }}{{ t.content.draft }}</template></span>
                  </span>
                  <span v-if="row.scope === 'wiki'" class="content-scope" data-content-scope>{{ t.content.fromWiki }}</span>
                  <!-- Its room is reserved; it shows on hover and focus, always on a touch screen. -->
                  <button type="button" class="content-row-detail" tabindex="-1" :aria-label="t.content.detail(plainTitle(row.title))" data-content-detail @click.stop="showDetail(row)"><AtlasIcon name="info" :size="15" /></button>
                </span>
                <span class="content-counts" role="presentation">
                  <span role="cell" class="content-used" :class="{ 'is-zero': !row.usedCount }"
                    ><button type="button" class="content-used-toggle" tabindex="-1" :aria-expanded="expandedId === row.id" aria-keyshortcuts="Alt+ArrowDown"
                      :aria-label="t.content.usedAria(row.usedCount)" :title="t.content.usedTitle(row.usedCount)" data-content-usage @click.stop="toggleUsage(row)"
                      ><AtlasIcon name="layers" :size="12" />{{ row.usedCount }}</button></span>
                  <span role="cell" class="content-links" :class="{ 'is-zero': !row.linksIn && !row.linksOut }" :aria-label="t.content.linksAria(row.linksIn, row.linksOut)" :title="t.content.linksTitle(row.linksIn, row.linksOut)">
                    <AtlasIcon name="arrow-left" :size="12" />{{ row.linksIn }}<AtlasIcon name="arrow-right" :size="12" />{{ row.linksOut }}</span>
                  <span role="cell" class="content-comments" :class="{ 'is-zero': !row.comments }" :aria-label="t.content.commentsLabel(row.comments)" :title="t.content.commentsLabel(row.comments)"><AtlasIcon name="quote" :size="12" />{{ row.comments }}</span>
                </span>
                <RowActionsMenu :items="rowMenu(row)" :label="t.content.rowActions(plainTitle(row.title))" @select="rowAction(row, $event)" />
                <time role="cell" class="content-changed" :datetime="row.updatedAt" :title="formatDateTime(row.updatedAt)">{{ formatShortDate(row.updatedAt) }}</time>
              </div>
              <Transition :css="false" v-bind="disclosureHooks">
                <div v-if="expandedId === row.id" :key="`usage:${row.id}`" class="content-usage" role="row" data-content-usage-row>
                  <div role="cell" class="content-usage-cell"><UsagePanel :object-id="row.id" :api="usage" @open="(id, typeId) => detailHandlers.onOpen(id, typeId)" /></div>
                </div>
              </Transition>
              </template>
              <div class="space-page-foot">
                <span aria-live="polite">{{ shownText }}</span>
                <Button v-if="nextCursor" variant="outline" size="sm" data-more-pages :disabled="loading" @click="loadMore">{{ t.content.loadMore }}</Button>
              </div>
            </div>
            <EmptyState v-else compact :icon="allUsed ? 'check' : 'search'" :title="allUsed ? t.content.allUsedTitle : filtered ? t.content.noMatchTitle : t.content.emptyTitle"
              :text="allUsed ? t.content.allUsedText : filtered ? t.content.noMatchText : t.content.emptyText">
              <Button v-if="filtered" data-clear-filters variant="outline" size="sm" @click="query = ''; type = ''; relation = ''"><AtlasIcon name="close" :size="15" />{{ t.content.clearFilters }}</Button>
            </EmptyState>
          </div>
          <div v-else key="graph" class="content-view content-graph" data-content-graph>
            <div v-if="subject" class="content-graph-bar">
              <span class="content-graph-subject"><AtlasIcon :name="typeInfo(subject.typeId).icon" :size="15" />{{ t.content.surroundings }} <strong :title="plainTitle(subject.title)">{{ plainTitle(subject.title) || t.content.untitled }}</strong></span>
              <OptionPills v-model="depth" :options="depthOptions()" :label="t.content.depth" hide-label />
            </div>
            <ErrorState v-if="graphFailed" :title="t.content.graphFailed" :text="graphFailed" :busy="graphLoading" @retry="reloadGraph()" />
            <!-- The skeleton, the chunk's placeholder and the graph all fill the canvas's height, so nothing below moves when it arrives. -->
            <LoadingRegion v-else :loading="graphLoading" :ready="!!graph" :rows="1" row-height="var(--content-graph-height)" :label="t.content.graphLoading">
              <div v-if="subject && graph" class="content-graph-canvas">
                <RelationsGraph :graph="graph" :focus-id="subject.id" :label="t.content.graphLabel(plainTitle(subject.title))" :branch-of="branchOf"
                  @select="graphSelect" @open="graphOpen" @back="graphBack" />
              </div>
            </LoadingRegion>
            <EmptyState v-if="!subject && loaded" compact icon="network" :title="t.content.graphEmptyTitle" :text="t.content.graphEmptyText" />
          </div>
        </Transition>
      </LoadingRegion>
  </div>
</template>

<style scoped>
/* A flex column, not a grid: the notes above the table unfold with the disclosure hooks, which give the gap above them
   back as they fold (a grid track never gets smaller than 0). */
.space-pages { container: space-pages / inline-size; position: relative; display: flex; flex-direction: column; gap: 14px; min-width: 0; }
/* One line (board 02): search, type, Vztah, and the view switch on the right. */
.space-list-controls { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; }
/* SearchField passes attributes to its input, so its frame is reached by its own class. */
.space-list-controls > .search-field { flex: 1 1 240px; max-width: 360px; }
.space-list-controls :deep(.search-field-input) { min-height: 36px; border-radius: 10px; font-size: 14px; }
.space-list-controls :deep(.filter-menu-trigger) { min-height: 36px; padding: 0 12px; border-radius: 10px; font-size: 14px; }
.content-bulk { margin-top: 10px; }
/* Under the actions and Zrušit výběr, on a line of its own. */
.content-bulk-note { order: 1; flex-basis: 100%; margin: 0; color: var(--muted); font-size: 12px; }
.space-list-note { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 0; color: var(--muted); font-size: 13px; line-height: 1.55; }
.space-list-note.is-error { color: var(--error); }

/* `clip`, not `hidden`: the frame stays rounded without becoming a scroll container, so the column header sticks under the top bar while the rows pass under it. */
.space-page-table { overflow: clip; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
/* The geometry of a row comes from variables of the table (set by the container query at the end). Only the table
   answers the query: with every row and cell inside the @container rule, each start and end of a row's entrance made
   the browser restyle and lay out all hundred rows again (one layout 44 ms instead of 7 ms at CPU ×4). */
/* There is always a first track for the selection's boxes: 0 wide for a reader, 20 px plus its gap for a writer of a space with
   drafts (the boxes show on hover and focus, BulkSelectBar). The padding on its side is the page's margin less that gap, so a
   reader's row is where it always was (16 px in). */
.space-page-table { --space-col-gap: 16px; --space-row-gap-y: 0px; --space-pad-y: 0px; --space-pad-x: 16px; --space-pick-w: calc(20px + var(--space-col-gap)); }
.is-pickable :is(.space-page-head, .space-page-row) { grid-template-columns: var(--space-pick-w) var(--space-row-columns, minmax(0, 2.6fr) minmax(0, 2fr) 148px); }
.space-page-head, .space-page-row { display: grid; grid-template-columns: 0px var(--space-row-columns, minmax(0, 2.6fr) minmax(0, 2fr) 148px); grid-template-areas: var(--space-row-areas, "pick main used links comments changed"); align-items: center; gap: var(--space-row-gap-y) var(--space-col-gap); padding: var(--space-pad-y) var(--space-pad-x) var(--space-pad-y) calc(var(--space-pad-x) - var(--space-col-gap)); }
.space-page-head { display: var(--space-head-display, grid); position: sticky; top: var(--atlas-topbar-height, 52px); z-index: 2; height: 36px; border-bottom: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); background: color-mix(in srgb, var(--line) 16%, var(--surface)); color: var(--muted); font-size: 12px; font-weight: 600; }
.space-page-row {
  position: relative;
  min-height: 44px;
  border-bottom: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface));
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards;
  animation-delay: calc(var(--i, 0) * var(--st-stagger));
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  .space-page-row:hover { background: color-mix(in srgb, var(--hover) 45%, transparent); }
}
.space-page-row:has(.space-page-title:focus-visible) { outline: 2px solid var(--accent); outline-offset: -2px; }
/* The row shows the ring, not the shell's ring around the title. */
.space-page-row .space-page-title:focus-visible:not(.md-editor):not(.md-editor *) { outline: none; }
.page-selection, .space-head-pick { display: grid; place-items: center; grid-area: pick; justify-self: end; width: 20px; }
.space-page-main, .space-head-main { grid-area: main; }
.space-page-main { display: flex; align-items: center; gap: 10px; min-width: 0; }
.space-page-main > svg { flex-shrink: 0; color: var(--muted); }
.space-page-title { overflow: hidden; color: var(--text); font-size: 14px; font-weight: 500; text-decoration: none; text-overflow: ellipsis; white-space: nowrap; outline: none; }
/* The title is the row's link, stretched over it; a selection box stays above. */
.space-page-title::after { content: ""; position: absolute; inset: 0; }
.space-page-foot { display: flex; align-items: center; gap: 12px; min-height: 44px; padding: 0 16px; background: color-mix(in srgb, var(--line) 16%, var(--surface)); color: var(--muted); font-size: 13px; }
.space-page-foot > span { flex: 1; font-variant-numeric: tabular-nums; }

/* Obsah. The tiles are filters: border and tint answer, nothing moves. */
/* One row at every width (it scrolls sideways when the tiles do not fit): its height never depends on how many tiles the summary brings. */
.content-tiles { display: grid; grid-auto-flow: column; grid-auto-columns: minmax(150px, 1fr); gap: 10px; overflow-x: auto; scrollbar-width: none; }
.content-tiles.is-loading .content-tile { cursor: default; color: transparent; background: color-mix(in srgb, var(--hover) 60%, var(--surface)); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both; }
.content-tile { min-height: 0; padding: 12px 14px; cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.content-tile :deep(.stat-tile-value) { font-size: 18px; }
.content-tile :deep(.stat-tile-context) { display: block; overflow: hidden; font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
@media (hover: hover) and (pointer: fine) { .content-tile:hover { border-color: color-mix(in srgb, var(--accent) 45%, var(--line)); box-shadow: var(--st-hover-shadow, none); } }
.content-tile:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.content-tile:active { transform: scale(var(--atlas-press)); }
.content-tile.is-active { border-color: var(--accent); background: var(--accent-soft); }
.content-type-pills :deep(.option-pills-row) { flex-wrap: nowrap; overflow-x: auto; scrollbar-width: none; }
.content-region { min-width: 0; }
.content-view { min-width: 0; }
.content-view-enter-active { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.content-view-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; }
.content-view-switch { margin-left: auto; }

.is-obsah { --space-row-columns: minmax(0, 1fr) 72px 104px 84px 84px; --space-row-areas: "pick main used links comments changed"; }
.is-obsah .space-page-row { cursor: default; min-height: 52px; content-visibility: auto; contain-intrinsic-size: auto 52px; }
/* The title no longer stretches over the row: a click on the row opens the dock, the title opens the object. */
.is-obsah .space-page-title::after { content: none; }
.is-obsah .space-page-title { max-width: 100%; padding: 0; border: 0; background: none; font-family: inherit; text-align: left; cursor: pointer; }
/* Outranks the row-ring rule above (`:not(.md-editor *)`), which otherwise left the title without any ring (UX cycle 4, SB4-1). */
.is-obsah .space-page-row .space-page-title:focus-visible:not(.md-editor):not(.md-editor *) { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.is-obsah .space-page-row:has(.space-page-title:focus-visible) { outline: none; }
@media (hover: hover) and (pointer: fine) { .is-obsah .space-page-title:hover { text-decoration: underline; } }
.content-row.is-active, .content-row.is-open { background: color-mix(in srgb, var(--accent) 9%, transparent); }
.content-row.is-active { box-shadow: inset 3px 0 0 var(--accent); }
.content-title-block { display: flex; flex-direction: column; min-width: 0; }
.content-sub { overflow: hidden; color: var(--muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.content-scope { flex: none; padding: 1px 8px; border-radius: 999px; background: var(--info-bg); color: var(--info); font-size: 11.5px; font-weight: 600; }
.content-row-detail { flex: none; display: grid; place-items: center; width: 28px; height: 28px; margin-left: auto; border: 0; border-radius: 8px; background: transparent; color: var(--muted); cursor: pointer; opacity: 0;
  transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.content-row:focus-within .content-row-detail, .content-row.is-open .content-row-detail { opacity: 1; }
@media (hover: hover) and (pointer: fine) { .content-row:hover .content-row-detail { opacity: 1; } }
@media (hover: none) { .content-row-detail { opacity: 1; } }
@media (hover: hover) and (pointer: fine) { .content-row-detail:hover { background: var(--hover); color: var(--text); } }
.content-row-detail:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.content-counts { display: contents; }
.content-used, .content-links, .content-comments, .content-changed, .content-head-used, .content-head-links, .content-head-comments, .content-head-changed { font-variant-numeric: tabular-nums; }
.content-used, .content-links, .content-comments { display: inline-flex; align-items: center; gap: 4px; color: var(--text); font-size: 13px; }
.content-used > svg, .content-links > svg, .content-comments > svg { color: var(--muted); }
.content-links > svg:nth-of-type(2) { margin-left: 6px; }
.content-used.is-zero, .content-links.is-zero, .content-comments.is-zero { color: var(--muted); }
.content-used, .content-head-used { grid-area: used; }
.content-used-toggle { display: inline-flex; align-items: center; gap: 4px; padding: 2px 6px; margin: -2px -6px; border: 0; border-radius: 6px; background: transparent; color: inherit; font: inherit; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.content-used-toggle > svg { color: var(--muted); }
.content-used-toggle[aria-expanded="true"] { background: var(--accent-soft); color: var(--accent); }
@media (hover: hover) and (pointer: fine) { .content-used-toggle:hover { background: var(--hover); } }
.content-used-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.content-usage { padding: 4px var(--space-pad-x, 16px) 14px calc(var(--space-pad-x, 16px) + 26px); border-bottom: 1px solid color-mix(in srgb, var(--line) 60%, transparent); }
.content-usage-cell { max-width: 640px; }
.content-links, .content-head-links { grid-area: links; }
.content-comments, .content-head-comments { grid-area: comments; }
.content-changed, .content-head-changed { grid-area: changed; color: var(--muted); font-size: 12.5px; white-space: nowrap; }
.content-graph { --content-graph-height: clamp(420px, 68vh, 760px); display: flex; flex-direction: column; gap: 10px; }
.content-graph-bar { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px 14px; }
.content-graph-subject { display: inline-flex; align-items: center; gap: 6px; min-width: 0; color: var(--muted); font-size: 13px; }
.content-graph-subject strong { overflow: hidden; color: var(--text); font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.content-graph-canvas { height: var(--content-graph-height); }

/* Narrow room (a phone, or a laptop with the sidebar and the page dock open): each page on two lines (title, then where it lives), the state on the right. */
@container space-pages (max-width: 680px) {
  .space-list-controls > .search-field { flex-basis: 100%; max-width: none; }
  .space-page-table {
    --space-head-display: none;
    --space-col-gap: 10px; --space-row-gap-y: 2px; --space-pad-y: 8px; --space-pad-x: 12px;
  }
  .space-page-foot { flex-wrap: wrap; padding: 8px 12px; }
  /* Obsah: a card per object, the title with where it lives, then the counts, the date beside the title. */
  .is-obsah { --space-row-columns: minmax(0, 1fr) auto; --space-row-areas: "pick main changed" "pick counts counts"; --space-pad-y: 10px; }
  .is-obsah .content-counts { display: flex; grid-area: counts; gap: 14px; }
  .is-obsah :is(.content-used, .content-links, .content-comments) { grid-area: auto; }
  .content-view-switch { margin-left: 0; }
  /* The tiles become one row that scrolls sideways, so the list starts right under the filters. */
  .content-tiles { display: flex; overflow-x: auto; scrollbar-width: none; }
  .content-tile { flex: none; padding: 8px 12px; }
  .content-tile :deep(.stat-tile-context) { display: none; }
  .content-graph { --content-graph-height: clamp(360px, 62vh, 560px); }
}
</style>
