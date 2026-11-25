<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onBeforeUpdate, ref, shallowRef, watch } from "vue";
import type { Space } from "../api";
import RelativeTime from "./RelativeTime.vue";
import { readStored, writeStored } from "../lib/browserStorage";
import { foldText } from "../lib/foldText";
import { isPlainNavigationClick, spaceLink } from "../pageLinks";
import { roleLabel } from "../roles";
import { comparePinnedFirst, isSpacePinned } from "../spacePins";
import AtlasIcon from "./AtlasIcon.vue";
import { Button } from "./ui/button";
import EmptyState from "./settings/EmptyState.vue";
import FilterMenu from "./settings/FilterMenu.vue";
import OptionPills from "./settings/OptionPills.vue";
import SearchField from "./settings/SearchField.vue";
import t from "../i18n/components-spaces";
import { formatNumber } from "../i18n";
import type { ChoiceOption } from "./settings/types";
import SpaceCard from "./library/SpaceCard.vue";
import SpacePinToggle from "./library/SpacePinToggle.vue";
import { leaveInPlace } from "./library/leaveInPlace";
import { useStageHeight } from "./library/stageHeight";
import { cardKeys, rowKeys } from "../keyboard/navigation";
// A workspace view outside the settings shell: it brings the design system's card and motion rules itself.
import "./settings/settings.css";

/**
 * Knihovna → Prostory (refresh 2.5, boards 01 and 14): every space the account sees, with search, a filter
 * (pinned or by the account's access) and a pin toggle per space, as cards with the space's cover or as an
 * aligned list. The choice is remembered per account under `storageKey`, like the other reading preferences.
 * On a phone the spaces are compact rows, the pinned ones first and the rest behind Všechny prostory.
 * Presentational: the shell owns the list, the pin request (`pendingIds` while it runs) and routing.
 */
const props = withDefaults(defineProps<{
  spaces: Space[];
  canCreate: boolean;
  /** The account administers at least one space: a note says where a space is pinned for everyone. */
  canManage?: boolean;
  pendingIds?: ReadonlySet<string>;
  limit?: number;
  /** localStorage key of the Karty / Seznam choice (`atlas:reading:<account>:library-view`); none keeps it for the session. */
  storageKey?: string;
}>(), {
  pendingIds: () => new Set<string>(),
  limit: 200,
  storageKey: undefined,
});
const emit = defineEmits<{ open: [space: Space]; pin: [space: Space, pinned: boolean]; create: [] }>();

const query = ref("");
const show = ref("");
/** On a phone, the unpinned spaces after Všechny prostory. */
const showAll = ref(false);

/* Pinned always first (the order of the sidebar, spacePins.ts): a space the account pins glides to
   the top and one it unpins back among the rest. Under the Připnuté filter an unpinned space stays
   until the filter or the search changes, so it does not vanish under the pointer: membership uses
   the pin state from when the list was last filtered, and a space that appears later joins with its
   current state. */
const listedPinned = shallowRef(new Map<string, boolean>());
watch(() => props.spaces, (spaces) => {
  if (spaces.every((space) => listedPinned.value.has(space.id))) return;
  const next = new Map(listedPinned.value);
  for (const space of spaces) if (!next.has(space.id)) next.set(space.id, space.pinned);
  listedPinned.value = next;
}, { immediate: true });
watch([query, show], () => {
  listedPinned.value = new Map(props.spaces.map((space) => [space.id, space.pinned]));
  showAll.value = false;
});

const searchable = computed(() => props.spaces.map((space) => ({ space, key: foldText(`${space.name} ${space.description ?? ""}`) })));
function matchesShow(space: Space) {
  if (!show.value) return true;
  if (show.value === "pinned") return listedPinned.value.get(space.id) ?? space.pinned;
  return space.role === show.value;
}
const shown = computed(() => {
  const terms = foldText(query.value.trim()).split(/\s+/).filter(Boolean);
  return searchable.value
    .filter(({ space, key }) => matchesShow(space) && terms.every((term) => key.includes(term)))
    .map(({ space }) => space)
    .sort(comparePinnedFirst);
});
const filtered = computed(() => !!query.value.trim() || !!show.value);
const pinnedCount = computed(() => props.spaces.filter((space) => space.pinned).length);
// The access filters appear only for roles the account actually has somewhere.
const roleOptions = (): ChoiceOption[] => [
  { value: "Admin", label: t.directory.managed, icon: "shield-check" },
  { value: "Writer", label: t.directory.editable, icon: "pencil" },
  { value: "Reader", label: t.directory.readOnly, icon: "eye" },
];
const showOptions = computed<ChoiceOption[]>(() => [
  { value: "", label: t.directory.all, icon: "book-open", badge: String(props.spaces.length) },
  { value: "pinned", label: t.directory.pinned, icon: "pin", badge: String(pinnedCount.value) },
  ...roleOptions()
    .map((option) => ({ ...option, badge: String(props.spaces.filter((space) => space.role === option.value).length) }))
    .filter((option) => option.badge !== "0" || show.value === option.value),
]);
const countText = computed(() => (filtered.value
  ? t.directory.countOf(shown.value.length, props.spaces.length)
  : t.directory.countPinnedFirst(props.spaces.length)));
