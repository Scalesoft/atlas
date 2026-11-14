<script setup lang="ts">
import { computed, nextTick, onMounted, ref, shallowRef, watch } from "vue";
import { startObjectDrag } from "../../boards/boardDrop";
import { isGroupId, openableId, relationItems } from "../../objects/relationGraph";
import { plainTitle, type RelationGraph, type RelationNode } from "../../objects/relationsApi";
import t from "../../i18n/components-objects";
import BoardCanvas from "../boards/BoardCanvas.vue";
import EmptyState from "../settings/EmptyState.vue";

/**
 * The relations of an object as a graph. It is BoardCanvas, read-only,
 * over items derived from the relations (objects/relationGraph.ts): no graph library, no SVG of its own. The canvas already brings
 * hovering that lights the neighbourhood (Shift: the whole chain), the path between two selected nodes, the merged connectors of
 * collapsed groups, the enter/leave/glide motion and the keys (arrows walk to the nearest node, Tab, Ctrl+Enter). This adds what the
 * relations need: a click on a node shows its detail (`select`), Ctrl+Enter or „Otevřít…“ in the actions menu opens the object
 * (D1: `open`), a click on a
 * group's name opens or folds it, Esc from the subject goes back to where the graph was opened (`back`), and a node is dragged
 * out as an object (the tree takes it as a reference, a board as a card).
 * Why not a graph of its own: the owner's rule is one component for anything board-like (§11); this is its derived, read-only use.
 */
const props = withDefaults(defineProps<{
  graph: RelationGraph | null;
  /** The object the graph is about: tinted, selected at first, kept out of groups. */
  focusId: string;
  label: string;
  /** A small, quiet graph for the dock: no rail and no zoom buttons. */
  compact?: boolean;
  branchOf?: (node: RelationNode) => string | undefined;
}>(), { compact: false, branchOf: undefined });
const emit = defineEmits<{ select: [id: string, typeId: string]; open: [id: string, typeId: string]; back: [] }>();

const canvas = ref<InstanceType<typeof BoardCanvas>>();
const root = ref<HTMLElement>();
// Below this many cards the graph is read at 50 % at least (a card's text stays legible), and is panned to for the rest.
const SMALL_GRAPH = 12;
// Switched to from the list, the graph sits below the heading, tiles and filters: bring it into view.
// On a narrow container the 50 % minimum would cut the outer nodes off at an unreadable size, so there the graph fits fully.
const narrow = ref(false);
onMounted(() => {
  const width = root.value?.clientWidth;
  narrow.value = !!width && width < 600;
  if (!props.compact) root.value?.scrollIntoView?.({ block: "nearest" });
});
watch(narrow, async () => { await nextTick(); canvas.value?.fit("content", 1); });
const expanded = shallowRef<ReadonlySet<string>>(new Set());
const selection = ref<string[]>([]);
const focus = computed(() => props.focusId.toLowerCase());
watch(focus, () => { expanded.value = new Set(); touched = false; });
const items = computed(() => (props.graph ? relationItems(props.graph, { focusId: focus.value, expanded: expanded.value, branchOf: props.branchOf }) : []));
const cardCount = computed(() => items.value.filter(item => item.kind !== "connector").length);
const nodes = computed(() => new Map((props.graph?.nodes ?? []).map(node => [node.id.toLowerCase(), node])));
// Fit again when the neighbourhood changes (a deeper step, another subject, a group opened); the first fit is the canvas's own.
watch(items, async (next, before) => {
  if (!before.length || !next.length) return;
  await nextTick();
  canvas.value?.fit("content", 1);
});

