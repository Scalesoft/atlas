<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { errorMessage } from "../../../api";
import { pageAccounts, type PagePerson } from "../../../contacts/contactSources";
import { formatDate } from "../../../formatDate";
import t from "../../../i18n/verification";
import { contentHealthApi, verificationIcon, verificationLabel, verificationTone, type ContentHealth, type ContentHealthApi, type ContentHealthBulk, type ContentHealthDays, type ContentHealthFilter, type ContentHealthItem, type ContentHealthSort } from "../../../verification/pageVerification";
import AppDialog from "../../AppDialog.vue";
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
 * Enter opens the page. The server answers the state of every page (one C# function), so nothing is derived here.
 */
const props = withDefaults(defineProps<{
  spaceId: string;
  api?: ContentHealthApi;
  /** The accounts an owner may be, searched in the context of one page (the gallery passes local ones). */
  people?: (pageId: string, text: string, signal: AbortSignal) => Promise<PagePerson[]>;
}>(), { api: () => contentHealthApi, people: (pageId: string, text: string, signal: AbortSignal) => pageAccounts(pageId)(text, signal) });
const context = useSettingsContext();
const flash = useSettingsFlash();
const router = useSettingsRoute();

const state = ref<ContentHealthFilter>("");
const mine = ref("");
const sort = ref<ContentHealthSort>("priority");
const days = ref(String(90));
const query = ref("");
const data = shallowRef<ContentHealth>();
const items = ref<ContentHealthItem[]>([]);
const loading = ref(false);
const loadError = ref("");
const actionError = ref("");
const busy = ref(false);
const selected = ref(new Set<string>());
const active = ref(0);
const root = ref<HTMLElement>();
let request: AbortController | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;

const stateOptions: ChoiceOption[] = [
  { value: "", label: t.health.filterStateAll, icon: "filter" },
  { value: "due", label: t.health.filterDue, icon: "shield-alert", tone: "warning" },
  { value: "no-steward", label: t.health.filterNoSteward, icon: "user" },
  { value: "unread", label: t.health.filterUnread, icon: "eye-off" },
];
const mineOptions: ChoiceOption[] = [{ value: "", label: t.health.mineAll, icon: "users" }, { value: "mine", label: t.health.mine, icon: "user" }];
const sortOptions: ChoiceOption[] = [
  { value: "priority", label: t.health.sortPriority, icon: "shield-alert" },
  { value: "expiry", label: t.health.sortExpiry, icon: "calendar" },
  { value: "readers", label: t.health.sortReaders, icon: "users" },
  { value: "published", label: t.health.sortPublished, icon: "history" },
];
const periodOptions: ChoiceOption[] = [30, 90, 365].map((n) => ({ value: String(n), label: t.health.days(n), icon: "clock" }));

/** `append`: the next page of the same list; `keep`: the same list read again after an action (the keyboard stays on its row, the selection is spent). */
async function load(append = false, keep = false) {
  request?.abort();
  const controller = (request = new AbortController());
  loading.value = true;
  loadError.value = "";
  try {
    const result = await props.api.list(props.spaceId, { state: state.value, mine: mine.value === "mine", sort: sort.value, days: Number(days.value) as ContentHealthDays, q: query.value, cursor: append ? data.value?.nextCursor : null }, controller.signal);
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
    { key: "verified", label: t.health.tiles.verified, value: s?.verified ?? 0, icon: "shield-check", tone: "success" as const, filter: "" as ContentHealthFilter },
    { key: "expiring", label: t.health.tiles.expiring, value: s?.expiring ?? 0, icon: "clock", tone: undefined, filter: "due" as ContentHealthFilter },
    { key: "expired", label: t.health.tiles.expired, value: (s?.expired ?? 0) + (s?.unverified ?? 0), icon: "shield-alert", tone: "warning" as const, filter: "due" as ContentHealthFilter },
    { key: "noSteward", label: t.health.tiles.noSteward, value: s?.withoutSteward ?? 0, icon: "user", tone: undefined, filter: "no-steward" as ContentHealthFilter },
  ];
});
/** A tile sets the filter it counts: Ověřeno shows everything again (the verified ones come last in the default order), the others narrow. */
const pickTile = (filter: ContentHealthFilter) => { state.value = filter; };