const noMatchText = computed(() => {
  const text = query.value.trim();
  if (text) return t.directory.noMatchSearch(show.value === "pinned", text);
  if (show.value === "pinned") return t.directory.noPinned;
  return t.directory.noneInSelection;
});

/* Karty / Seznam, remembered per account. Storage may be unavailable (private mode); the choice then lasts the visit. */
type Layout = "cards" | "list";
const viewOptions = (): ChoiceOption[] => [
  { value: "cards", label: t.directory.cards, icon: "layout-dashboard" },
  { value: "list", label: t.directory.list, icon: "list" },
];
function storedView(): Layout {
  return props.storageKey && readStored(props.storageKey) === "list" ? "list" : "cards";
}
const view = ref<Layout>(storedView());
watch(() => props.storageKey, () => { view.value = storedView(); });
watch(view, (value) => {
  if (props.storageKey) writeStored(props.storageKey, value);
});
const viewModel = computed({ get: () => view.value, set: (value: string) => { view.value = value === "list" ? "list" : "cards"; } });

/* A phone shows compact rows (board 14): the pinned spaces, the rest one tap away. */
const phone = typeof window.matchMedia === "function" ? window.matchMedia("(max-width: 680px)") : undefined;
const narrow = ref(!!phone?.matches);
const onPhoneChange = (event: MediaQueryListEvent) => { narrow.value = event.matches; };
phone?.addEventListener?.("change", onPhoneChange);
onBeforeUnmount(() => phone?.removeEventListener?.("change", onPhoneChange));
const layout = computed<Layout>(() => (narrow.value ? "list" : view.value));
const pinnedShown = computed(() => shown.value.filter(isSpacePinned));
const collapsed = computed(() => narrow.value && !showAll.value && !filtered.value && pinnedShown.value.length > 0 && pinnedShown.value.length < shown.value.length);
const rows = computed(() => (collapsed.value ? pinnedShown.value : shown.value));

function descriptionOf(space: Space) {
  if (space.itemAccessOnly) return t.directory.selectedPagesOnly;
  return space.description?.trim() || t.directory.noDescription;
}
const pages = t.directory.pages;

function open(space: Space, event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("open", space);
}
/** Moving a space moves its node, which drops the focus of its toggle: the toggle gets it back, without scrolling. */
async function togglePin(space: Space, event: MouseEvent) {
  if (space.pinPolicy === "Required" || props.pendingIds.has(space.id)) return;
  const toggle = event.currentTarget as HTMLElement;
  const focused = document.activeElement === toggle;
  emit("pin", space, !space.pinned);
  await nextTick();
  if (focused && toggle.isConnected && document.activeElement !== toggle) toggle.focus({ preventScroll: true });
}
function reset() {
  query.value = "";
  show.value = "";
}
/** The view that is fading out is a still picture: nothing in it takes focus or a click meanwhile; the stage eases to the new height. */
const stage = ref<HTMLElement>();
const { onBeforeLeave, onEnter } = useStageHeight(stage);
function retire(element: Element) {
  onBeforeLeave();
  element.setAttribute("inert", "");
  element.setAttribute("aria-hidden", "true");
}
/* A space that drops out of the search fades where it was; leaving the flow at once lets the others glide into its place.
   Its box is read before the update: two spaces leaving together would otherwise find the first one's place already taken. */