function toggle(id: string) {
  const next = new Set(expanded.value);
  if (!next.delete(id)) next.add(id);
  expanded.value = next;
}
// The subject is selected when the graph opens (preselect): that is no choice of the reader, so it shows no dock until a first touch.
let touched = false;
const touch = () => { touched = true; };
function onSelection(ids: string[]) {
  selection.value = ids;
  if (!touched) return;
  const id = ids.length === 1 ? openableId(ids[0]!) : null;
  if (id) emit("select", id, nodes.value.get(id)?.typeId ?? "");
}
function openNode(id: string | null) {
  if (id === null) return;
  if (isGroupId(id)) { toggle(id); return; }
  const openable = openableId(id);
  if (openable) emit("open", openable, nodes.value.get(openable)?.typeId ?? "");
}
const nodeIdOf = (target: EventTarget | null) => (target as Element | null)?.closest?.<HTMLElement>(".vue-flow__node")?.dataset.id;

// A group's card or name folds and unfolds it; Enter does the same from the keyboard (the canvas edits nothing read-only).
function onClick(event: MouseEvent) {
  const id = nodeIdOf(event.target);
  if (id && isGroupId(id) && (event.target as Element).closest(".board-group-card, .board-group-title")) toggle(id);
}
function onKeydown(event: KeyboardEvent) {
  touch();
  if (event.key === "Escape") {
    // Out of the graph, back to its trigger: from the subject itself or from nothing; the canvas takes the first Esc from anything else.
    if (!selection.value.length || (selection.value.length === 1 && selection.value[0] === focus.value)) emit("back");
  } else if (event.key === "Enter") {
    const id = nodeIdOf(event.target);
    if (id && isGroupId(id)) { event.preventDefault(); toggle(id); }
  }
}
// Vue Flow does not drag read-only nodes, so the browser's own drag can: a node is made draggable when the pointer is over it.
function arm(event: MouseEvent) {
  const node = (event.target as Element).closest?.<HTMLElement>(".vue-flow__node");
  if (node && openableId(node.dataset.id ?? "") && !node.draggable) node.draggable = true;
}
function onDragstart(event: DragEvent) {
  const id = openableId(nodeIdOf(event.target) ?? "");
  const node = id ? nodes.value.get(id) : undefined;
  if (!id || !node) { event.preventDefault(); return; }
  startObjectDrag(event, { id, typeId: node.typeId, title: plainTitle(node.title) });
}
// The graph sits in a scrolling page: a plain wheel scrolls the page (a board's wheel pans, which here trapped the page and
// slid the nodes out of view); Ctrl or ⌘ with the wheel still zooms, the zoom buttons and the arrows stay.
function onWheel(event: WheelEvent) { if (!event.ctrlKey && !event.metaKey) event.stopPropagation(); }
defineExpose({ fit: () => canvas.value?.fit("content", 1) });
</script>

<template>
  <div ref="root" class="relations-graph" :class="{ 'is-compact': compact }" data-relations-graph @keydown.capture="onKeydown" @pointerdown.capture="touch" @click.capture="onClick" @wheel.capture="onWheel" @mouseover="arm" @dragstart="onDragstart">
    <BoardCanvas v-if="items.length > 1" ref="canvas" :items="items" :label="label" :can-edit="false" :preselect="focus" :fit-min-zoom="cardCount <= SMALL_GRAPH && !narrow ? 0.5 : undefined" :aria-label="t.graph.aria"
      @selection="onSelection" @open-detail="openNode" @open-page="openNode" />
    <EmptyState v-else-if="graph" compact icon="network" :title="t.graph.noneTitle" :text="t.graph.noneText" />
  </div>
</template>

<style scoped>
.relations-graph { position: relative; min-width: 0; height: 100%; }
.relations-graph :deep(.board-canvas) { min-height: 360px; }
/* The dock's graph is a glance, not a workspace. */
.relations-graph.is-compact { height: 240px; }
.relations-graph.is-compact :deep(.board-canvas) { min-height: 0; }
.relations-graph.is-compact :deep(:is(.board-canvas-rail, .board-canvas-corner, .board-canvas-toolbar)) { display: none; }
</style>