// Selection and the bulk actions.
const chosen = computed(() => items.value.filter((item) => selected.value.has(item.pageId)));
const toggle = (id: string) => { const next = new Set(selected.value); if (!next.delete(id)) next.add(id); selected.value = next; };
const allShown = computed(() => items.value.length > 0 && items.value.every((item) => selected.value.has(item.pageId)));
const toggleAll = () => { selected.value = allShown.value ? new Set() : new Set(items.value.map((item) => item.pageId)); };
/** The rows an action acts on: the selection, or the row the keyboard stands on when nothing is selected. */
const target = () => (chosen.value.length ? chosen.value : items.value[active.value] ? [items.value[active.value]!] : []);

async function bulk(request: ContentHealthBulk, done: (n: number) => string) {
  if (busy.value || !request.pageIds.length) return;
  busy.value = true;
  actionError.value = "";
  try {
    const result = await props.api.bulk(props.spaceId, request);
    flash.notify([done(result.changed), result.skipped.length ? t.health.skipped(result.skipped.length) : ""].filter(Boolean).join(" "), { icon: "shield-check" });
    await load(false, true);
  } catch (failure) {
    actionError.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
const verify = (rows = target()) => bulk({ action: "verify", pageIds: rows.map((row) => row.pageId) }, t.health.verifiedDone);
const fromAuthor = () => bulk({ action: "stewardFromLastAuthor", pageIds: target().map((row) => row.pageId) }, t.health.stewardDone);

// The owner picker: the accounts of the first page of the rows (a space has no list of its own), the first result preselected, Enter applies it.
const pickerRows = ref<ContentHealthItem[]>([]);
const pickerOpen = ref(false);
const people = ref<ChoiceOption[]>([]);
const peopleQuery = ref("");
const searching = ref(false);
let peopleRequest: AbortController | undefined;
let peopleTimer: ReturnType<typeof setTimeout> | undefined;
async function findPeople() {
  peopleRequest?.abort();
  const controller = (peopleRequest = new AbortController());
  const first = pickerRows.value[0];
  if (!first) return;
  searching.value = true;
  try {
    const found = await props.people(first.pageId, peopleQuery.value.trim(), controller.signal);
    if (!controller.signal.aborted) people.value = found.map((person) => ({ value: person.id, label: person.name, description: person.username ?? person.email ?? undefined, icon: "user" }));
  } catch {
    if (!controller.signal.aborted) people.value = [];
  } finally {
    if (peopleRequest === controller) searching.value = false;
  }
}
function openPicker() {
  const rows = target();
  if (!rows.length) return;
  pickerRows.value = rows;
  peopleQuery.value = "";
  people.value = [];
  pickerOpen.value = true;
  void findPeople();
}
watch(peopleQuery, () => { clearTimeout(peopleTimer); peopleTimer = setTimeout(() => void findPeople(), 180); });
async function pickSteward(stewardId: string) {
  const rows = pickerRows.value;
  pickerOpen.value = false;
  await bulk({ action: "steward", pageIds: rows.map((row) => row.pageId), stewardId }, t.health.stewardDone);
}
const openPage = (id: string) => context.openPage(id);

/** The keys of the list: ↑ ↓ move (roving tab stop), Space selects, V verifies, G picks an owner, Enter opens. Not while a button or field has the key. */
function rowKey(event: KeyboardEvent, index: number) {
  if (event.altKey || event.ctrlKey || event.metaKey || (event.target as HTMLElement).closest("input, textarea, button, a, [role='checkbox']")) return;
  const key = event.key.toLowerCase();
  if (key === "arrowdown" || key === "arrowup") {
    event.preventDefault();
    const next = Math.min(items.value.length - 1, Math.max(0, index + (key === "arrowdown" ? 1 : -1)));
    active.value = next;
    (root.value?.querySelectorAll<HTMLElement>("[data-health-row]")[next])?.focus();
  } else if (key === " ") { event.preventDefault(); toggle(items.value[index]!.pageId); }
  else if (key === "v") { event.preventDefault(); void verify(); }
  else if (key === "g") { event.preventDefault(); openPicker(); }
  else if (key === "enter") { event.preventDefault(); openPage(items.value[index]!.pageId); }
}

const rowText = (item: ContentHealthItem) => [item.path.length ? item.path.join(t.health.pathSep) : "", t.health.steward(item.stewardName)].filter(Boolean).join(" · ");
const rowDetail = (item: ContentHealthItem) => [t.health.readers(item.readers, Number(days.value)), t.health.lastPublished(item.lastPublishedAt ? formatDate(item.lastPublishedAt) : null)].join(" · ");
const enabled = computed(() => !data.value || data.value.enabled);
const empty = computed(() => !!data.value && !loading.value && !items.value.length);
</script>

<template>
  <div ref="root">
    <SettingsPage :title="t.health.title" icon="shield-check" :description="t.health.description">
      <ErrorState v-if="loadError" :title="t.health.loadFailed" :text="loadError" :busy="loading" @retry="load()" />
      <LoadingSkeleton v-else-if="!data" :label="t.health.loading" />
      <EmptyState v-else-if="!enabled" icon="shield-check" :title="t.health.off">
        <Button as-child variant="outline"><a :href="router.href('general')"><AtlasIcon name="settings" :size="16" />{{ t.health.offOpen }}</a></Button>
      </EmptyState>
      <template v-else>
        <div class="health-tiles" data-health-tiles>
          <StatTile v-for="tile in tiles" :key="tile.key" compact :label="tile.label" :value="String(tile.value)" :icon="tile.icon" :tone="tile.value ? tile.tone : undefined" href="#" :data-health-tile="tile.key" :aria-label="t.health.tilesFilter(tile.label)" @click.prevent="pickTile(tile.filter)" />
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
          <ErrorState v-if="actionError" :title="t.health.actionFailed" :text="actionError" :busy="busy" @retry="actionError = ''" />
          <div :aria-busy="loading" class="health-rows">
            <label v-if="items.length" class="health-all"><Checkbox :model-value="allShown" :aria-label="t.health.selectAll" data-health-all @update:model-value="toggleAll" /><span>{{ t.health.selectAll }}</span></label>
            <SettingsList v-if="items.length" :label="t.health.list">
              <SettingsListRow v-for="(item, index) in items" :key="item.pageId" :title="item.title" :description="rowText(item)" :tabindex="index === active ? 0 : -1" class="health-row" data-health-row :data-page="item.pageId" :data-selected="selected.has(item.pageId) || undefined" @keydown="rowKey($event, index)" @focusin="active = index">
                <template #leading><Checkbox :model-value="selected.has(item.pageId)" :aria-label="t.health.selectRow(item.title)" tabindex="-1" @update:model-value="toggle(item.pageId)" /></template>
                <template #meta>
                  <span class="health-detail">{{ rowDetail(item) }}</span>
                  <StatusBadge :tone="verificationTone(item.state)" :icon="verificationIcon(item.state)" :label="verificationLabel(item)" size="sm" :data-health-state="item.state" />
                </template>
                <template #actions>
                  <Button v-if="item.isPublished" variant="ghost" size="sm" type="button" :disabled="busy" data-health-row-verify @click="verify([item])"><AtlasIcon name="shield-check" :size="15" />{{ t.health.verify }}</Button>
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
    <AppDialog :title="t.health.stewardPick" :open="pickerOpen" @close="pickerOpen = false">
      <SearchableChoiceList v-model:query="peopleQuery" remote :loading="searching" :options="people" :label="t.health.stewardPick" :placeholder="t.stewardSearch" :empty-text="t.stewardEmpty" :max-height="260" @pick="pickSteward" />
    </AppDialog>
  </div>
</template>

<style scoped>
.health-tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; }
@container settings-shell (max-width: 760px) { .health-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
.health-tiles :deep(a.stat-tile) { cursor: pointer; }
.health-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.health-filters .search-field { flex: 1 1 220px; }
.health-bulk { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; padding: 8px 0 4px; font-size: 13px; }
.health-bulk kbd, .health-keys kbd { margin-left: 4px; padding: 0 4px; border-radius: 4px; background: color-mix(in srgb, currentColor 18%, transparent); font: 600 11px var(--font-mono, ui-monospace, monospace); }
.health-all { display: flex; align-items: center; gap: 10px; padding: 4px 0; color: var(--muted); font-size: 13px; }
.health-rows > .settings-list { margin: 0 calc(var(--st-pad-x, 18px) * -1) calc(var(--st-body-pad-y, 16px) * -1); --st-row-pad-x: var(--st-pad-x, 18px); }
.health-rows > .st-empty.is-compact { border-style: none; padding-block: 20px; }
.health-row { transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.health-row[data-selected] { background: color-mix(in srgb, var(--accent) 8%, transparent); }
@media (hover: hover) and (pointer: fine) { .health-row:hover { background: var(--hover); } }
.health-row:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.health-detail { color: var(--muted); font-size: 12.5px; }
.health-more { margin-top: 10px; }
.health-keys { margin: 12px 0 0; color: var(--muted); font-size: 12.5px; }
</style>