const boxes = new WeakMap<Element, string>();
onBeforeUpdate(() => {
  for (const node of stage.value?.querySelectorAll<HTMLElement>(".space-directory-grid > li, .space-directory-rows > .space-directory-row") ?? []) {
    boxes.set(node, `left:${node.offsetLeft}px;top:${node.offsetTop}px;width:${node.offsetWidth}px;height:${node.offsetHeight}px`);
  }
});
function leaving(element: Element) {
  const box = boxes.get(element);
  if (box) (element as HTMLElement).style.cssText += `;${box}`;
}
</script>

<template>
  <section class="space-directory settings-ui" :aria-label="t.directory.region" :data-layout="layout">
    <template v-if="spaces.length">
      <div class="space-directory-toolbar">
        <SearchField v-model="query" :label="t.directory.search" :placeholder="t.directory.searchPlaceholder" data-space-search />
        <FilterMenu v-model="show" :options="showOptions" :label="t.directory.show" data-space-filter />
        <span class="space-directory-count" role="status">{{ countText }}</span>
        <OptionPills v-if="!narrow" segmented v-model="viewModel" class="space-directory-view" :options="viewOptions()" :label="t.directory.view" hide-label data-library-view />
      </div>

      <p v-if="collapsed" class="space-directory-section">{{ t.directory.pinned }}</p>
      <!-- Karty and Seznam cross-fade in one grid cell; inside, a space that drops out of the search fades where it was. -->
      <Transition name="space-stage" @before-leave="leaveInPlace">
      <div v-if="shown.length" ref="stage" class="space-directory-stage">
        <Transition name="space-view" @before-leave="retire" @enter="onEnter">
          <div v-if="layout === 'cards'" key="cards" class="space-directory-view-part">
            <TransitionGroup tag="ul" name="space-tile" class="space-directory-grid" :aria-label="t.directory.listLabel" @before-leave="leaving" @keydown="cardKeys($event, '.space-directory-grid > li')">
              <li v-for="(space, index) in rows" :key="space.id">
                <SpaceCard :space="space" :href="spaceLink(space.id)" :index="index" :data-space="space.id" @navigate="open(space, $event)">
                  <template #corner><SpacePinToggle :space="space" :pending="pendingIds.has(space.id)" @toggle="togglePin(space, $event)" /></template>
                </SpaceCard>
              </li>
            </TransitionGroup>
          </div>
          <div v-else key="list" class="space-directory-view-part">
            <div class="space-directory-table" role="table" :aria-label="t.directory.listLabel">
              <div class="space-directory-head" role="row">
                <span role="columnheader">{{ t.directory.colSpace }}</span>
                <span role="columnheader" class="is-number">{{ t.directory.colPages }}</span>
                <span role="columnheader">{{ t.directory.colAccess }}</span>
                <span role="columnheader">{{ t.directory.colChanged }}</span>
                <span role="columnheader"><span class="sr-only">{{ t.directory.colPin }}</span></span>
              </div>
              <TransitionGroup tag="div" name="space-tile" role="rowgroup" class="space-directory-rows" @before-leave="leaving" @keydown="rowKeys($event, '.space-directory-row')">
                <div v-for="(space, index) in rows" :key="space.id" class="space-directory-row" role="row" :data-space="space.id" :style="{ '--i': Math.min(index, 8) }">
                  <span role="cell" class="space-directory-name">
                    <span class="space-icon" :data-color="space.color" aria-hidden="true"><AtlasIcon :name="space.icon" :size="narrow ? 18 : 16" /></span>
                    <span class="space-directory-text">
                      <a class="space-directory-link" :href="spaceLink(space.id)" :title="space.name" @click="open(space, $event)">{{ space.name }}</a>
                      <small v-if="!narrow" :class="{ 'is-empty': !space.itemAccessOnly && !space.description?.trim() }" :title="descriptionOf(space)">{{ descriptionOf(space) }}</small>
                      <small v-else>{{ pages(space.pageCount ?? 0) }}<template v-if="space.lastChangedAt"> · <RelativeTime :value="space.lastChangedAt" /></template></small>
                    </span>
                  </span>
                  <span role="cell" class="is-number" data-space-pages>{{ formatNumber(space.pageCount ?? 0) }}</span>
                  <span role="cell"><span class="space-directory-role" :title="t.directory.yourAccess(roleLabel(space.role))" data-space-access-level>{{ roleLabel(space.role) }}</span></span>
                  <span role="cell" class="space-directory-changed"><RelativeTime v-if="space.lastChangedAt" :value="space.lastChangedAt" /></span>
                  <span role="cell" class="space-directory-pin"><SpacePinToggle :space="space" :pending="pendingIds.has(space.id)" @toggle="togglePin(space, $event)" /></span>
                </div>
              </TransitionGroup>
            </div>
          </div>
        </Transition>
      </div>
      </Transition>
      <Button v-if="collapsed" variant="outline" class="space-directory-all" data-space-show-all @click="showAll = true">{{ t.directory.allSpaces(formatNumber(shown.length)) }}</Button>
      <Transition name="space-stage" @before-leave="leaveInPlace">
      <EmptyState v-if="!shown.length" compact :icon="query.trim() ? 'search' : 'pin'" :title="t.directory.nothingFound" :text="noMatchText" data-space-no-match>
        <Button variant="outline" type="button" data-space-reset @click="reset"><AtlasIcon name="close" :size="16" />{{ query.trim() ? t.directory.clearSearch : t.directory.showAll }}</Button>
      </EmptyState>
      </Transition>

      <p v-if="spaces.length >= limit" class="st-note space-directory-note" data-space-limit>
        <AtlasIcon name="info" :size="15" />{{ t.directory.limit(limit) }}
      </p>
      <p v-if="canManage" class="st-note space-directory-note" data-space-policy-hint>
        <AtlasIcon name="users" :size="15" />{{ t.directory.policyHint }}
      </p>
    </template>

    <EmptyState
      v-else
      icon="book-open"
      :title="canCreate ? t.directory.emptyTitleCreate : t.directory.emptyTitle"
      :text="canCreate ? t.directory.emptyTextCreate : t.directory.emptyText"
      data-space-empty
    >
      <Button v-if="canCreate" type="button" @click="emit('create')"><AtlasIcon name="plus" :size="16" />{{ t.directory.newSpace }}</Button>
    </EmptyState>
  </section>
