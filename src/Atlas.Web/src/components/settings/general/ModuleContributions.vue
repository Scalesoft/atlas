<script setup lang="ts">
import t from "../../../i18n/settings-modules";
// "Co modul přidá do wiki" (refresh 2.5, board 10): where a module shows up for readers and writers, as quiet
// chips under the module's settings. A private part of the module page template; each module lists its own.
defineProps<{ items: { kind: string; label: string }[] }>();
</script>

<template>
  <section class="st-group" aria-labelledby="module-contributions" data-module-contributions>
    <h3 id="module-contributions" class="st-group-label">{{ t.common.moduleAddsTitle }}</h3>
    <ul class="module-contributions">
      <li v-for="item in items" :key="`${item.kind}-${item.label}`"><span class="module-contribution-kind">{{ item.kind }}</span>{{ item.label }}</li>
    </ul>
  </section>
</template>

<style scoped>
.module-contributions { display: flex; flex-wrap: wrap; gap: 8px; margin: 0; padding: 0; list-style: none; }
.module-contributions > li {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-height: 34px;
  padding: 6px 10px;
  border: 1px solid var(--st-line-soft, var(--line));
  border-radius: 9px;
  background: var(--st-subtle, var(--hover));
  font-weight: 500;
}
.module-contribution-kind { color: var(--muted); font-size: 11px; font-weight: 700; letter-spacing: .05em; text-transform: uppercase; }
</style>
