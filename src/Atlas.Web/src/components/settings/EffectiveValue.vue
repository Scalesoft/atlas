<script setup lang="ts">
import { locale } from "../../i18n";
import t from "../../i18n/settings-grants";
import { computed } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import StatusBadge from "./StatusBadge.vue";
import type { OriginStep } from "./types";

/**
 * The value in effect and where it comes from ("odkud"). The chain runs from the broadest level
 * (instance) to the target; the nearest level with its own value wins. The target shows
 * Vlastní or Zděděno, and offers to set its own value or return to the inherited one.
 */
const props = withDefaults(defineProps<{ chain: OriginStep[]; label?: string; readonly?: boolean }>(), { label: undefined, readonly: false });
const emit = defineEmits<{ customize: []; inherit: [] }>();

const sourceIndex = computed(() => {
  for (let index = props.chain.length - 1; index >= 0; index--) if (props.chain[index]!.explicit) return index;
  return -1;
});
const source = computed(() => (sourceIndex.value >= 0 ? props.chain[sourceIndex.value] : undefined));
const target = computed(() => props.chain[props.chain.length - 1]);
const own = computed(() => sourceIndex.value === props.chain.length - 1);
const originText = computed(() => {
  if (!source.value) return t.effective.none;
  if (own.value) return t.effective.own(target.value?.level.toLocaleLowerCase(locale) ?? "");
  return t.effective.inherited(source.value.level.toLocaleLowerCase(locale));
});
</script>

<template>
  <div class="effective-value" :data-own="own || undefined">
    <div class="effective-main">
      <span class="effective-label">{{ label ?? t.effective.label }}</span>
      <strong class="effective-current" aria-live="polite">{{ source?.value ?? "—" }}</strong>
      <StatusBadge :tone="own ? 'accent' : 'neutral'" :icon="own ? 'pencil' : 'arrow-right-left'" :label="own ? t.effective.ownBadge : t.effective.inheritedBadge" />
    </div>
    <p class="effective-origin">{{ originText }}</p>
    <ol class="effective-chain" :aria-label="t.effective.origin">
      <li v-for="(step, index) in chain" :key="step.level" :data-source="index === sourceIndex || undefined" :data-overridden="index < sourceIndex && step.explicit ? true : undefined">
        <component :is="step.href ? 'a' : 'span'" :href="step.href" class="effective-step">
          <AtlasIcon v-if="step.icon" :name="step.icon" :size="15" />
          <span class="effective-step-level">{{ step.level }}</span>
          <span class="effective-step-value">{{ step.explicit ? step.value : t.effective.takes }}</span>
        </component>
        <AtlasIcon v-if="index < chain.length - 1" name="right" :size="14" class="effective-arrow" />
      </li>
    </ol>
    <div v-if="!readonly" class="effective-actions">
      <!-- The one place to change the value: no second "Změnit" elsewhere on the page. -->
      <Button v-if="own" variant="outline" size="sm" type="button" data-effective-change @click="emit('customize')"><AtlasIcon name="pencil" :size="15" />{{ t.effective.change }}</Button>
      <Button v-if="own && chain.length > 1" variant="ghost" size="sm" type="button" data-effective-inherit @click="emit('inherit')"><AtlasIcon name="undo-2" :size="15" />{{ t.effective.revert }}</Button>
      <Button v-else-if="!own" variant="outline" size="sm" type="button" data-effective-customize @click="emit('customize')"><AtlasIcon name="pencil" :size="15" />{{ t.effective.custom }}</Button>
    </div>
  </div>
</template>

<style scoped>
.effective-value {
  display: grid;
  gap: 8px;
  padding: 14px 16px;
  border: 1px solid var(--line);
  border-radius: var(--st-radius);
  background: var(--surface);
}
.effective-main { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 10px; }
.effective-label { color: var(--muted); font-size: 12.5px; }
.effective-current { font-size: 17px; font-weight: 650; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.effective-origin { margin: 0; color: var(--muted); font-size: 12.5px; }
.effective-chain { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 2px 0 0; padding: 0; list-style: none; }
.effective-chain li { display: inline-flex; align-items: center; gap: 6px; }
.effective-step {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 10px;
  border: 1px dashed var(--line);
  border-radius: 999px;
  color: var(--muted);
  font-size: 12.5px;
  text-decoration: none;
  transition: border-color var(--atlas-motion-fast), background-color var(--atlas-motion-base), color var(--atlas-motion-base);
}
@media (hover: hover) and (pointer: fine) {
  a.effective-step:hover { border-color: var(--accent); color: var(--text); }
}
a.effective-step:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.effective-step-level { font-weight: 600; }
[data-source] .effective-step { border-style: solid; border-color: var(--accent); color: var(--accent); background: var(--accent-soft); }
[data-overridden] .effective-step-value { text-decoration: line-through; }
.effective-arrow { color: var(--muted); }
.effective-actions { display: flex; gap: 8px; margin-top: 2px; }
</style>
