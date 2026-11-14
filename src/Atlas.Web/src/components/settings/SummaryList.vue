<script setup lang="ts">
import t from "../../i18n/settings-shell";
import AtlasIcon from "../AtlasIcon.vue";
import type { SummaryItem } from "./types";

// What will happen, as label/value rows with icons: the summary step of every wizard and read-only
// facts (a vault's file). A value can be replaced with the `value-<key>` slot (badges, code).
withDefaults(defineProps<{ items: SummaryItem[]; label?: string }>(), { label: undefined });
</script>

<template>
  <dl class="summary-list" :aria-label="label ?? t.summary">
    <div v-for="(item, index) in items" :key="item.key" :style="{ '--i': index }" :data-summary="item.key">
      <dt><AtlasIcon :name="item.icon" :size="15" />{{ item.label }}</dt>
      <dd>
        <slot :name="`value-${item.key}`" :item="item"><code v-if="item.code" class="st-mono">{{ item.value }}</code><template v-else>{{ item.value }}</template></slot>
      </dd>
    </div>
  </dl>
</template>

<style scoped>
.summary-list { container: summary-list / inline-size; display: grid; margin: 0; border: 1px solid var(--line); border-radius: var(--st-radius); overflow: hidden; background: var(--surface); }
.summary-list > div {
  display: grid;
  grid-template-columns: minmax(130px, 1fr) minmax(0, 2.6fr);
  gap: 12px;
  padding: 10px 14px;
  border-top: 1px solid var(--line);
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both;
  animation-delay: calc(var(--i) * var(--st-stagger));
}
.summary-list > div:first-child { border-top: 0; }
.summary-list dt { display: flex; align-items: center; gap: 7px; color: var(--muted); font-size: 13px; font-weight: 600; }
.summary-list dd { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0; font-size: 13.5px; min-width: 0; overflow-wrap: anywhere; }
@container summary-list (max-width: 520px) { .summary-list > div { grid-template-columns: 1fr; gap: 3px; } }
</style>
