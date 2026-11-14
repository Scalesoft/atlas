<script setup lang="ts">
import { observeMarkdownProximity } from "@atlas/markdown/core";
import { computed, nextTick, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { errorMessage } from "../../api";
import { startObjectDrag } from "../../boards/boardDrop";
import { rowKeys } from "../../keyboard/navigation";
import t from "../../i18n/components-objects";
import { kindInfo, plainTitle, typeInfo, usageApi, type UsageApi, type UsageCount, type UsagePage, type UsageRow } from "../../objects/relationsApi";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingRegion from "../LoadingRegion.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import { disclosureHooks } from "../settings/disclosure";
import { Button } from "../ui/button";

/**
 * „Použito na“ (plan 3.1 M5, U1): where ONE object is used, the same panel for a contact, a board, a page, a template and a catalog
 * requirement, read from the one usage service (GET /api/usage/{id}). Grouped by space, then by page or board; each place says jak
 * (zmínka, odkaz, karta na tabuli…, „koncept“ when only the working draft holds it) and proč (the text of the block around it). Places
 * the reader may not see are one line with their number, never a name. ↑ ↓ Home End walk the places, Enter or Space opens one (with
 * the card or node it is about), a place drags as its object. `still`: the rows only inform (the trash confirmations), nothing opens,
 * takes focus or drags. `collapsible`: one line „Použito na N místech“ from the cheap count, the list unfolds on request (page info).
 */
/** `part`: the uses of one part of the object only: a catalog requirement (its key) on its catalog page. */
const props = withDefaults(defineProps<{ objectId: string; part?: string; api?: UsageApi; still?: boolean; collapsible?: boolean }>(), { api: () => usageApi, still: false, collapsible: false });
const emit = defineEmits<{ open: [id: string, typeId: string, partId: string | null] }>();

const page = shallowRef<UsagePage | null>(null);
const count = shallowRef<UsageCount | null>(null);
const loading = ref(false), failed = ref(""), missing = ref(false);
const expanded = ref(!props.collapsible);
const root = ref<HTMLElement>();
let request: AbortController | undefined, seen: ReturnType<typeof observeMarkdownProximity> | undefined;

async function load(more = false) {
  request?.abort();
  const current = request = new AbortController();
  loading.value = true; failed.value = "";
  try {
    const answer = await props.api.usage(props.objectId, more ? page.value?.nextCursor : null, current.signal, props.part);
    if (current.signal.aborted) return;
    missing.value = !answer;
    page.value = answer && more && page.value ? { ...answer, items: [...page.value.items, ...answer.items] } : answer;
  } catch (failure) {
    if (!current.signal.aborted) failed.value = errorMessage(failure);
  } finally {
    if (!current.signal.aborted) loading.value = false;
  }
}
async function loadCount() {
  const id = props.objectId;
  try {
    const answer = await props.api.count(id, undefined, props.part);
    if (id === props.objectId) { count.value = answer; missing.value = !answer; }
  } catch {
    // The count only labels the disclosure; the list says what failed once it is asked for.
  }
}
/** A list mounts one collapsed panel per row: its count is asked for when the row comes into view (the toggle line has its size already, so nothing jumps). */
function whenSeen(run: () => void) {
  seen?.dispose();
  nextTick(() => {
    if (!root.value) return run();
    // The page's one proximity observer: within a viewport of the screen.
    seen = observeMarkdownProximity(root.value, () => run());
  });
}
watch(() => [props.objectId, props.part], () => {
  page.value = null; count.value = null; missing.value = false;
  if (expanded.value) void load(); else whenSeen(() => void loadCount());
}, { immediate: true });
watch(expanded, now => { if (now && !page.value) void load(); });
onBeforeUnmount(() => { request?.abort(); seen?.dispose(); });

interface Place { id: string; typeId: string; title: string; uses: UsageRow[] }
/** By space, then by place; the server's order (space, place, kind) is kept. */
const groups = computed(() => {
  const spaces = new Map<string, { key: string; name: string; places: Map<string, Place> }>();
  for (const use of page.value?.items ?? []) {
    const key = use.spaceId ?? "";
    const space = spaces.get(key) ?? spaces.set(key, { key, name: use.spaceName ?? t.usage.wholeWiki, places: new Map() }).get(key)!;
    const place = space.places.get(use.placeId) ?? space.places.set(use.placeId, { id: use.placeId, typeId: use.placeTypeId, title: plainTitle(use.placeTitle) || t.usage.untitled, uses: [] }).get(use.placeId)!;
    place.uses.push(use);
  }
  return [...spaces.values()].map(space => ({ key: space.key, name: space.name, places: [...space.places.values()] }));
});
const total = computed(() => page.value?.total ?? count.value?.total);
const summary = computed(() => (total.value === undefined ? t.usage.usedOn : total.value ? t.usage.usedOnPlaces(total.value) : t.usage.nowhere));
const kindText = (use: UsageRow) => `${use.card ? t.usage.contactCard : kindInfo[use.kind].use}${use.count > 1 ? ` ×${use.count}` : ""}`;
const excerpts = (place: Place) => [...new Set(place.uses.map(use => use.excerpt).filter((text): text is string => !!text))];
/** The card or node a jump opens: the first use that names one. */
const partOf = (place: Place) => place.uses.find(use => use.partId)?.partId ?? null;

/** Reads the first page again, the places on screen until the answer (a board's derived list, BoardLinks). */
defineExpose({ refresh: () => { if (expanded.value) void load(); } });

function open(place: Place) {
  if (!props.still) emit("open", place.id, place.typeId, partOf(place));
}
function drag(event: DragEvent, place: Place) {
  if (props.still) event.preventDefault();
  else startObjectDrag(event, { id: place.id.toLowerCase(), typeId: place.typeId, title: place.title });
}
</script>

<template>
  <div ref="root" class="usage-panel" data-usage-panel :data-still="still || undefined">
    <button v-if="collapsible" type="button" class="usage-toggle" :aria-expanded="expanded" data-usage-toggle @click="expanded = !expanded">
      <AtlasIcon name="layers" :size="15" />
      <span>{{ summary }}</span>
      <AtlasIcon class="usage-chevron" name="chevron-down" :size="15" />
    </button>
    <Transition :css="false" v-bind="disclosureHooks">
      <LoadingRegion v-if="expanded" :loading="loading" :ready="!!page || !!failed || missing" :rows="3" :label="t.usage.loading">
        <ErrorState v-if="failed" :title="t.usage.failed" :text="failed" :busy="loading" @retry="load()" />
        <EmptyState v-else-if="missing" compact icon="lock" :title="t.usage.missingTitle" :text="t.usage.missingText" />
        <template v-else-if="page">
          <EmptyState v-if="!page.total" compact icon="layers" :title="t.usage.nowhere" :text="t.usage.nowhereText" />
          <p v-else-if="!collapsible" class="usage-summary" aria-live="polite">{{ summary }}</p>
          <div v-if="page.items.length" class="usage-list" @keydown="!still && rowKeys($event, '.usage-space li')">
            <section v-for="space in groups" :key="space.key" class="usage-space" :aria-label="t.usage.space(space.name)">
              <h4><AtlasIcon :name="space.key ? 'book' : 'globe'" :size="13" />{{ space.name }}</h4>
              <ul class="atlas-stagger">
                <li v-for="place in space.places" :key="place.id">
                  <!-- Not a <button>: Firefox cannot drag one. Enter and Space still open it. -->
                  <div class="usage-place" data-usage-place :data-type="place.typeId" :role="still ? undefined : 'button'" :tabindex="still ? undefined : 0"
                    :draggable="!still" @click="open(place)" @keydown.enter.prevent="open(place)" @keydown.space.prevent="open(place)" @dragstart="drag($event, place)">
                    <AtlasIcon :name="typeInfo(place.typeId).icon" :size="15" />
                    <span class="usage-body">
                      <span class="usage-title" :title="place.title">{{ place.title }}</span>
                      <span class="usage-kinds">
                        <span v-for="use in place.uses" :key="use.kind" class="usage-kind" :data-kind="use.kind"
                          ><AtlasIcon :name="kindInfo[use.kind].icon" :size="12" />{{ kindText(use) }}<span v-if="use.draft" class="usage-draft" data-usage-draft>{{ t.usage.draft }}</span></span>
                      </span>
                      <q v-for="text in excerpts(place)" :key="text" class="usage-excerpt">{{ text }}</q>
                    </span>
                  </div>
                </li>
              </ul>
            </section>
          </div>
          <p v-if="page.hidden" class="usage-hidden" data-usage-hidden><AtlasIcon name="lock" :size="14" />{{ t.usage.hidden(page.items.length > 0, page.hidden) }}</p>
          <Button v-if="page.nextCursor" variant="outline" size="sm" class="usage-more" :disabled="loading" data-usage-more @click="load(true)">{{ t.usage.loadMore }}</Button>
        </template>
      </LoadingRegion>
    </Transition>
  </div>
</template>

<style scoped>
.usage-panel { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
.usage-panel :deep(.loading-region-content) { display: flex; flex-direction: column; gap: 12px; }
.usage-toggle { display: flex; align-items: center; gap: 8px; width: 100%; padding: 8px 10px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px;
  background: var(--surface); color: var(--text); font: inherit; font-size: 13px; text-align: left; cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.usage-toggle > span { flex: 1; min-width: 0; }
.usage-toggle > svg { flex: none; color: var(--muted); }
.usage-chevron { rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.usage-toggle[aria-expanded="true"] .usage-chevron { rotate: 0deg; }
@media (hover: hover) and (pointer: fine) { .usage-toggle:hover { border-color: var(--line); background: color-mix(in srgb, var(--hover) 50%, var(--surface)); } }
.usage-toggle:focus-visible, .usage-place:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.usage-summary { margin: 0; padding: 0 2px; color: var(--muted); font-size: 12px; font-weight: 650; }
.usage-list { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
.usage-space { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.usage-space h4 { display: flex; align-items: center; gap: 6px; margin: 0; padding: 0 2px; color: var(--muted); font-size: 12px; font-weight: 650; }
.usage-space ul { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
.usage-place { display: flex; align-items: flex-start; gap: 10px; width: 100%; padding: 9px 12px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px;
  background: var(--surface); color: var(--text); cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
[data-still] .usage-place { cursor: default; }
@media (hover: hover) and (pointer: fine) { .usage-panel:not([data-still]) .usage-place:hover { border-color: var(--line); background: color-mix(in srgb, var(--hover) 50%, var(--surface)); } }
.usage-place > svg { flex: none; margin-top: 2px; color: var(--muted); }
.usage-body { display: flex; flex: 1; flex-direction: column; gap: 3px; min-width: 0; }
.usage-title { overflow: hidden; font-size: 13px; text-overflow: ellipsis; white-space: nowrap; }
.usage-kinds { display: flex; flex-wrap: wrap; gap: 4px 10px; color: var(--muted); font-size: 12px; }
.usage-kind { display: inline-flex; align-items: center; gap: 4px; }
.usage-draft { margin-left: 4px; padding: 0 6px; border-radius: 6px; background: var(--warning-bg, var(--hover)); color: var(--warning, var(--muted)); font-size: 11px; font-weight: 600; }
.usage-excerpt { display: -webkit-box; overflow: hidden; color: var(--muted); font-size: 12px; font-style: italic; line-height: 1.45; -webkit-box-orient: vertical; -webkit-line-clamp: 2; }
.usage-hidden { display: flex; align-items: center; gap: 6px; margin: 0; padding: 0 2px; color: var(--muted); font-size: 12px; }
.usage-more { align-self: flex-start; }
@media (prefers-reduced-motion: reduce) { .usage-chevron { transition: none; } }
</style>
