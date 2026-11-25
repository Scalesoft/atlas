<script setup lang="ts">
import { computed, inject } from "vue";
import type { Geometry } from "../../../boards/boardApi";
import type { ConnectorStyle } from "../../../boards/boardConnectorStyle";
import { cardsNear, connectorPath, type Point, type Port } from "../../../boards/boardRoute";
import { boardCanvasKey } from "./boardCanvasContext";
import { colorAttributes } from "./boardPalette";

/**
 * A connector being drawn or reconnected (by the pointer: Vue Flow's connection line; by the keys: L), drawn as the connector it
 * becomes: the same route around the cards (boardRoute.connectorPath), the look the new one takes or the moved one has, its
 * arrowheads. `port`: the side of a card its free end snapped to, ringed. The line's classes are BoardEdge's, so colour, dash and
 * thickness read the same in both themes. Distinct from BoardEdge: no item, label, popover or motion of its own.
 */
const props = defineProps<{ source: Port; target: Port; look: ConnectorStyle; port?: Point; boxes: readonly Geometry[]; ends: readonly (string | undefined)[] }>();
const ctx = inject(boardCanvasKey)!;
const route = computed(() => props.look.route ?? "elbow");
const path = computed(() => (route.value === "elbow"
  ? connectorPath("elbow", props.source, props.target, [...props.boxes, ...cardsNear(ctx.obstacles.value, props.source, props.target, props.ends)])
  : connectorPath(route.value, props.source, props.target, []))[0]);
// Its own marker: the board's shared one lives on the first connector drawn, and a board may have none yet.
const marker = `${ctx.flowId}-preview-arrow`;
</script>

<template>
  <g class="board-edge board-connector-preview" :data-dash="look.dash" :data-thickness="look.thickness" v-bind="colorAttributes(look.color)" aria-hidden="true" data-board-connector-preview>
    <defs>
      <marker :id="marker" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5" markerHeight="5" markerUnits="strokeWidth" orient="auto-start-reverse">
        <path d="M0 0 10 5 0 10z" class="board-edge-arrow" />
      </marker>
    </defs>
    <path :d="path" class="board-edge-line" :marker-start="look.arrowStart === 'arrow' ? `url(#${marker})` : undefined"
      :marker-end="(look.arrowEnd ?? 'arrow') === 'arrow' ? `url(#${marker})` : undefined" />
    <circle v-if="port" :key="`${port.x}:${port.y}`" :cx="port.x" :cy="port.y" r="6" class="board-connector-port" data-board-connector-port />
  </g>
</template>

<style scoped>
.board-connector-preview { pointer-events: none; }
.board-connector-preview .board-edge-line { animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
/* The side the free end lands at: a ring that pops in where it snapped. */
.board-connector-port { fill: var(--surface); stroke: var(--accent); stroke-width: 2.5; transform-box: fill-box; transform-origin: center;
  animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out) both; }
</style>