</template>

<style scoped>
.space-directory { container: space-directory / inline-size; position: relative; display: grid; gap: 16px; min-width: 0; color: var(--text); }

/* One line (board 01): search, filter, then the count and Karty / Seznam on the right. */
.space-directory-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
/* SearchField passes attributes to its input, so its frame is reached by its own class. */
.space-directory-toolbar > .search-field { flex: 1 1 220px; max-width: 340px; }
.space-directory-toolbar :deep(.search-field-input) { min-height: 36px; border-radius: 10px; font-size: 14px; }
.space-directory-toolbar :deep(.filter-menu-trigger) { min-height: 36px; padding: 0 12px; border-radius: 10px; font-size: 14px; }
.space-directory-count { margin-left: auto; color: var(--muted); font-size: 13px; white-space: nowrap; font-variant-numeric: tabular-nums; }

/* Karty and Seznam share one grid cell while they cross-fade (board 16: the content is only blended, no shift). */
.space-directory-stage { display: grid; grid-template-columns: minmax(0, 1fr); min-width: 0; }
.space-directory-view-part { grid-area: 1 / 1; align-self: start; min-width: 0; }
.space-view-enter-active { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.space-view-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }

.space-directory-grid { position: relative; display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr)); gap: 16px; margin: 0; padding: 0; list-style: none; }
.space-directory-grid > li { display: grid; min-width: 0; }
.space-directory-rows { position: relative; }
/* A space whose pin changed glides to its new place; the others make room. */
.space-tile-move { transition: transform var(--atlas-motion-slow) var(--atlas-ease-out); }
@media (prefers-reduced-motion: reduce) { .space-tile-move { transition: none; } }

