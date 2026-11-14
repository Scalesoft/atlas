<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import type { BoardLinksShow } from "../../../boards/boardApi";
import { linksNames } from "../../../boards/boardFlow";
import { boardLinksApi, linkedPeople, scopeChoices, waitingOfPage, type BoardLinksApi } from "../../../boards/boardLinks";
import { liveObjects } from "../../../objects/liveObjects";
import { placesText, plainTitle, typeInfo, type ContentRow } from "../../../objects/relationsApi";
import { useGuardedLoad } from "../../../projector/overview/useGuardedLoad";
import WaitingList from "../../../projector/overview/WaitingList.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import LoadingRegion from "../../LoadingRegion.vue";
import UsagePanel from "../../objects/UsagePanel.vue";
import EmptyState from "../../settings/EmptyState.vue";
import ErrorState from "../../settings/ErrorState.vue";
import SearchableChoiceList from "../../settings/SearchableChoiceList.vue";
import SubjectAvatar from "../../settings/SubjectAvatar.vue";
import type { ChoiceOption } from "../../settings/types";
import "./boardCard.css";
import { pageTopic, useLiveFeed } from "../../../live/liveFeed";

/**
 * A list the board derives from the relations (`links`, P15 „Tabule ukazují vazby samy“), never written by hand: „Lidé projektu“
 * (the contacts the space's pages use, most used first, or those one page names), „Na co čekáme“ (Projector's WaitingList of the
 * space or of one page) or „Kde se používá“ (UsagePanel of one object). The rows are the shared views' own, read through the
 * reader's rights; a source the reader may not read is no row. The list reads again when the tab comes back and when the scope's page changes
 * (PageChanged on /hubs/live; nothing polls).
 *
 * `editing` (Enter, F2, a double-click; D1) chooses its scope in place: the space or one of its pages, or the object whose uses it
 * shows, in the shared searchable list (first result preselected, Enter takes it, the first Esc empties the search, the next one
 * ends the editing as a click outside does). A row opens its target by its own click or Enter (P1: a link inside an item).
 */
const props = withDefaults(defineProps<{ show: BoardLinksShow; objectId?: string | null; spaceId: string; editing?: boolean; api?: BoardLinksApi }>(),
  { objectId: null, editing: false, api: () => boardLinksApi });
const emit = defineEmits<{ done: []; scope: [objectId: string | null]; open: [id: string, typeId: string] }>();

const icons: Record<BoardLinksShow, string> = { people: "users", waiting: "hourglass", usage: "layers" };
const root = ref<HTMLElement>();
const target = computed(() => (props.objectId ? liveObjects.get(props.objectId) : undefined));
watch(() => props.objectId, id => { if (id) void liveObjects.load(id); }, { immediate: true });
const scopeText = computed(() => {
  if (!props.objectId) return props.show === "usage" ? t.linkCard.nothingSelected : t.linkCard.wholeSpace;
  if (!target.value) return t.linkCard.loading;
  return target.value.available ? plainTitle(target.value.title) || t.linkCard.untitled : t.linkCard.unavailable;
});

// Lidé projektu: one read, kept on screen while it reads again.
const people = useGuardedLoad(signal => linkedPeople(props.api, props.spaceId, props.objectId ?? undefined, signal));
// WaitingList reads the space; one page keeps only that page's items.
const waitingApi = computed(() => (props.objectId
  ? { ...props.api.waiting, waiting: (spaceId: string, signal?: AbortSignal) => props.api.waiting.waiting(spaceId, signal).then(list => waitingOfPage(list, props.objectId!)) }
  : props.api.waiting));
const waiting = ref<InstanceType<typeof WaitingList>>();
const usage = ref<InstanceType<typeof UsagePanel>>();
function refresh() {
  if (props.editing) return;
  if (props.show === "people") void people.load();
  else if (props.show === "waiting") waiting.value?.refresh();
  else usage.value?.refresh();
}
watch(() => [props.show, props.spaceId, props.objectId], () => { if (props.show === "people") { people.value.value = undefined; void people.load(); } }, { immediate: true });
// Read again when the scope's page changes (its PageChanged on /hubs/live) and when the tab comes back; nothing polls.
// shortcut: a whole-space scope has no channel and is read again only on the tab's return; add a space channel when it must follow every page.
useLiveFeed(() => (props.objectId ? pageTopic(props.objectId, "PageChanged") : undefined), refresh);
const visible = () => { if (document.visibilityState === "visible") refresh(); };
onMounted(() => document.addEventListener("visibilitychange", visible));
onBeforeUnmount(() => document.removeEventListener("visibilitychange", visible));

