<script setup lang="ts">
import { computed } from "vue";
import AccessLevelBadge from "../AccessLevelBadge.vue";
import t from "../../../i18n/settings-access";
import type { ChoiceOption } from "../types";
import { accessLevels, allLevelOptions, levelLabel, type AccessLevel } from "./accessKinds";

/**
 * The colour key of the levels in a list and its level filter in one: every level with how many
 * rows have it, highest first; pressing one shows only those rows (aria-pressed), pressing it again
 * shows all. Levels with no rows stay out, so the line says at a glance what someone can do.
 */
const props = defineProps<{ counts: Partial<Record<AccessLevel, number>> }>();
const model = defineModel<string>({ default: "" });
const options: ChoiceOption[] = allLevelOptions();
const shown = computed(() => [...accessLevels].reverse().filter((level) => (props.counts[level] ?? 0) > 0 || model.value === level));
</script>

<template>
  <div class="level-legend" role="group" :aria-label="t.legend.label">
    <button
      v-for="level in shown"
      :key="level"
      type="button"
      class="level-legend-item"
      :aria-pressed="model === level"
      :data-dimmed="model && model !== level ? true : undefined"
      :aria-label="t.legend.item(levelLabel(level), counts[level] ?? 0, model === level)"
      @click="model = model === level ? '' : level"
    >
      <AccessLevelBadge :options="options" :value="level" tone="level" />
      <span class="level-legend-count">{{ counts[level] ?? 0 }}</span>
    </button>
  </div>
</template>

<style scoped>
.level-legend { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; }
.level-legend-item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 6px 3px 3px;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-size: 12.5px;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
  transition: opacity var(--atlas-motion-fast), border-color var(--atlas-motion-fast), background-color var(--atlas-motion-fast);
}
@media (hover: hover) and (pointer: fine) {
  .level-legend-item:hover { background: var(--hover); }
}
.level-legend-item:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.level-legend-item[aria-pressed="true"] { border-color: var(--line); background: var(--surface); color: var(--text); font-weight: 650; }
.level-legend-item[data-dimmed] { opacity: .45; }
.level-legend-count { min-width: 1ch; }
</style>
