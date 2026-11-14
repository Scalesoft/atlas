<script setup lang="ts">
import { computed, useId } from "vue";
import type { BoardSettings } from "../../../boards/boardApi";
import { gridLevels } from "../../../boards/boardGrid";
import { timelineTicks, todayX } from "../../../boards/boardTimeline";
import type { Viewport } from "../../../boards/boardViewport";

/**
 * The board's background (spec §2, §3.7) behind the Vue Flow layers, in screen space: the adaptive dot grid or its lines (two
 * levels cross-fading by zoom, boardGrid.ts), a timeline (boardTimeline.ts: period lines, the next finer level fading in, labels held
 * at the top of the view, a „dnes“ marker), or nothing. One <pattern> per grid level, never an element per dot. The colour is
 * opaque, so the coarse level drawn over the fine one does not darken the dots they share.
 */
const props = defineProps<{ viewport: Viewport; settings: BoardSettings; width: number }>();
const uid = useId();
const timeline = computed(() => (props.settings.background === "timeline" ? props.settings.timeline : undefined));
const levels = computed(() => (props.settings.background === "dots" || props.settings.background === "lines" ? gridLevels(props.viewport) : []));
const lines = computed(() => props.settings.background === "lines");
const sx = (x: number) => x * props.viewport.zoom + props.viewport.x;
const axis = computed(() => {
  const t = timeline.value;
  if (!t) return null;
  const v = props.viewport;
  const { ticks, finerTicks, finerOpacity } = timelineTicks(t, v.zoom, { x: -v.x / v.zoom, width: props.width / v.zoom });
  const shown = ticks.map((tick, index) => {
    const x = sx(tick.x), next = ticks[index + 1] ? sx(ticks[index + 1]!.x) : Infinity;
    // A label stays at the left of the view while its period is in view, and gives way to the next one.
    return { x, label: tick.label, labelX: Math.min(Math.max(x, 0), next - 72) + 8 };
  });
  return { ticks: shown, finer: finerTicks.map(tick => sx(tick.x)), finerOpacity, today: sx(todayX(t)) };
});
</script>

<template>
  <svg class="board-background" aria-hidden="true" data-board-background :data-background="settings.background">
    <defs>
      <pattern v-for="(level, index) in levels" :id="`${uid}-${index}`" :key="level.spacing" patternUnits="userSpaceOnUse"
        :width="level.size" :height="level.size" :x="lines ? level.x : level.x - level.size / 2" :y="lines ? level.y : level.y - level.size / 2">
        <path v-if="lines" :d="`M ${level.size} 0 L 0 0 0 ${level.size}`" class="board-background-line" :class="{ 'is-major': index === 1 }" />
        <circle v-else :cx="level.size / 2" :cy="level.size / 2" r="1.1" class="board-background-dot" />
      </pattern>
    </defs>
    <rect v-for="(level, index) in levels" :key="level.spacing" width="100%" height="100%" :fill="`url(#${uid}-${index})`" :opacity="level.opacity" />
    <g v-if="axis" data-board-timeline>
      <line v-for="x in axis.finer" :key="`f${x}`" :x1="x" :x2="x" y1="0" y2="100%" class="board-timeline-minor" :opacity="axis.finerOpacity" />
      <template v-for="tick in axis.ticks" :key="tick.x">
        <line :x1="tick.x" :x2="tick.x" y1="0" y2="100%" class="board-timeline-major" />
        <text :x="tick.labelX" y="20" class="board-timeline-label">{{ tick.label }}</text>
      </template>
      <line :x1="axis.today" :x2="axis.today" y1="0" y2="100%" class="board-timeline-today" data-board-today />
      <text :x="axis.today + 6" y="38" class="board-timeline-today-label">dnes</text>
    </g>
  </svg>
</template>

<style scoped>
.board-background { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; }
.board-background-dot { fill: color-mix(in oklab, var(--muted) 42%, var(--board-surface)); }
.board-background-line { fill: none; stroke: color-mix(in oklab, var(--muted) 16%, var(--board-surface)); stroke-width: 1; }
.board-background-line.is-major { stroke: color-mix(in oklab, var(--muted) 28%, var(--board-surface)); }
.board-timeline-minor { stroke: color-mix(in oklab, var(--muted) 14%, var(--board-surface)); stroke-width: 1; }
.board-timeline-major { stroke: color-mix(in oklab, var(--muted) 32%, var(--board-surface)); stroke-width: 1; }
.board-timeline-label { fill: var(--muted); font-size: 12px; font-weight: 600; }
.board-timeline-today { stroke: var(--accent); stroke-width: 1.5; }
.board-timeline-today-label { fill: var(--accent); font-size: 11px; font-weight: 700; }
</style>