// Choosing the scope in place.
const query = ref("");
const choices = shallowRef<ContentRow[]>([]);
const loading = ref(false);
let searching: AbortController | undefined, pause: ReturnType<typeof setTimeout> | undefined;
async function search() {
  searching?.abort();
  const mine = searching = new AbortController();
  loading.value = true;
  try {
    const rows = await scopeChoices(props.api, props.spaceId, props.show, query.value, mine.signal);
    if (!mine.signal.aborted) choices.value = rows;
  } catch { /* the list keeps what it had; another letter asks again */ } finally {
    if (searching === mine) loading.value = false;
  }
}
watch(query, () => { clearTimeout(pause); loading.value = true; pause = setTimeout(search, 200); });
const options = computed<ChoiceOption[]>(() => [
  ...(props.show === "usage" || query.value.trim() ? [] : [{ value: "", label: t.linkCard.wholeSpace, icon: "book", description: t.linkCard.allPages }]),
  ...choices.value.map(row => ({ value: row.id, label: plainTitle(row.title) || t.linkCard.untitled, icon: typeInfo(row.typeId).icon, description: [typeInfo(row.typeId).label, row.path].filter(Boolean).join(" · ") })),
]);
function pick(value: string) {
  emit("scope", value || null);
  emit("done");
}
function outside(event: PointerEvent) {
  if (!root.value?.contains(event.target as Node)) emit("done");
}
watch(() => props.editing, now => {
  document.removeEventListener("pointerdown", outside, true);
  if (!now) { searching?.abort(); clearTimeout(pause); return; }
  query.value = "";
  void search();
  document.addEventListener("pointerdown", outside, true);
  void nextTick(() => root.value?.querySelector<HTMLInputElement>("[data-board-links-scope] input")?.focus({ preventScroll: true }));
}, { immediate: true });
onBeforeUnmount(() => { document.removeEventListener("pointerdown", outside, true); searching?.abort(); clearTimeout(pause); });
// The search's own first Esc empties it and stops there (clearOnEscape); the next one reaches this and ends the editing.
function escape(event: KeyboardEvent) {
  event.stopPropagation();
  emit("done");
}
defineExpose({ refresh });
</script>

<template>
  <article ref="root" class="bcard board-links" data-board-card="links" :data-show="show">
    <header class="bcard-head">
      <span class="bcard-icon" aria-hidden="true"><AtlasIcon :name="icons[show]" :size="18" /></span>
      <span class="bcard-heading">
        <strong class="bcard-title">{{ linksNames[show] }}</strong>
        <small class="bcard-muted" data-board-links-scope-name :title="scopeText">{{ scopeText }}</small>
      </span>
    </header>
    <div v-if="editing" class="board-links-scope nodrag nowheel nopan" data-board-links-scope @keydown.esc="escape">
      <SearchableChoiceList v-model:query="query" remote :loading="loading" :options="options" :model-value="objectId ?? ''"
        :label="show === 'usage' ? t.linkCard.watch : t.linkCard.scope" :placeholder="show === 'usage' ? t.linkCard.pickPlaceholder : t.linkCard.spacePage" :max-height="220" @pick="pick" />
    </div>
    <div v-else class="board-links-body nodrag nowheel" data-board-links-body>
      <template v-if="show === 'people'">
        <ErrorState v-if="people.error.value" :title="t.linkCard.peopleFailed" :text="people.error.value" :busy="people.loading.value" @retry="people.load" />
        <LoadingRegion v-else :loading="people.loading.value" :ready="!!people.value.value || people.denied.value" :rows="4" :label="t.linkCard.loadingPeople">
          <EmptyState v-if="!people.value.value?.length" compact icon="users" :title="t.linkCard.nobody"
            :text="t.linkCard.nobodyText" />
          <ul v-else class="board-links-people atlas-stagger" data-board-links-people>
            <li v-for="person in people.value.value" :key="person.id">
              <button type="button" class="board-links-person" :data-person="person.id" @click="emit('open', person.id, 'atlas.contact')">
                <SubjectAvatar kind="user" :name="person.name" size="sm" />
                <span class="board-links-name" :title="person.name">{{ person.name }}</span>
                <span class="bcard-muted" :title="t.linkCard.usedOn(placesText(person.uses))">{{ person.uses }}×</span>
              </button>
            </li>
          </ul>
        </LoadingRegion>
      </template>
      <WaitingList v-else-if="show === 'waiting'" :key="objectId ?? ''" ref="waiting" :space-id="spaceId" :api="waitingApi" :heading="false" />
      <template v-else>
        <UsagePanel v-if="objectId" ref="usage" :object-id="objectId" :api="api.usage" @open="(id, typeId) => emit('open', id, typeId)" />
        <EmptyState v-else compact icon="layers" :title="t.linkCard.chooseTitle"
          :text="t.linkCard.chooseText" />
      </template>
    </div>
  </article>
</template>

<style scoped>
.board-links { gap: 10px; }
.board-links-body, .board-links-scope { flex: 1 1 0; min-height: 0; overflow-y: auto; overscroll-behavior: contain; }
.board-links-people { display: grid; gap: 2px; margin: 0; padding: 0; list-style: none; }
/* D5: a row is at least a finger high; it looks like the usage panel's and the waiting list's rows. */
.board-links-person { display: flex; align-items: center; gap: 10px; width: 100%; min-height: var(--touch-target, 44px); padding: 6px 8px; border: 0; border-radius: var(--atlas-radius-sm);
  background: transparent; color: var(--text); font: inherit; text-align: left; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .board-links-person:hover { background: var(--hover); } }
.board-links-person:active { scale: var(--atlas-press); }
.board-links-person:focus-visible { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset, -2px); }
.board-links-name { flex: 1; min-width: 0; overflow: hidden; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
</style>
