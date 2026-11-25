<script lang="ts">
import { computed as sharedComputed, type ComputedRef } from "vue";
import type { VueFlowStore } from "@vue-flow/core";
/** The first connector a board draws, worked out once per board for all of them (Vue Flow lists the drawn ones anew on a pan). */
const firstEdges = new WeakMap<VueFlowStore, ComputedRef<string | undefined>>();
function firstEdgeOf(flow: VueFlowStore): ComputedRef<string | undefined> {
  let first = firstEdges.get(flow);
  if (!first) firstEdges.set(flow, first = sharedComputed(() => flow.getEdges.value[0]?.id));
  return first;
}
</script>

<script setup lang="ts">
import t from "../../../i18n/boards";
import { useVueFlow, type GraphNode, type Position } from "@vue-flow/core";
import { computed, inject, onMounted, ref, watch } from "vue";
import type { BoardEdgeState, BoardSide, Geometry } from "../../../boards/boardApi";
import type { BoardEdgeData } from "../../../boards/boardFlow";
import { cardsNear, connectorPath, shiftPort } from "../../../boards/boardRoute";
import { BoardMarkdown } from "../text/textEditorChunk";
import BoardText from "../text/BoardText.vue";
import type { TextBinding } from "../text/textBinding";
import { boardCanvasKey } from "./boardCanvasContext";
import { colorAttributes } from "./boardPalette";
import { playEdgeMotion, type PlayedMotion } from "./boardAnimations";

/**
 * A connector as a Vue Flow edge: elbow (out of its facing sides and around the cards in the way, corners rounded: boardRoute.ts;
 * parallel ones spread apart), curved or straight, arrowheads
 * at either end, solid or dashed, in a board colour, with its label in the middle edited on a double-click. One arrowhead marker
 * for all connectors, filled with the line's own stroke, so it takes the connector's colour in both themes. Vue Flow draws the end
 * handles of a selected, hovered or focused connector (boardFlow.ts makes only those updatable).
 *
 * Live edges (objects spec §5.3): a state colours the line and its arrowheads (waiting amber, done green, blocked red); a highlighted
 * or still open one carries a subtle flow along its direction, which moves only while the connector is hovered, focused, selected,
 * highlighted or animated on purpose (static under reduced motion); the neighbour highlighting colours it up or down or dims it;
 * hovering shows a popover with its type, state and label. A merged edge (collapsed groups) shows its count.
 */
defineOptions({ inheritAttrs: false });
const props = defineProps<{
  id: string; data: BoardEdgeData & { motion?: PlayedMotion }; selected?: boolean;
  sourceX: number; sourceY: number; targetX: number; targetY: number; sourcePosition: Position; targetPosition: Position;
  sourceNode?: GraphNode; targetNode?: GraphNode; updatable?: boolean | "source" | "target";
}>();
const ctx = inject(boardCanvasKey)!;
const group = ref<SVGGElement>();
const line = ref<SVGPathElement>();

const boxOf = (node: GraphNode | undefined): Geometry | undefined =>
  node ? { x: node.computedPosition.x, y: node.computedPosition.y, width: node.dimensions.width, height: node.dimensions.height } : undefined;
// Vue Flow gives every node a new computedPosition whenever the nodes are set (a selection change too): the route reads its ends'
// boxes through these, which keep the previous box while it is the same, so selecting all of a board reroutes nothing (UX SB4-3).
const sameBox = (a: Geometry | undefined, b: Geometry | undefined) => a === b || (!!a && !!b && a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height);
const sourceBox = computed<Geometry | undefined>(previous => { const next = boxOf(props.sourceNode); return sameBox(previous, next) ? previous : next; });
const targetBox = computed<Geometry | undefined>(previous => { const next = boxOf(props.targetNode); return sameBox(previous, next) ? previous : next; });
/** The two ends, parallel connectors spread apart. */
const ports = computed(() => {
  const offset = props.data.offset ?? 0;
  return [shiftPort({ x: props.sourceX, y: props.sourceY, side: props.sourcePosition as BoardSide }, offset),
    shiftPort({ x: props.targetX, y: props.targetY, side: props.targetPosition as BoardSide }, offset)] as const;
});
/**
 * The cards in the way, near the two ends (a box around both, a card's width wider); the same array while they stay where they were,
 * so a change elsewhere on the board (an item added, a text, an undo) reroutes no connector it does not touch.
 */
