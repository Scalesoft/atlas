<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import LoadingSkeleton from "../components/LoadingSkeleton.vue";
import t from "../i18n/overview";
import { boardDocument, overviewKind, type OverviewBoardItem, type OverviewJson } from "./overviewKinds";

/**
 * An overview where no editor holds it: a page that is read (`document`, overviewReading.ts) or a block on a board (`board`,
 * BoardBlock.vue). The same kinds as in the editor: the kind's `collect` over the stored document, or over the board as one document
 * (`boardDocument`: its blocks, then what its other items stand for); the kind's `readCard` shows the result with the level of
 * `scope` (the page's or the board's id: one state for every overview of the kind there and the root's `data-overview-<kind>`), and
 * the kind's actions that need no page editor go to its foot. A skeleton while a board's items are read (a live contact, a
 * product's page). Why not OverviewView: that one is the editor's node view and reads the editor's live document.
 */
const props = defineProps<{ kind: string; scope: string; document?: OverviewJson; board?: { items(): readonly OverviewBoardItem[] } }>();
const kind = computed(() => overviewKind(props.kind));
const items = shallowRef<readonly unknown[] | null>(null);
// What the overview reads of a board's items (those its kind reads), without their places: moving a card collects nothing again.
const inputs = computed(() => props.board ? JSON.stringify(props.board.items().filter(item => !item.deleted && (kind.value?.boardReads?.(item) ?? true))
  .map(({ kind: type, text, props: data, targetResourceId }) => [type, text, data, targetResourceId])) : props.document);
let run = 0;
watch([kind, inputs], async ([current]) => {
  const turn = ++run;
  if (!current) { items.value = []; return; }
  const document = props.board ? await boardDocument(current, props.board.items()).catch((): OverviewJson => ({ type: "doc" })) : props.document ?? { type: "doc" };
  if (turn === run) items.value = current.collect(document);
}, { immediate: true });
const actions = computed(() => (kind.value?.actions ?? []).filter(action => !action.pageOnly)
  .map(action => ({ id: action.id, label: action.label, icon: action.icon, run: () => action.run({ items: items.value ?? [] }) })));
</script>

<template>
  <div class="overview-read-card" :data-overview-kind="props.kind" contenteditable="false">
    <p v-if="!kind" class="atlas-overview">{{ t.unavailable }}</p>
    <LoadingSkeleton v-else-if="!items" :rows="2" row-height="44px" :label="t.loading(kind.title ?? kind.label)" />
    <component :is="kind.readCard" v-else :items="items" :scope="scope" :actions="items.length ? actions : []" />
  </div>
</template>

<style scoped>
.overview-read-card { width: 100%; min-width: 0; }
</style>
