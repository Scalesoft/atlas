<script setup lang="ts">
import AtlasIcon from "../AtlasIcon.vue";
import type { StatusTone } from "./types";

/**
 * A card in a grid of things to open: task shortcuts, spaces, vaults, groups. With `href` the whole
 * card is one link with a border and shadow response. `#badge` sits beside the icon,
 * the default slot holds facts, `#footer` a call to action. `dashed` is the "add another" card.
 */
withDefaults(
  defineProps<{ title: string; description?: string; icon?: string; iconTone?: StatusTone | string; href?: string; dashed?: boolean; highlighted?: boolean; compact?: boolean; index?: number }>(),
  { description: undefined, icon: undefined, iconTone: undefined, href: undefined, dashed: false, highlighted: false, compact: false, index: 0 },
);
</script>

<template>
  <component
    :is="href ? 'a' : 'article'"
    :href="href"
    class="st-card tile-card"
    :class="{ 'is-interactive': !!href, 'is-dashed': dashed, 'is-compact': compact, 'is-headless': !(icon || $slots.badge || $slots.icon) }"
    :data-new="highlighted || undefined"
    :style="{ '--i': index }"
  >
    <span v-if="icon || $slots.badge || $slots.icon" class="tile-card-head">
      <slot name="icon"><span v-if="icon" class="st-icon-tile" :data-tone="iconTone"><AtlasIcon :name="icon" :size="20" /></span></slot>
      <slot name="badge" />
    </span>
    <span class="tile-card-text">
      <strong>{{ title }}</strong>
      <small v-if="description">{{ description }}</small>
    </span>
    <span v-if="$slots.default" class="tile-card-body"><slot /></span>
    <span v-if="$slots.footer" class="tile-card-footer"><slot name="footer" /><AtlasIcon v-if="href" name="arrow-right" :size="14" class="tile-card-arrow" /></span>
  </component>
</template>

<style scoped>
.tile-card { display: grid; gap: 10px; align-content: start; padding: 16px; color: var(--text); text-decoration: none; min-width: 0; }
/* A card stretched to the height of its row keeps its footer on the bottom line: auto margins do nothing in a grid whose
   rows size to their content, a column does. The compact card is a two-column grid of its own. */
.tile-card:not(.is-compact) { display: flex; flex-direction: column; }
.tile-card:not([data-new]) { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i) * var(--st-stagger)); }
.tile-card.is-dashed { border-style: dashed; background: transparent; box-shadow: none; }
/* As the empty state: a dashed stroke in the line token is barely there in the dark schemes (hover and focus keep their accent). */
:global([data-mode="dark"] .tile-card.is-dashed) { border-color: color-mix(in oklab, var(--input-line) 55%, var(--line)); }
.tile-card-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.tile-card-text { display: grid; gap: 3px; }
.tile-card-text strong { font-size: 15px; }
.tile-card-text small { color: var(--muted); font-size: 12.5px; line-height: 1.5; }
.tile-card-body { display: grid; gap: 6px; min-width: 0; color: var(--muted); font-size: 12.5px; }
.tile-card-footer { display: flex; align-items: center; justify-content: space-between; gap: 8px; color: var(--accent); font-size: 12.5px; font-weight: 600; margin-top: auto; }
.tile-card-arrow { transition: translate var(--atlas-motion-base) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .tile-card:hover .tile-card-arrow { translate: calc(var(--atlas-lift) * 2) 0; }
}
.tile-card.is-compact { grid-template-columns: auto minmax(0, 1fr); gap: 6px 12px; padding: 12px; }
.is-compact .tile-card-head { grid-column: 1; grid-row: 1 / span 3; align-self: start; }
.is-compact .tile-card-head .st-icon-tile { width: 32px; height: 32px; }
.is-compact :is(.tile-card-text, .tile-card-body, .tile-card-footer) { grid-column: 2; }
/* A class, not `:not(:has(.tile-card-head))`: a :has() over a universal child makes Chrome restyle every page editor's subtree on each change. */
.is-compact.is-headless { grid-template-columns: minmax(0, 1fr); }
.is-compact.is-headless > * { grid-column: 1; }
</style>