const nearby = computed<Geometry[]>(previous => {
  if (props.data.route === "curved" || props.data.route === "straight") return previous ?? [];
  const [source, target] = ports.value;
  // The ends' ids read once: a reactive read per card was most of a remote change's cost on a large board.
  const next = cardsNear(ctx.obstacles.value, source, target, [props.sourceNode?.id, props.targetNode?.id]);
  return previous && previous.length === next.length && next.every((box, index) => sameBox(box, previous[index])) ? previous : next;
});
/** The path and the label's point: the elbow routed around the cards near it (boardRoute.ts), parallel ones spread apart. */
const geometry = computed<[string, number, number]>(() => {
  const [source, target] = ports.value;
  if (props.data.route === "curved" || props.data.route === "straight") return connectorPath(props.data.route, source, target, []);
  const own = [sourceBox.value, targetBox.value].filter((box): box is Geometry => !!box);
  return connectorPath(props.data.route, source, target, [...own, ...nearby.value]);
});
/** The middles of Vue Flow's grab circles of the ends (each its radius out of the card, as Vue Flow places them): a dot shows each. */
const out = { top: [0, -1], right: [1, 0], bottom: [0, 1], left: [-1, 0] } as const;
const endDots = computed(() => {
  if (!props.updatable) return [];
  const r = ctx.endRadius.value;
  return ([[props.sourceX, props.sourceY, props.sourcePosition], [props.targetX, props.targetY, props.targetPosition]] as const)
    .map(([x, y, side]) => ({ x: x + out[side as BoardSide][0] * r, y: y + out[side as BoardSide][1] * r }));
});
// One arrowhead marker for all connectors (its fill is the line's own stroke): the first connector drawn carries it.
const marker = `${ctx.flowId}-arrow`;
const firstEdge = firstEdgeOf(useVueFlow());
const merged = computed(() => props.data.merged);
const label = computed(() => (merged.value ? "" : props.data.item.text ?? ""));
// Not in a graph that only reads: its nodes are page cards, so the text renderer (and the editor plugins) stay out of it.
// The shared lazy component: a loader made in setup would be held by the chunk preload and keep the closed board alive.
const editing = computed(() => ctx.per.editing(props.id));
const state = computed(() => (merged.value ? undefined : props.data.item.props?.state));
// What the highlighting leaves out dims by the canvas's mark (BoardNode).
const mark = computed(() => ctx.per.linkMark(props.id));
// The direction flows from the start to the end, or back when only the start has an arrow.
const flowing = computed(() => !!props.data.animated || !!mark.value || state.value === "waiting" || state.value === "blocked");
const backward = computed(() => props.data.arrowStart === "arrow" && props.data.arrowEnd === "none");
const stateLabels: Record<BoardEdgeState, string> = { waiting: t.edge.waiting, done: t.edge.done, blocked: t.edge.blocked };
const popover = computed(() => ctx.per.hoveredEdge(props.id) && !editing.value);
// The popover's layer stays while it fades out, then goes: an idle connector holds no anchor (a label layer per connector cost).
const popoverShown = ref(false);
watch(popover, on => { if (on) popoverShown.value = true; });
// The label is inline Markdown, edited by the page editor in its one-line mode (Enter, Esc or a click outside end it).
const binding: TextBinding = { read: () => label.value, commit: markdown => ctx.commitText(props.id, markdown), save: markdown => ctx.saveText(props.id, markdown) };

function play() {
  const motion = props.data.motion;
  if (motion && line.value && group.value) playEdgeMotion(line.value, group.value, motion, props.data.dash === "dashed");
}
onMounted(play);
watch(() => props.data.motion?.key, play, { flush: "post" });
</script>

