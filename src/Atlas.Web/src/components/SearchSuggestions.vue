<script lang="ts">
import type { LibraryItem } from "../personalLibrary";
import t from "../i18n/components-c";

type Group = { title: string; icon: string; items: LibraryItem[] };
/** The last answer, kept for the next opening of the search (P17): the recent pages show at once and the request only refines them. Per user. */
let known: { user: string; groups: Group[] } | undefined;
/**
 * A page was just opened: it moves to the top of the cached recent pages, where the server will list it, so the refresh of the next
 * opening finds the rows where they are and nothing shifts under the arrow keys. A page the cache does not know is left to the refresh.
 */
export function noteVisit(user: string, id: string) {
  if (known?.user !== user) return;
  const item = known.groups.flatMap(group => group.items).find(entry => entry.id === id);
  if (!item) return;
  const recent = known.groups.find(group => group.title === t.suggestions.recent);
  const others = known.groups.filter(group => group !== recent);
  known = { user, groups: [{ title: t.suggestions.recent, icon: "page", items: [item, ...(recent?.items ?? []).filter(entry => entry.id !== id)].slice(0, 5) }, ...others] };
}
</script>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import { personalLibraryApi } from "../personalLibrary";
import { errorMessage } from "../api";
import AtlasIcon from "./AtlasIcon.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import EmptyState from "./settings/EmptyState.vue";
import ErrorState from "./settings/ErrorState.vue";
import { isPlainNavigationClick, pageLink } from "../pageLinks";
/**
 * The recent and favourite pages under the empty search. The first row is preselected and Enter opens it (F13); `activeIndex` -1 says
 * the arrows have not moved yet, so a refresh keeps the first row preselected instead of following a page. The pages of the last
 * opening are drawn in the first frame without the rows' rise-in (F33: clearing the search replayed it); a skeleton of the same rows
 * stands in only when none is known yet.
 */
const props = withDefaults(defineProps<{ activeIndex?: number; userId?: string }>(), { activeIndex: 0, userId: "" });
const emit = defineEmits<{ open: [id: string]; loaded: [items: LibraryItem[]]; select: [index: number] }>();
const earlier = known?.user === props.userId ? known.groups : undefined;
const groups = ref<Group[]>(earlier ?? []);
const indexedGroups = computed(() => groups.value.map((group, groupIndex) => ({ ...group, items: group.items.map((item, index) => ({ ...item, index: groups.value.slice(0, groupIndex).reduce((sum, g) => sum + g.items.length, 0) + index })) })));
const loading = ref(!earlier), error = ref("");
let pending: AbortController | undefined;
if (earlier) emit("loaded", earlier.flatMap(group => group.items));
async function load() {
  pending?.abort();
  const controller = new AbortController(); pending = controller;
  error.value = "";
  try {
    const [recent, favorites] = await Promise.all([
      personalLibraryApi.list("recent", 0, controller.signal, 5),
      personalLibraryApi.list("favorites", 0, controller.signal, 5),
    ]);
    if (controller.signal.aborted) return;
    // The row the keyboard is on stays the same page when the refresh puts others above it.
    const chosen = props.activeIndex >= 0 ? groups.value.flatMap(group => group.items)[props.activeIndex]?.id : undefined;
    groups.value = [
      { title: t.suggestions.recent, icon: "page", items: recent.items },
      { title: t.suggestions.favorites, icon: "star", items: favorites.items },
    ].filter(group => group.items.length > 0);
    known = { user: props.userId, groups: groups.value };
    const items = groups.value.flatMap(group => group.items), now = chosen === undefined ? -1 : items.findIndex(item => item.id === chosen);
    emit("loaded", items);
    if (now >= 0 && now !== props.activeIndex) emit("select", now);
  } catch (failure) {
    // A refresh that fails leaves the pages shown; only a first opening has nothing else to say.
    if (!controller.signal.aborted && !earlier) error.value = errorMessage(failure);
  } finally {
    if (!controller.signal.aborted) loading.value = false;
  }
}
// The list settling beneath a resting pointer must not override the keyboard selection (the results list does the same).
function pointerSelect(event: PointerEvent, index: number) {
  if (!event.movementX && !event.movementY) return;
  emit('select', index);
}
function open(event: MouseEvent, id: string) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault(); emit("open", id);
}
onBeforeUnmount(() => pending?.abort());
void load();
</script>
<template>
  <section class="search-suggestions" :aria-label="t.suggestions.region" :aria-busy="loading">
    <LoadingSkeleton v-if="loading" :rows="3" row-height="52px" :label="t.suggestions.loading" />
    <ErrorState v-else-if="error" :title="t.suggestions.loadFailed" :text="error" :retry-label="t.suggestions.retry" @retry="load" />
    <EmptyState v-else-if="!groups.length" compact icon="search" :title="t.suggestions.minTitle" :text="t.suggestions.minText" />
    <div v-else id="search-suggestions-results" role="listbox" :aria-label="t.suggestions.region">
      <section v-for="group in indexedGroups" :key="group.title" class="search-group" :class="{ 'atlas-stagger': !earlier }" role="group" :aria-label="group.title">
        <h3 class="search-group-title">{{ group.title }}<span>{{ group.items.length }}</span></h3>
        <a v-for="item in group.items" :key="item.id" class="search-result" :id="`suggested-result-${item.index}`" role="option" :aria-selected="item.index === Math.max(props.activeIndex, 0)" :tabindex="item.index === Math.max(props.activeIndex, 0) ? 0 : -1" :class="{ active: item.index === Math.max(props.activeIndex, 0) }" @pointermove="pointerSelect($event, item.index)" @focus="emit('select', item.index)" @mousedown.prevent :href="pageLink(item.id)" @click="open($event, item.id)">
          <span class="space-icon" data-color="blue"><AtlasIcon :name="group.icon" :size="18" /></span><span><strong>{{ item.title }}</strong><small>{{ item.spaceName }}<template v-if="!item.isPublished"> · {{ t.suggestions.draft }}</template></small></span>
        </a>
      </section>
    </div>
  </section>
</template>
<style scoped>
@media (hover: hover) and (pointer: fine) { a:hover { background: var(--accent-soft); } }
a:focus-visible { background: var(--accent-soft); outline: var(--focus-ring); outline-offset: var(--focus-offset); }
button { margin-left: 12px; }
</style>
