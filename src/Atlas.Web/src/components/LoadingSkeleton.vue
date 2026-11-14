<script setup lang="ts">
import t from "../i18n/components-a";
withDefaults(defineProps<{ rows?: number; label?: string; rowHeight?: string; instant?: boolean }>(), {
  rows: 4,
  instant: false,
  rowHeight: undefined,
  label: () => t.loading,
});
</script>

<template>
  <div class="loading-skeleton" :class="{ instant }" role="status" :style="rowHeight ? { '--loading-row-height': rowHeight } : undefined">
    <span class="visually-hidden">{{ label }}</span>
    <div aria-hidden="true" class="loading-skeleton-rows">
      <div v-for="row in rows" :key="row" class="loading-skeleton-row">
        <span class="loading-skeleton-icon" />
        <span class="loading-skeleton-copy"><span /><span /></span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.loading-skeleton { width: 100%; min-width: 0; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both; }
.loading-skeleton.instant { animation: none; }
.loading-skeleton-row { display: flex; align-items: center; gap: 12px; height: var(--loading-row-height, 76px); padding: 9px 8px; box-sizing: border-box; }
.loading-skeleton-row + .loading-skeleton-row { border-top: 1px solid var(--line); }
.loading-skeleton-icon { flex: 0 0 36px; height: 36px; border-radius: var(--radius-md); background: var(--hover); }
.loading-skeleton-copy { display: grid; gap: 9px; flex: 1; }
.loading-skeleton-copy > span { display: block; height: 10px; width: 68%; border-radius: 4px; background: var(--hover); }
.loading-skeleton-copy > span + span { width: 44%; height: 8px; }
.loading-skeleton-row:nth-child(even) .loading-skeleton-copy > span:first-child { width: 54%; }
/* `instant` is for a skeleton that replaces another one or a shown region, so it must not fade in a second time. */
/* The skeleton reserves its space at once but shows only after a short delay, so a fast response never flashes it. */
</style>
