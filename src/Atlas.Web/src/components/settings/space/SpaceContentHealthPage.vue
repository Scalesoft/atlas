<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { ApiError, errorMessage } from "../../../api";
import { useToast } from "../../../composables/useToast";
import { useAccountSearch, type PageAccountSource } from "../../../contacts/useAccountSearch";
import { formatDate } from "../../../formatDate";
import t from "../../../i18n/verification";
import { contentHealthApi, verificationIcon, verificationLabel, verificationTone, type ContentHealth, type ContentHealthApi, type ContentHealthBulk, type ContentHealthDays, type ContentHealthFilter, type ContentHealthItem, type ContentHealthSort } from "../../../verification/pageVerification";
import AppDialog from "../../AppDialog.vue";
import ConfirmDialog from "../ConfirmDialog.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import { Button } from "../../ui/button";
import { Checkbox } from "../../ui/checkbox";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import FilterMenu from "../FilterMenu.vue";
import InlineReveal from "../InlineReveal.vue";
import SearchField from "../SearchField.vue";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatTile from "../StatTile.vue";
import StatusBadge from "../StatusBadge.vue";
import { useSettingsContext } from "../context";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import type { ChoiceOption } from "../types";

/**
 * Stav obsahu (5.2, plan §6): what of a space is verified, what expires and what people actually read, in existing parts only: StatTile row that sets
 * the filter, one SettingsList with a search and FilterMenus, a selection with bulk actions. Counts of readers, never names. The default order is
 * „Nejdřív čtené a neověřené“. Keyboard first: arrows move through the rows, Space selects, V verifies the selection (or the row), G picks the owner,
 * Enter opens the page. The server answers the state of every page (one C# function), so nothing is derived here. A row offers only what the reader
 * can do there: Ověřit, the checkbox, V and G where it may edit the page and the page verifies; „Přesunout do koše“ (the tree's trash, Zpět in
 * the toast) on an unread page it may edit. „Bez garanta“ offers the owner from the last author for the whole space (the server's fill).
 */
const props = withDefaults(defineProps<{
  spaceId: string;
  api?: ContentHealthApi;
  /** The accounts an owner may be, searched in the context of one page (the gallery passes local ones). */
  people?: PageAccountSource;
}>(), { api: () => contentHealthApi, people: undefined });
const context = useSettingsContext();
const flash = useSettingsFlash();
const router = useSettingsRoute();
const toaster = useToast();
// Only who manages the space can switch verification on (Obecné); a writer is told who does.
const manages = computed(() => context.isWikiAdmin || context.spaces.find((space) => space.id === props.spaceId)?.role === "Admin");

const state = ref<ContentHealthFilter>("");
const mine = ref("");
// A FilterMenu's first option (value "") is its default and does not light the trigger: the default order and the 90 days are those.
const sort = ref<"" | ContentHealthSort>("");
const days = ref("");
const query = ref("");
const data = shallowRef<ContentHealth>();
const items = ref<ContentHealthItem[]>([]);
const loading = ref(false);
const loadError = ref("");
/** The last action's failure with its way forward: the same action again, or (a page with subpages) the page, whose tree trashes it. */
const failure = shallowRef<{ text: string; retry: () => unknown; label?: string }>();
const busy = ref(false);
const selected = ref(new Set<string>());
const active = ref(0);
const root = ref<HTMLElement>();
let request: AbortController | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;

const stateOptions: ChoiceOption[] = [
  { value: "", label: t.health.filterStateAll, icon: "filter" },
  { value: "due", label: t.health.filterDue, icon: "shield-alert", tone: "warning" },
  { value: "verified", label: t.health.filterVerified, icon: "shield-check" },
  { value: "expiring", label: t.health.filterExpiring, icon: "clock" },
  { value: "lapsed", label: t.health.filterLapsed, icon: "shield-alert", tone: "warning" },
  { value: "no-steward", label: t.health.filterNoSteward, icon: "user" },
  { value: "unread", label: t.health.filterUnread, icon: "eye-off" },
];
const mineOptions: ChoiceOption[] = [{ value: "", label: t.health.mineAll, icon: "users" }, { value: "mine", label: t.health.mine, icon: "user" }];
const sortOptions: ChoiceOption[] = [
  { value: "", label: t.health.sortPriority, icon: "shield-alert" },
  { value: "expiry", label: t.health.sortExpiry, icon: "calendar" },
  { value: "readers", label: t.health.sortReaders, icon: "users" },
  { value: "published", label: t.health.sortPublished, icon: "history" },
];
const periodOptions: ChoiceOption[] = [30, 90, 365].map((n) => ({ value: n === 90 ? "" : String(n), label: t.health.days(n), icon: "clock" }));
const periodDays = () => Number(days.value || 90) as ContentHealthDays;

