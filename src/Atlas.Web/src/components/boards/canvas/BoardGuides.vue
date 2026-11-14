<script setup lang="ts">
import type { Guide } from "../../../boards/boardSnap";
import type { Viewport } from "../../../boards/boardViewport";

/**
 * Alignment and spacing guides of a move (boardSnap.ts), drawn in screen space; they fade in. No TransitionGroup: it measures each of
 * its children on every render, a layout of the whole board on every frame of a drag (BoardPresence, COL2-1).
 */
const props = defineProps<{ guides: readonly Guide[]; viewport: Viewport }>();
const sx = (x: number) => x * props.viewport.zoom + props.viewport.x;
const sy = (y: number) => y * props.viewport.zoom + props.viewport.y;
function line(guide: Guide) {
  const along = guide.kind === "align" ? guide.axis === "x" : guide.axis === "y";
  return along
    ? { x1: sx(guide.at), x2: sx(guide.at), y1: sy(guide.from), y2: sy(guide.to) }
    : { x1: sx(guide.from), x2: sx(guide.to), y1: sy(guide.at), y2: sy(guide.at) };
}
</script>

<template>
  <svg class="board-guides" aria-hidden="true">
    <line v-for="guide in guides" :key="`${guide.kind}:${guide.axis}:${guide.at}:${guide.from}`" v-bind="line(guide)" class="board-guide" :class="`is-${guide.kind}`" data-board-guide />
  </svg>
</template>

<style scoped>
.board-guides { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; pointer-events: none; }
.board-guide { stroke: var(--accent); stroke-width: 1; animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-guide.is-spacing { stroke-dasharray: 3 3; }
</style>
