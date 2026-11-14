<script setup lang="ts">
import t from "../../i18n/settings-flow";
import AtlasIcon from "../AtlasIcon.vue";
import type { ImpactItem } from "./types";

// "Co se stane" before a change is saved: a headline, the concrete effects and what stays
// the same. Items appear one after another so the eye follows the list.
withDefaults(defineProps<{ title?: string; items: ImpactItem[]; unchanged?: string[]; before?: string; after?: string }>(), {
  title: undefined,
  unchanged: () => [],
  before: undefined,
  after: undefined,
});
</script>

<template>
  <div class="impact-preview" role="group" :aria-label="title ?? t.impact.title">
    <div class="impact-head">
      <AtlasIcon name="activity" :size="17" />
      <strong>{{ title ?? t.impact.title }}</strong>
      <span v-if="before && after" class="impact-change">
        <span class="impact-before">{{ before }}</span>
        <AtlasIcon name="arrow-right" :size="14" />
        <span class="impact-after">{{ after }}</span>
      </span>
    </div>
    <ul class="impact-items">
      <li v-for="(item, position) in items" :key="item.label" :data-tone="item.tone" :style="{ '--i': position }">
        <span class="impact-icon"><AtlasIcon :name="item.icon" :size="16" /></span>
        <span class="impact-text"><span>{{ item.label }}</span><small v-if="item.detail">{{ item.detail }}</small></span>
      </li>
    </ul>
    <p v-if="unchanged.length" class="impact-unchanged"><AtlasIcon name="check" :size="14" />{{ t.impact.unchanged(unchanged.join(" · ")) }}</p>
    <slot />
  </div>
</template>

<style scoped>
.impact-preview {
  display: grid;
  gap: 10px;
  padding: 14px 16px;
  border: 1px solid color-mix(in srgb, var(--info) 30%, var(--line));
  border-radius: var(--st-radius);
  background: color-mix(in srgb, var(--info-bg) 55%, var(--surface));
}
.impact-head { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; color: var(--info); }
.impact-head strong { color: var(--text); font-size: 14px; }
.impact-change { display: inline-flex; align-items: center; gap: 6px; margin-left: auto; font-size: 12.5px; font-weight: 600; color: var(--text); }
.impact-before { color: var(--muted); text-decoration: line-through; text-decoration-color: color-mix(in srgb, var(--muted) 60%, transparent); }
.impact-after { padding: 1px 8px; border-radius: 999px; background: var(--surface); }
.impact-items { display: grid; gap: 8px; margin: 0; padding: 0; list-style: none; }
.impact-items li {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both;
  animation-delay: calc(var(--i) * var(--st-stagger));
}
.impact-icon { display: grid; place-items: center; flex-shrink: 0; width: 26px; height: 26px; border-radius: 8px; color: var(--info); background: var(--surface); }
[data-tone="warning"] .impact-icon { color: var(--warning); background: var(--warning-bg); }
[data-tone="success"] .impact-icon { color: var(--success); background: var(--success-bg); }
[data-tone="danger"] .impact-icon { color: var(--error); background: var(--error-bg); }
.impact-text { display: grid; gap: 2px; font-size: 13.5px; line-height: 1.5; padding-top: 2px; }
.impact-text small { color: var(--muted); font-size: 12px; }
.impact-unchanged { display: flex; align-items: flex-start; gap: 6px; margin: 0; color: var(--muted); font-size: 12.5px; }
.impact-unchanged > svg { flex-shrink: 0; margin-top: 3px; color: var(--success); }
</style>