/* Seznam: the aligned table of board 02, one row per space. */
.space-directory-table { overflow: hidden; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
.space-directory-head, .space-directory-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 88px 128px 148px 36px;
  align-items: center;
  gap: 16px;
  padding: 0 12px 0 16px;
}
.space-directory-head { height: 36px; border-bottom: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); background: color-mix(in srgb, var(--line) 16%, var(--surface)); color: var(--muted); font-size: 12px; font-weight: 600; }
.space-directory-row {
  position: relative;
  min-height: 56px;
  border-bottom: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface));
  font-size: 13px;
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards;
  animation-delay: calc(var(--i, 0) * var(--st-stagger));
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.space-directory-row:last-child { border-bottom: 0; border-radius: 0 0 13px 13px; } /* the frame's inner radius (14 − 1), so the focus ring of the last row follows the frame */
@media (hover: hover) and (pointer: fine) {
  .space-directory-row:hover { background: color-mix(in srgb, var(--hover) 45%, transparent); }
}
.space-directory-row:has(.space-directory-link:focus-visible) { outline: 2px solid var(--accent); outline-offset: -2px; }
/* The row shows the ring, not the shell's ring around the name. */
.space-directory-row .space-directory-link:focus-visible:not(.md-editor):not(.md-editor *) { outline: none; }
.is-number { text-align: right; font-variant-numeric: tabular-nums; }
.space-directory-name { display: flex; align-items: center; gap: 12px; min-width: 0; }
.space-directory .space-directory-name .space-icon { width: 30px; height: 30px; border-radius: 9px; }
.space-directory-text { display: grid; gap: 1px; min-width: 0; }
.space-directory-link { overflow: hidden; color: var(--text); font-size: 14px; font-weight: 600; text-decoration: none; text-overflow: ellipsis; white-space: nowrap; outline: none; }
/* The name is the row's link, stretched over it; the pin toggle stays above. */
.space-directory-link::after { content: ""; position: absolute; inset: 0; }
.space-directory-text small { overflow: hidden; color: var(--muted); font-size: 12.5px; text-overflow: ellipsis; white-space: nowrap; }
.space-directory-text small.is-empty { font-style: italic; opacity: .8; }
.space-directory-role { padding: 1px 7px; border-radius: 6px; background: color-mix(in srgb, var(--line) 50%, var(--surface)); color: color-mix(in srgb, var(--text) 82%, var(--muted)); font-size: 12px; font-weight: 600; }
.space-directory-changed { color: var(--muted); white-space: nowrap; font-variant-numeric: tabular-nums; }
.space-directory-pin { position: relative; z-index: 1; display: flex; justify-content: flex-end; }
.space-directory-pin :deep(.space-pin) { box-shadow: none; background: transparent; }
@media (hover: hover) and (pointer: fine) {
  .space-directory-row:hover .space-directory-pin :deep(.space-pin) { background: var(--surface); }
}
/* The list and its empty state give way to each other without holding their place: the leaving one is pinned where it
   stands (leaveInPlace.ts) and fades while the other one comes in. Outranks the empty state's own entrance animation. */
.space-directory :is(.space-directory-stage, .st-empty).space-stage-leave-active { position: absolute; z-index: 0; pointer-events: none; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; }
/* A space that drops out of the search fades where it was (the others glide over; SpaceDirectory.vue `leaving`). */
.space-tile-leave-active { position: absolute; z-index: 0; pointer-events: none; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; }

.space-directory-section { margin: 4px 0 -8px; color: var(--muted); font-size: 12px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.space-directory-all { justify-self: stretch; min-height: 44px; border-radius: 12px; font-weight: 600; }
.space-directory-note { margin: 0; }

/* Phone (board 14): rows without the table frame, two lines each, the pin as the trailing control. */
@container space-directory (max-width: 640px) {
  .space-directory-head { display: none; }
  .space-directory-row { grid-template-columns: minmax(0, 1fr) 40px; gap: 8px; padding: 8px 12px; }
  .space-directory-row > [role="cell"]:not(.space-directory-name, .space-directory-pin) { display: none; }
  .space-directory-toolbar > .search-field { flex-basis: 100%; max-width: none; }
  .space-directory-count { margin-left: 0; white-space: normal; }
}
@media (max-width: 680px) {
  .space-directory { gap: 12px; }
  .space-directory-toolbar > .search-field { flex: 1 1 100%; max-width: none; }
  .space-directory-count { margin-left: 0; }
  .space-directory-table { border: 0; border-radius: 0; background: transparent; overflow: visible; }
  .space-directory-row:last-child { border-radius: 0; }
  .space-directory-head { display: none; }
  .space-directory-row { grid-template-columns: minmax(0, 1fr) 44px; gap: 8px; min-height: 56px; padding: 8px 0 8px 4px; }
  .space-directory-row > [role="cell"]:not(.space-directory-name, .space-directory-pin) { display: none; }
  .space-directory .space-directory-name .space-icon { width: 36px; height: 36px; border-radius: 10px; }
  .space-directory-link { font-size: 15px; }
  .space-directory-text small { font-size: 13px; }
  .space-directory-pin { justify-content: center; }
  .space-directory-pin :deep(.space-pin) { width: 40px; height: 40px; }
  /* The pin-for-everyone hint belongs to a desk, not to a quick look on the phone. */
  .space-directory-note[data-space-policy-hint] { display: none; }
}
</style>
