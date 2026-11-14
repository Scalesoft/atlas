<script setup lang="ts">
import AtlasIcon from "../AtlasIcon.vue";
import CommandIcon from "../CommandIcon.vue";
import type { StatusTone } from "./types";

// One figure on an overview: a small label, the value with its icon and a line of context
// (default slot: text or a StatusBadge). With `href` the tile is a link to where it is managed.
// `valueIcon` is for a state (warning, done, waiting), coloured by `tone`; the label icon names the area.
// `#chart` puts a small decorative chart (ring, bars) beside the figures; the text must still say what it shows.
// `compact` drops the minimum height and tightens the tile, for a row of figures above a list that must stay in view (Úlohy na pozadí).
withDefaults(defineProps<{ label: string; value: string; icon?: string; valueIcon?: string; tone?: StatusTone; href?: string; compact?: boolean }>(), { icon: undefined, valueIcon: undefined, tone: undefined, href: undefined, compact: false });
</script>

<template>
  <component :is="href ? 'a' : 'div'" :href="href" class="st-card stat-tile" :class="{ 'is-interactive': !!href, 'has-chart': !!$slots.chart, 'is-compact': compact }">
    <span class="stat-tile-label"><CommandIcon v-if="icon" :name="icon" :size="15" />{{ label }}</span>
    <strong :key="value" class="stat-tile-value" :data-tone="tone"><AtlasIcon v-if="valueIcon" :name="valueIcon" :size="18" />{{ value }}</strong>
    <span v-if="$slots.default" class="stat-tile-context"><slot /></span>
    <span v-if="$slots.chart" class="stat-tile-chart" aria-hidden="true"><slot name="chart" /></span>
  </component>
</template>

<style scoped>
/* Refresh 2.5 (board 06): a quiet tile, label with its icon, the value large and coloured by its tone
   (the word carries the meaning, the colour only repeats it), a line of context below. */
.stat-tile { display: grid; gap: 6px; align-content: start; min-height: 104px; padding: 14px 16px; border-radius: 12px; box-shadow: none; color: var(--text); text-decoration: none; min-width: 0; }
.stat-tile.has-chart { grid-template-columns: minmax(0, 1fr) auto; column-gap: 12px; }
.stat-tile.has-chart > :not(.stat-tile-chart) { grid-column: 1; }
.stat-tile-chart { grid-column: 2; grid-row: 1 / span 3; align-self: center; display: flex; }
.stat-tile-label { display: flex; align-items: center; gap: 8px; color: var(--muted); font-size: 13px; font-weight: 500; }
.stat-tile-label > svg { flex-shrink: 0; }
.stat-tile-value { display: flex; align-items: center; gap: 8px; font-size: 20px; font-weight: 650; line-height: 1.3; letter-spacing: -.01em; overflow-wrap: anywhere; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.stat-tile-value > svg { color: var(--accent); flex-shrink: 0; }
.stat-tile-value[data-tone="warning"] { color: var(--warning); }
.stat-tile-value[data-tone="success"] { color: var(--success); }
.stat-tile-value[data-tone="danger"] { color: var(--error); }
.stat-tile-value[data-tone="warning"] > svg { color: var(--warning); }
.stat-tile-value[data-tone="success"] > svg { color: var(--success); }
.stat-tile-value[data-tone="danger"] > svg { color: var(--error); }
.stat-tile-value[data-tone="neutral"] > svg { color: var(--muted); }
.stat-tile-context { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; color: var(--muted); font-size: 12.5px; line-height: 1.45; }
.stat-tile.is-compact { min-height: 0; gap: 2px; padding: 10px 14px; }
.is-compact .stat-tile-value { font-size: 18px; }
@container settings-shell (max-width: 680px) { .stat-tile { min-height: 0; padding: 12px 14px; } .stat-tile-value { font-size: 17px; } .stat-tile.has-chart { grid-template-columns: minmax(0, 1fr); } .stat-tile-chart { display: none; } }
</style>