/** `append`: the next page of the same list; `keep`: the same list read again after an action (the keyboard stays on its row, the selection is spent). */
async function load(append = false, keep = false) {
  request?.abort();
  const controller = (request = new AbortController());
  loading.value = true;
  loadError.value = "";
  try {
    const result = await props.api.list(props.spaceId, { state: state.value, mine: mine.value === "mine", sort: sort.value || "priority", days: periodDays(), q: query.value, cursor: append ? data.value?.nextCursor : null }, controller.signal);
    if (controller.signal.aborted) return;
    data.value = result;
    items.value = append ? [...items.value, ...result.items] : result.items;
    if (!append) { selected.value = new Set(); active.value = keep ? Math.min(active.value, Math.max(0, items.value.length - 1)) : 0; }
  } catch (failure) {
    if (!controller.signal.aborted) loadError.value = errorMessage(failure);
  } finally {
    if (request === controller) loading.value = false;
  }
}
// A filter, sort or period asks again at once; the search waits for a pause in the typing.
watch([state, mine, sort, days], () => void load());
watch(query, () => { clearTimeout(timer); timer = setTimeout(() => void load(), 250); });
void load();
onBeforeUnmount(() => { request?.abort(); clearTimeout(timer); });

const summary = computed(() => data.value?.summary);
const tiles = computed(() => {
  const s = summary.value;
  return [
    { key: "verified", label: t.health.tiles.verified, value: s?.verified ?? 0, icon: "shield-check", tone: "success" as const, filter: "verified" as ContentHealthFilter },
    { key: "expiring", label: t.health.tiles.expiring, value: s?.expiring ?? 0, icon: "clock", tone: undefined, filter: "expiring" as ContentHealthFilter },
    { key: "expired", label: t.health.tiles.expired, value: (s?.expired ?? 0) + (s?.unverified ?? 0), icon: "shield-alert", tone: "warning" as const, filter: "lapsed" as ContentHealthFilter },
    { key: "noSteward", label: t.health.tiles.noSteward, value: s?.withoutSteward ?? 0, icon: "user", tone: undefined, filter: "no-steward" as ContentHealthFilter },
  ];
});
/** A tile sets the filter it counts (the server knows each one); a second click on the lit tile shows everything again. */
const pickTile = (filter: ContentHealthFilter) => { state.value = state.value === filter ? "" : filter; };

// Selection and the bulk actions: only the rows the reader may verify (it edits the page and the page verifies).
const actionable = (item: ContentHealthItem) => item.canEdit && item.state !== "off";
const chosen = computed(() => items.value.filter((item) => selected.value.has(item.pageId)));
const toggle = (id: string) => { const next = new Set(selected.value); if (!next.delete(id)) next.add(id); selected.value = next; };
const allShown = computed(() => items.value.some(actionable) && items.value.filter(actionable).every((item) => selected.value.has(item.pageId)));
const toggleAll = () => { selected.value = allShown.value ? new Set() : new Set(items.value.filter(actionable).map((item) => item.pageId)); };
/** The rows an action acts on: the selection, or the row the keyboard stands on when nothing is selected. */
const target = () => (chosen.value.length ? chosen.value : items.value.slice(active.value, active.value + 1).filter(actionable));

/** The list read again after an action: the row leaves or moves, and the keyboard stays on its place (V, ↓, V row by row). */
async function reload(inList: boolean) {
  await load(false, true);
  if (inList) { await nextTick(); root.value?.querySelectorAll<HTMLElement>("[data-health-row]")[active.value]?.focus(); }
}
const focusInList = () => !!(document.activeElement as HTMLElement | null)?.closest?.("[data-health-row]");