<template>
  <defs v-if="firstEdge === id">
    <marker :id="marker" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5" markerHeight="5" markerUnits="strokeWidth" orient="auto-start-reverse">
      <path d="M0 0 10 5 0 10z" class="board-edge-arrow" />
    </marker>
  </defs>
  <g ref="group" class="board-edge" :class="{ 'is-selected': selected, 'is-flowing': flowing, 'is-backward': backward }" :data-dash="data.dash" :data-state="state" :data-thickness="data.thickness" :data-animated="data.animated || undefined"
    :data-highlight="mark" :data-merged="merged" v-bind="colorAttributes(merged ? undefined : data.item.color)">
    <path :d="geometry[0]" class="vue-flow__edge-interaction board-edge-hit" />
    <path ref="line" :d="geometry[0]" class="vue-flow__edge-path board-edge-line"
      :marker-start="data.arrowStart === 'arrow' ? `url(#${marker})` : undefined" :marker-end="data.arrowEnd === 'arrow' ? `url(#${marker})` : undefined" />
    <path v-if="flowing" :d="geometry[0]" class="board-edge-flow" aria-hidden="true" />
    <circle v-for="(dot, index) in endDots" :key="index" :cx="dot.x" :cy="dot.y" r="5" class="board-edge-end" aria-hidden="true" data-board-edge-end />
  </g>
  <!-- One layer for what sits over the line, only while there is any: an SVG of its own whose HTML goes to the board's label layer, as
       Vue Flow's EdgeLabelRenderer does (which looked that layer up in the page on each of its renders: 200 ms of a select all). -->
  <svg v-if="merged || label || editing || popoverShown"><foreignObject width="0" height="0"><Teleport :to="ctx.labelLayer.value" :disabled="!ctx.labelLayer.value">
    <div v-if="merged" class="board-edge-count nodrag nopan" :style="{ transform: `translate(-50%, -50%) translate(${geometry[1]}px, ${geometry[2]}px)` }" data-board-edge-count>{{ merged }}</div>
    <!-- The anchor holds the place; the popover inside it animates. -->
    <div v-if="popoverShown" class="board-edge-popover-anchor" :style="{ transform: `translate(${geometry[1]}px, ${geometry[2]}px)` }">
      <Transition name="board-edge-popover" appear @after-leave="popoverShown = popover">
        <div v-if="popover" class="board-edge-popover" role="tooltip" data-board-edge-popover>
          <strong>{{ merged ? t.edge.merged(merged) : t.edge.connector }}</strong>
          <span v-if="state" class="board-edge-state" :data-state="state">{{ stateLabels[state] }}</span>
          <BoardMarkdown v-if="label" class="board-edge-popover-label" :text="label" inline :aria-label="t.edge.label" />
        </div>
      </Transition>
    </div>
    <div v-if="label || editing" class="board-edge-label nodrag nopan" :class="{ 'is-selected': selected }" :style="{ transform: `translate(-50%, -50%) translate(${geometry[1]}px, ${geometry[2]}px)` }"
      :data-board-edge-label="id" @dblclick.stop="ctx.startEdit(id)">
      <BoardText :id="id" :editing="editing" :binding="binding" :text="label" editor-class="board-edge-field" inline :placeholder="t.edge.placeholder" :aria-label="t.edge.label" />
    </div>
  </Teleport></foreignObject></svg>
</template>

