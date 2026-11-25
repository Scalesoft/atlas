<script setup lang="ts">
import { computed, ref } from "vue";
import StatusBadge from "../../settings/StatusBadge.vue";
import t from "../../../i18n/components-release";
import { useDemoLoop } from "./demoClock";

// Stav obsahu (5.2): the space's counts by verification state count up, each page's readership fills in (counts, never names), and the
// list settles into its default order, the most read pages that need verifying first.
const d = t.demos.health;
type Page = (typeof d.pages)[number];
const root = ref<HTMLElement>();
const counts = ref(d.tiles.map(() => 0)), filled = ref(false), sorted = ref(false), lit = ref(false);
const most = Math.max(...d.pages.map((page) => page.readers));
// Priority: what needs verifying, by readership (an expired page before an expiring one, verified last).
const weight = (page: Page) => ({ expired: 2, expiring: 1 } as Record<string, number>)[page.state] ?? 0;
const order = computed(() => {
  const ranked = sorted.value ? [...d.pages].sort((a, b) => weight(b) - weight(a) || b.readers - a.readers) : [...d.pages];
  return new Map(ranked.map((page, index) => [page.title, index]));
});
const badge = (state: string) => ({ tone: state === "expired" ? "warning" : "neutral", icon: state === "expiring" ? "clock" : state === "expired" ? "triangle-alert" : "shield-check" } as const);

useDemoLoop(root, (at) => {
  at(0, () => { counts.value = d.tiles.map(() => 0); filled.value = false; sorted.value = false; lit.value = false; });
  for (let step = 1; step <= 12; step++) at(200 + step * 70, () => { counts.value = d.counts.map((count) => Math.round(count * step / 12)); });
  at(1300, () => { filled.value = true; });
  at(3000, () => { sorted.value = true; });
  at(3700, () => { lit.value = true; });
  at(6900, () => { lit.value = false; });
  return 7600;
}, () => { counts.value = [...d.counts]; filled.value = true; sorted.value = true; });
</script>

<template>
  <div ref="root" class="release-demo demo-health" aria-hidden="true">
    <div class="demo-health-tiles">
      <span v-for="(label, index) in d.tiles" :key="label" class="demo-health-tile" :data-warn="index > 0 || undefined"><b>{{ counts[index] }}</b><small>{{ label }}</small></span>
    </div>
    <ol class="demo-health-rows">
      <li v-for="page in d.pages" :key="page.title" class="demo-health-row" :class="{ 'is-lit': lit && order.get(page.title) === 0 }" :style="{ '--row': order.get(page.title) }">
        <StatusBadge v-bind="badge(page.state)" :label="d.states[page.state as keyof typeof d.states]" size="sm" />
        <span class="demo-health-title">{{ page.title }}</span>
        <span class="demo-health-bar"><span :style="{ scale: filled ? `${page.readers / most} 1` : '0 1' }" /></span>
        <span class="demo-health-count">{{ filled ? d.readers(page.readers) : "" }}</span>
      </li>
    </ol>
  </div>
</template>

<style scoped>
.demo-health { display: grid; grid-template-rows: auto 1fr; gap: 10px; height: 100%; padding: 12px 14px; overflow: hidden; font-size: 12px; }
.demo-health-tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 6px; }
.demo-health-tile { display: grid; gap: 1px; min-width: 0; padding: 5px 8px; border: 1px solid var(--line); border-radius: 9px; background: var(--surface); }
.demo-health-tile b { font-size: 15px; font-variant-numeric: tabular-nums; }
.demo-health-tile[data-warn] b { color: var(--warning); }
.demo-health-tile:first-child b { color: var(--accent); }
.demo-health-tile small { overflow: hidden; color: var(--muted); font-size: 10.5px; text-overflow: ellipsis; white-space: nowrap; }
.demo-health-rows { position: relative; margin: 0; padding: 0; list-style: none; }
/* Rows stand by their place in the order and glide to a new one: the default order, most read pages that need verifying first. */
.demo-health-row {
  position: absolute; inset: 0 0 auto; display: grid; grid-template-columns: auto minmax(0, 1fr) 40px 58px; align-items: center; gap: 6px;
  height: 24px; padding: 0 6px; border-radius: 7px; translate: 0 calc(var(--row) * 26px);
  transition: translate var(--atlas-motion-slow) var(--atlas-ease-out), background-color var(--atlas-motion-slow) var(--atlas-ease-out);
}
.demo-health-row.is-lit { background: color-mix(in srgb, var(--warning) 10%, transparent); }
.demo-health-row :deep(.status-badge) { padding-inline: 4px; }
.demo-health-row :deep(.status-badge) > span { display: none; }
.demo-health-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.demo-health-bar { height: 5px; overflow: hidden; border-radius: 3px; background: color-mix(in srgb, var(--text) 7%, transparent); }
.demo-health-bar > span { display: block; height: 100%; border-radius: inherit; background: var(--accent); transform-origin: left; transition: scale var(--atlas-motion-slow) var(--atlas-ease-out); }
.demo-health-count { color: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; }
</style>