async function bulk(request: ContentHealthBulk, done: (n: number) => string) {
  if (busy.value || (request.pageIds && !request.pageIds.length)) return;
  const inList = focusInList();
  busy.value = true;
  failure.value = undefined;
  try {
    let changed = 0;
    const skipped: { reason: string }[] = [];
    // Without pageIds the server fills the whole space 500 pages at a time: again while it has more and still changes some.
    for (let more = true; more;) {
      const result = await props.api.bulk(props.spaceId, request);
      changed += result.changed;
      skipped.push(...result.skipped);
      more = !request.pageIds && !!result.more && result.changed > 0;
    }
    const owned = skipped.filter((page) => page.reason === "has_steward").length;
    flash.notify([done(changed), owned ? t.health.hasSteward(owned) : "", skipped.length > owned ? t.health.skipped(skipped.length - owned) : ""].filter(Boolean).join(" "), { icon: "shield-check" });
    await reload(inList);
  } catch (error) {
    failure.value = { text: errorMessage(error), retry: () => bulk(request, done) };
  } finally {
    busy.value = false;
  }
}
/** Verification has no bulk undo (a date is set, the previous one is not kept), so a large selection asks first. */
const confirmAbove = 10;
const confirming = ref<ContentHealthItem[]>([]);
const runVerify = (rows: ContentHealthItem[]) => bulk({ action: "verify", pageIds: rows.map((row) => row.pageId) }, t.health.verifiedDone);
const verify = async (rows = target()) => {
  if (rows.length > confirmAbove) confirming.value = rows;
  else await runVerify(rows);
};
async function confirmVerify() { const rows = confirming.value; confirming.value = []; await runVerify(rows); }
const fromAuthor = () => bulk({ action: "stewardFromLastAuthor", pageIds: target().map((row) => row.pageId) }, t.health.stewardDone);
const fromAuthorSpace = () => bulk({ action: "stewardFromLastAuthor" }, t.health.stewardDone);
/** „Bez garanta“ with nothing selected: the fill of the whole space, not only the rows loaded so far. */
const offerSpaceFill = computed(() => state.value === "no-steward" && !chosen.value.length && !!summary.value?.withoutSteward);

/** „Přesunout do koše“ of an unread page: the tree's trash, restorable, confirmed by a toast with Zpět. */
async function trash(item: ContentHealthItem) {
  if (busy.value) return;
  const inList = focusInList();
  busy.value = true;
  failure.value = undefined;
  try {
    const { undo } = await props.api.trash(props.spaceId, item.pageId);
    toaster.success(t.health.trashed(item.title), { undo: () => void undo().then(() => load(false, true), (error) => toaster.error(errorMessage(error))) });
    await reload(inList);
  } catch (error) {
    failure.value = error instanceof ApiError && error.code === "subtree_confirmation_required"
      ? { text: t.health.trashSubtree, label: t.health.open, retry: () => { failure.value = undefined; openPage(item.pageId); } }
      : { text: errorMessage(error), retry: () => trash(item) };
  } finally {
    busy.value = false;
  }
}

// The owner picker: the accounts with access to all the rows (a space has no list of its own), the first result preselected, Enter applies it.
const pickerRows = ref<ContentHealthItem[]>([]);
const pickerOpen = ref(false);
const { query: peopleQuery, options: people, loading: searching, start: findPeople } = useAccountSearch(() => pickerRows.value.map((row) => row.pageId), props.people);
function openPicker(rows = target()) {
  if (!rows.length) return;
  pickerRows.value = rows;
  pickerOpen.value = true;
  findPeople();
}
async function pickSteward(stewardId: string) {
  const rows = pickerRows.value;
  pickerOpen.value = false;
  await bulk({ action: "steward", pageIds: rows.map((row) => row.pageId), stewardId }, t.health.stewardDone);
}
const openPage = (id: string) => context.openPage(id);

/** The keys of a row (↑ ↓ Home End are the list's): Space selects, V verifies, G picks an owner, Enter opens. Not while a button or field has the key. */
function rowKey(event: KeyboardEvent, index: number) {
  if (event.altKey || event.ctrlKey || event.metaKey || (event.target as HTMLElement).closest("input, textarea, button, a, [role='checkbox']")) return;
  const key = event.key.toLowerCase();
  if (key === " ") { event.preventDefault(); if (actionable(items.value[index]!)) toggle(items.value[index]!.pageId); }
  else if (key === "v") { event.preventDefault(); void verify(); }
  else if (key === "g") { event.preventDefault(); openPicker(); }
  else if (key === "enter") { event.preventDefault(); openPage(items.value[index]!.pageId); }
}

/** The row's menu: what the bulk buttons do (Ověřit, Garant), Otevřít and, where it is offered, „Přesunout do koše“. No V/G hints until the keys are settled (D6). */
const rowMenu = (item: ContentHealthItem) => [
  ...(actionable(item) ? [{ id: "verify", label: t.health.verify, icon: "shield-check", disabled: busy.value }, { id: "steward", label: t.health.setSteward, icon: "user", disabled: busy.value }] : []),
  { id: "open", label: t.health.open, icon: "file-text" },
  ...(item.canEdit && !item.views ? [{ id: "trash", label: t.health.toTrash, icon: "trash", destructive: true, disabled: busy.value }] : []),
];
const rowAction = (item: ContentHealthItem, id: string) => { if (id === "verify") void verify([item]); else if (id === "steward") openPicker([item]); else if (id === "open") openPage(item.pageId); else void trash(item); };