<style>
.board-edge { --board-edge: var(--appearance-ink, var(--muted)); }
/* White on a light board would vanish: a pale grey there, near white on a dark one (J1). */
.board-edge[data-board-color="white"] { --board-edge: color-mix(in oklab, var(--muted) 45%, var(--surface)); }
[data-mode="dark"] .board-edge[data-board-color="white"] { --board-edge: color-mix(in oklab, var(--text) 85%, var(--surface)); }
.board-edge[data-board-color="black"] { --board-edge: var(--text); }
.board-edge-hit { fill: none; stroke: transparent; stroke-width: 20; }
.board-edge-line { fill: none; stroke: var(--board-edge); stroke-width: 2; transition: stroke var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-edge[data-dash="dashed"] .board-edge-line { stroke-dasharray: 8 6; }
/* Thickness (quick styles): the arrowheads follow, their size is in stroke widths. */
.board-edge[data-thickness="s"] .board-edge-line { stroke-width: 1.25; }
.board-edge[data-thickness="l"] .board-edge-line { stroke-width: 3.5; }
.board-edge[data-thickness="l"].is-selected .board-edge-line { stroke-width: 4; }
/* A connector animated on purpose flows in its own colour, more visibly than the hint of a live state. */
.board-edge[data-animated] .board-edge-flow { stroke: var(--board-edge); stroke-width: 2.5; stroke-dasharray: 3 12; opacity: 1; }
.board-edge[data-animated] .board-edge-line { stroke-opacity: .45; }
.board-edge[data-animated][data-thickness="l"] .board-edge-flow { stroke-width: 4; }
.board-edge.is-selected .board-edge-line, .vue-flow__edge:focus-visible .board-edge-line { stroke: var(--accent); stroke-width: 2.5; }
/* State colours (objects spec §5.3), then the neighbour highlighting over them; the arrowheads follow --board-edge. */
.board-edge[data-state="waiting"] { --board-edge: var(--warning); }
.board-edge[data-state="done"] { --board-edge: var(--success); }
.board-edge[data-state="blocked"] { --board-edge: var(--error); }
.board-edge:is([data-highlight="up"], [data-highlight="path"]) { --board-edge: var(--accent); }
.board-edge[data-highlight="down"] { --board-edge: var(--warning); }
.board-edge { transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-canvas[data-highlighting] .board-edge:not([data-highlight]) { opacity: var(--board-dim-edge, .18); }
.board-edge:is([data-highlight="up"], [data-highlight="down"], [data-highlight="path"]) .board-edge-line { stroke-width: 2.5; }
.board-edge[data-merged] .board-edge-line { stroke-width: 2.5; }
/* A merged edge appears as its group collapses: it fades in while the members tuck into the card. */
.board-edge[data-merged] { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
/* The flow: short dashes along the line in its direction, over the line in its own colour. They travel only while the connector is
   looked at (hovered, focused, selected, highlighted) or animated on purpose: dozens of waiting or blocked ones moving at once repainted
   the whole board on every frame. */
.board-edge-flow { fill: none; stroke: var(--surface); stroke-width: 1.5; stroke-linecap: round; stroke-dasharray: 2 9; opacity: .9; pointer-events: none;
  animation: atlas-dash-flow var(--atlas-motion-loop) linear infinite paused; }
.board-edge:is(.is-selected, [data-highlight], [data-animated]) .board-edge-flow, .vue-flow__edge:is(:hover, :focus-visible) .board-edge-flow { animation-play-state: running; }
.board-edge.is-backward .board-edge-flow { animation-direction: reverse; }
@media (prefers-reduced-motion: reduce) { .board-edge-flow { animation: none; } }
[data-atlas-reduced-motion] .board-edge-flow { animation: none; }
.board-edge-count { position: absolute; min-width: 22px; padding: 1px 7px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); color: var(--text);
  font-size: 12px; font-weight: 600; text-align: center; pointer-events: none; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.board-edge-popover-anchor { position: absolute; z-index: 10; width: 0; height: 0; pointer-events: none; }
.board-edge-popover { position: absolute; bottom: 14px; left: 0; translate: -50% 0; display: flex; flex-direction: column; gap: 4px; max-width: 260px; padding: 8px 10px; border: 1px solid var(--line);
  border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow-hover); color: var(--text); font-size: 12px; pointer-events: none; }
.board-edge-popover strong { font-weight: 600; }
.board-edge-state { align-self: flex-start; padding: 0 6px; border-radius: 6px; font-weight: 600; }
.board-edge-state[data-state="waiting"] { background: var(--warning-bg); color: var(--warning); }
.board-edge-state[data-state="done"] { background: var(--success-bg); color: var(--success); }
.board-edge-state[data-state="blocked"] { background: var(--error-bg); color: var(--error); }
.board-edge-popover-label { overflow: hidden; color: var(--muted); text-overflow: ellipsis; white-space: nowrap; }
.board-edge-popover-enter-active { animation: atlas-menu-in-top var(--atlas-motion-menu) var(--atlas-ease-out) both; }
.board-edge-popover-leave-active { animation: st-fade-out var(--atlas-motion-surface) var(--atlas-ease-out) both; }
/* The shared arrowhead takes the stroke of the line it ends (its colour, state, highlight or selection). */
.board-edge-arrow { fill: var(--muted); fill: context-stroke; }
.board-edge-label { position: absolute; max-width: 240px; padding: 2px 8px; border: 1px solid var(--line); border-radius: 6px; background: var(--surface); color: var(--text);
  font-size: 12px; line-height: 1.4; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; pointer-events: all; cursor: default;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-edge-label.is-selected { border-color: var(--accent); }
.board-edge-field { min-width: 120px; }
/* Vue Flow's grab circle of an end takes the pointer in its whole radius (the canvas's edge-updater-radius); the dot in its middle shows it. */
.vue-flow__edge .vue-flow__edgeupdater { fill: transparent; stroke: transparent; cursor: grab; }
.board-edge-end { fill: var(--surface); stroke: var(--accent); stroke-width: 2; pointer-events: none; transform-box: fill-box; transform-origin: center;
  animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); transition: scale var(--atlas-motion-fast) var(--atlas-ease-out); }
.vue-flow__edge.updating .board-edge-end { scale: 1.3; }
.board-canvas.is-readonly .board-edge-end { display: none; }
</style>
