<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import type { StatusTone } from "./types";

// A state is always an icon and a word: colour alone never carries the meaning.
// Refresh 2.5 (boards 07–09): a small rectangular chip; `dot` swaps the icon for a dot of the tone (table cells).
const props = withDefaults(defineProps<{ tone?: StatusTone; label: string; icon?: string; size?: "sm" | "md"; confirmed?: boolean; dot?: boolean }>(), {
  tone: "neutral",
  icon: undefined,
  size: "md",
  confirmed: false,
  dot: false,
});
const toneIcons: Record<StatusTone, string> = {
  success: "circle-check",
  warning: "triangle-alert",
  danger: "circle-x",
  info: "info",
  neutral: "clock",
  accent: "shield-check",
};
const iconName = computed(() => props.icon ?? toneIcons[props.tone]);
</script>

<template>
  <span class="status-badge" :data-tone="tone" :data-size="size" :data-confirmed="confirmed || undefined" :data-dot="dot || undefined">
    <span v-if="dot" class="status-badge-dot" aria-hidden="true" />
    <AtlasIcon v-else :name="iconName" :size="size === 'sm' ? 13 : 14" />
    <span>{{ label }}</span>
  </span>
</template>

<style scoped>
.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  /* A badge keeps its own width: in a flex column (a card body, a dialog) or a stretching grid it became a full-width bar. */
  width: fit-content;
  max-width: 100%;
  padding: 3px 8px 3px 7px;
  border-radius: 7px;
  font-size: 12px;
  font-weight: 600;
  line-height: 1.35;
  white-space: nowrap;
  color: var(--muted);
  background: var(--hover);
  transition: background-color var(--atlas-motion-base) var(--atlas-ease-out), color var(--atlas-motion-base) var(--atlas-ease-out);
}
.status-badge > span:not(.status-badge-dot) { overflow: hidden; text-overflow: ellipsis; }
.status-badge-dot { flex-shrink: 0; width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
.status-badge > svg { flex-shrink: 0; }
.status-badge[data-size="sm"] { padding: 2px 7px 2px 6px; border-radius: 6px; font-size: 11.5px; gap: 4px; }
.status-badge[data-tone="success"] { color: var(--success); background: var(--success-bg); }
.status-badge[data-tone="warning"] { color: var(--warning); background: var(--warning-bg); }
.status-badge[data-tone="danger"] { color: var(--error); background: var(--error-bg); }
.status-badge[data-tone="info"] { color: var(--info); background: var(--info-bg); }
.status-badge[data-tone="accent"] { color: var(--accent); background: var(--accent-soft); }
/* Opt in only for a completed action. Persisted success states stay quiet. */
.status-badge[data-confirmed] {
  position: relative;
  color: var(--success);
  background: color-mix(in srgb, var(--success) 9%, transparent);
  animation: status-settle var(--atlas-motion-pop) var(--atlas-ease-out);
}
.status-badge[data-confirmed]::after {
  content: ""; position: absolute; inset: 0; border-radius: inherit; pointer-events: none;
  box-shadow: 0 0 0 0 color-mix(in srgb, var(--success) 22%, transparent);
  animation: status-halo calc(var(--atlas-motion-pop) * 2) var(--atlas-ease-out) both;
}
.status-badge[data-confirmed] > span { animation: status-label var(--atlas-motion-slow) var(--atlas-ease-out) both; }
.status-badge[data-confirmed] :deep(svg circle) { stroke-dasharray: 70; animation: status-circle var(--atlas-motion-slow) var(--atlas-ease-out) both; }
.status-badge[data-confirmed] :deep(svg path),
.status-badge[data-confirmed] :deep(svg polyline) { stroke-dasharray: 30; animation: status-check var(--atlas-motion-slow) var(--atlas-ease-out) var(--atlas-motion-fast) both; }
@media (prefers-reduced-motion: reduce) {
  .status-badge[data-confirmed] { animation: atlas-backdrop-in var(--atlas-motion-surface) var(--atlas-ease-out); }
  .status-badge[data-confirmed]::after,
  .status-badge[data-confirmed] > span, .status-badge[data-confirmed] :deep(svg circle),
  .status-badge[data-confirmed] :deep(svg path), .status-badge[data-confirmed] :deep(svg polyline) { animation: none; }
}
[data-atlas-reduced-motion] .status-badge[data-confirmed] { animation: atlas-backdrop-in var(--atlas-motion-surface) var(--atlas-ease-out); }
[data-atlas-reduced-motion] .status-badge[data-confirmed]::after,
[data-atlas-reduced-motion] .status-badge[data-confirmed] > span,
[data-atlas-reduced-motion] .status-badge[data-confirmed] :deep(svg circle),
[data-atlas-reduced-motion] .status-badge[data-confirmed] :deep(svg path),
[data-atlas-reduced-motion] .status-badge[data-confirmed] :deep(svg polyline) { animation: none; }
</style>