const rowText = (item: ContentHealthItem) => [item.path.length ? item.path.join(t.health.pathSep) : "", t.health.steward(item.stewardName)].filter(Boolean).join(" · ");
const rowDetail = (item: ContentHealthItem) => [t.health.readers(item.readers, periodDays()), t.health.lastPublished(item.lastPublishedAt ? formatDate(item.lastPublishedAt) : null)].join(" · ");
const enabled = computed(() => !data.value || data.value.enabled);
const empty = computed(() => !!data.value && !loading.value && !items.value.length);
</script>

<template>
  <div ref="root">
    <SettingsPage :title="t.health.title" icon="shield-check" :description="t.health.description">
      <ErrorState v-if="loadError" :title="t.health.loadFailed" :text="loadError" :busy="loading" @retry="load()" />
      <LoadingSkeleton v-else-if="!data" :label="t.health.loading" />
      <EmptyState v-else-if="!enabled" icon="shield-check" :title="manages ? t.health.off : t.health.offWriter">
        <Button v-if="manages" as-child variant="outline"><a :href="router.href('general')"><AtlasIcon name="settings" :size="16" />{{ t.health.offOpen }}</a></Button>
      </EmptyState>
      <template v-else>
        <div class="health-tiles" data-health-tiles>
          <StatTile v-for="tile in tiles" :key="tile.key" compact :label="tile.label" :value="String(tile.value)" :icon="tile.icon" :tone="tile.value ? tile.tone : undefined" href="#" :data-health-tile="tile.key" role="button" :aria-pressed="state === tile.filter" :class="{ 'is-active': state === tile.filter }" :aria-label="t.health.tilesFilter(tile.label)" @click.prevent="pickTile(tile.filter)" />
        </div>
        <SettingsSection :title="t.health.section" icon="shield-check" :count="items.length">
          <div class="health-filters">
            <SearchField v-model="query" :label="t.health.search" data-health-search />
            <FilterMenu v-model="state" :label="t.health.filterState" :options="stateOptions" data-health-filter-state />
            <FilterMenu v-model="mine" :label="t.steward" :options="mineOptions" data-health-filter-mine />
            <FilterMenu v-model="sort" :label="t.health.sort" :options="sortOptions" data-health-sort />
            <FilterMenu v-model="days" :label="t.health.period" :options="periodOptions" icon="clock" data-health-period />
          </div>
          <InlineReveal :open="chosen.length > 0">
            <div class="health-bulk" role="group" :aria-label="t.health.selected(chosen.length)" data-health-bulk>
              <strong>{{ t.health.selected(chosen.length) }}</strong>
              <Button size="sm" type="button" :disabled="busy" data-health-verify @click="verify()"><AtlasIcon name="shield-check" :size="15" />{{ t.health.verify }}<kbd>V</kbd></Button>
              <Button variant="outline" size="sm" type="button" :disabled="busy" data-health-steward @click="openPicker"><AtlasIcon name="user" :size="15" />{{ t.health.setSteward }}<kbd>G</kbd></Button>
              <Button variant="outline" size="sm" type="button" :disabled="busy" data-health-from-author @click="fromAuthor"><AtlasIcon name="history" :size="15" />{{ t.health.fromAuthor }}</Button>
              <Button variant="ghost" size="sm" type="button" @click="selected = new Set()">{{ t.health.clear }}</Button>
            </div>
          </InlineReveal>
          <InlineReveal :open="offerSpaceFill">
            <div class="health-bulk" data-health-space-fill>
              <span>{{ t.health.fromAuthorSpace(summary?.withoutSteward ?? 0) }}</span>
              <Button variant="outline" size="sm" type="button" :disabled="busy" data-health-from-author-space @click="fromAuthorSpace"><AtlasIcon name="history" :size="15" />{{ t.health.fromAuthor }}</Button>
            </div>
          </InlineReveal>
          <ErrorState v-if="failure" :title="t.health.actionFailed" :text="failure.text" :retry-label="failure.label" :busy="busy" @retry="failure.retry()" />
          <div :aria-busy="loading" class="health-rows">
            <label v-if="items.some(actionable)" class="health-all"><Checkbox :model-value="allShown" :aria-label="t.health.selectAll" data-health-all @update:model-value="toggleAll" /><span>{{ t.health.selectAll }}</span></label>
            <SettingsList v-if="items.length" :label="t.health.list">
              <SettingsListRow v-for="(item, index) in items" :key="item.pageId" :title="item.title" :description="rowText(item)" :tabindex="index === active ? 0 : -1" class="health-row" :menu="rowMenu(item)" :menu-label="t.health.rowActions(item.title)" :more="actionable(item)" data-health-row :data-page="item.pageId" :data-selected="selected.has(item.pageId) || undefined" @select="rowAction(item, $event)" @keydown="rowKey($event, index)" @focusin="active = index">
                <!-- A row it cannot verify keeps the checkbox's room (the titles stay in one line) without the checkbox. -->
                <template #leading><Checkbox :model-value="selected.has(item.pageId)" :aria-label="t.health.selectRow(item.title)" tabindex="-1" :class="{ invisible: !actionable(item) }" :disabled="!actionable(item)" @update:model-value="toggle(item.pageId)" /></template>
                <template #meta>
                  <span class="health-detail">{{ rowDetail(item) }}</span>
                  <StatusBadge :tone="verificationTone(item.state)" :icon="verificationIcon(item.state)" :label="verificationLabel(item)" size="sm" :data-health-state="item.state" />
                </template>
                <template #actions>
                  <Button v-if="actionable(item)" variant="ghost" size="sm" type="button" :disabled="busy" data-health-row-verify @click="verify([item])"><AtlasIcon name="shield-check" :size="15" />{{ t.health.verify }}</Button>
                  <Button v-if="item.canEdit && !item.views" variant="ghost" size="sm" type="button" :disabled="busy" data-health-row-trash @click="trash(item)"><AtlasIcon name="trash" :size="15" />{{ t.health.toTrash }}</Button>
                  <Button variant="ghost" size="sm" type="button" data-health-open @click="openPage(item.pageId)"><AtlasIcon name="file-text" :size="15" />{{ t.health.open }}</Button>
                </template>
              </SettingsListRow>
            </SettingsList>
            <EmptyState v-if="empty" compact icon="search" :title="t.health.emptyTitle" :text="t.health.emptyText" />
            <Button v-if="data?.nextCursor" variant="outline" size="sm" type="button" :disabled="loading" class="health-more" data-health-more @click="load(true)">{{ t.health.more }}</Button>
          </div>
          <p class="health-keys">{{ t.health.keys }}</p>
        </SettingsSection>
      </template>
    </SettingsPage>
    <ConfirmDialog :open="confirming.length > 0" :title="t.health.confirmTitle(confirming.length)" :confirm-label="t.health.verify" confirm-icon="shield-check" :destructive="false" :busy="busy" @confirm="confirmVerify" @cancel="confirming = []">
      <p>{{ t.health.confirmText }}</p>
    </ConfirmDialog>
    <AppDialog :title="t.health.stewardPick" :open="pickerOpen" @close="pickerOpen = false">
      <SearchableChoiceList v-model:query="peopleQuery" remote :loading="searching" :options="people" :label="t.health.stewardPick" :placeholder="t.stewardSearch" :empty-text="t.stewardEmpty" :max-height="260" @pick="pickSteward" />
    </AppDialog>
  </div>
</template>

<style scoped>
.health-tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; }
@container settings-shell (max-width: 760px) { .health-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
.health-tiles :deep(a.stat-tile) { cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.health-tiles :deep(a.stat-tile.is-active) { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 8%, var(--surface)); }
.health-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.health-filters .search-field { flex: 1 1 220px; }
.health-bulk { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; padding: 8px 0 4px; font-size: 13px; }
.health-all { display: flex; align-items: center; gap: 10px; padding: 4px 0; color: var(--muted); font-size: 13px; }
.health-rows > .settings-list { margin: 0 calc(var(--st-pad-x, 18px) * -1); --st-row-pad-x: var(--st-pad-x, 18px); }
.health-rows > .st-empty.is-compact { border-style: none; padding-block: 20px; }
.health-row { transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.health-row[data-selected] { background: color-mix(in srgb, var(--accent) 8%, transparent); }
@media (hover: hover) and (pointer: fine) { .health-row:hover { background: var(--hover); } }
.health-row:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.health-detail { color: var(--muted); font-size: 12.5px; }
.health-more { margin-top: 10px; }
.health-keys { margin: 12px 0 0; color: var(--muted); font-size: 12.5px; }
</style>
