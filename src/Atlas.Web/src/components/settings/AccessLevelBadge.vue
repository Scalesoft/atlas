<script setup lang="ts">
import t from "../../i18n/settings-grants";
import { computed } from "vue";
import type { ChoiceOption } from "./types";

// A read-only level of an access staircase: a small stair filled up to the level and its name.
// Takes the same ordered options as AccessLevelPicker, so lists never need a column per right.
const props = withDefaults(defineProps<{ options: ChoiceOption[]; value: string; tone?: "accent" | "neutral" | "level" }>(), { tone: "accent" });
const index = computed(() => props.options.findIndex((option) => option.value === props.value));
const label = computed(() => props.options[index.value]?.label ?? props.value);
const includes = computed(() => props.options.slice(0, Math.max(0, index.value)).map((option) => option.label));
const spoken = computed(() => t.level.spoken(label.value, includes.value.length ? t.list(includes.value) : undefined));
</script>

<template>
  <span class="level-badge" :data-tone="tone" :data-level="value" :data-level-color="tone === 'level' ? value : undefined" role="img" :aria-label="spoken">
    <span class="level-badge-steps" aria-hidden="true">
      <i v-for="(option, step) in options" :key="option.value" :data-on="step <= index || undefined" :style="{ height: `${4 + step * 3}px` }" />
    </span>
    <span class="level-badge-label" aria-hidden="true">{{ label }}</span>
  </span>
</template>

<style scoped>
.level-badge { display: inline-flex; align-items: center; gap: 7px; padding: 2px 9px 2px 7px; border-radius: 999px; color: var(--accent); background: var(--accent-soft); font-size: 12px; font-weight: 650; white-space: nowrap; }
.level-badge[data-tone="neutral"] { color: var(--muted); background: var(--hover); }
.level-badge[data-tone="level"] { color: var(--level, var(--accent)); background: var(--level-bg, var(--accent-soft)); }
.level-badge-steps { display: inline-flex; align-items: flex-end; gap: 2px; height: 11px; }
.level-badge-steps i { display: block; width: 4px; border-radius: 1.5px; background: color-mix(in srgb, currentColor 22%, transparent); transition: background-color var(--atlas-motion-base) var(--atlas-ease-out); }
.level-badge-steps i[data-on] { background: currentColor; }
</style>
