<script setup lang="ts">
import { computed, ref } from "vue";
import { afterPaint } from "../idle";
import t from "../i18n/overview";
import { Slider } from "../components/ui/slider";
import { overviewKind, overviewLevel, setOverviewLevel } from "./overviewKinds";

/**
 * The level switch in the head of every overview card (requirements, contacts…): „Detail“ and a slider over the kind's levels (ordered
 * steps of one quantity: arrows, Home/End, a click on a step's letter, or the letter itself, S, M, L, X), set on the one state of the
 * kind on the page or board (`scope`, overviewKinds.ts), so every overview of the kind there moves with it, its thumb travelling
 * (Slider.vue). Why not OptionPills: the levels are less → more of one thing, not alternatives.
 */
const props = defineProps<{ kind: string; scope: string }>();
const levels = computed(() => overviewKind(props.kind)?.levels ?? []);
const level = computed(() => overviewLevel(props.kind, props.scope).value);
// The step pressed shows at once (thumb, letter, aria); the page changes after that frame has painted, so the press answers within one
// frame however big the page is (3.6 budget: feedback ≤ 16 ms).
const pressed = ref<number>();
const step = computed(() => pressed.value ?? Math.max(0, levels.value.findIndex(({ id }) => id === level.value)));
const letter = (index: number) => levels.value[index]!.short ?? levels.value[index]!.id.toUpperCase();
function choose(index: number | undefined) {
  const next = levels.value[index ?? -1];
  if (!next) return;
  pressed.value = index;
  afterPaint(() => { setOverviewLevel(props.kind, props.scope, next.id); if (pressed.value === index) pressed.value = undefined; });
}
/** A press on a step's letter leaves the focus on the slider (not on the page's text around the card), so the arrows go on from there. */
const thumb = (event: MouseEvent) => (event.currentTarget as HTMLElement).closest(".overview-level")?.querySelector<HTMLElement>("[role=slider]")?.focus();
function shortcut(event: KeyboardEvent) {
  if (event.ctrlKey || event.metaKey || event.altKey || event.key.length !== 1) return;
  const index = levels.value.findIndex((_level, at) => letter(at).toLowerCase().startsWith(event.key.toLowerCase()));
  if (index >= 0) { event.preventDefault(); choose(index); }
}
</script>

<template>
  <div v-if="levels.length > 1" class="overview-level" :data-level="level" :data-overview-level="kind" @keydown="shortcut">
    <span class="overview-level-label">{{ t.level.label }}</span>
    <span class="overview-level-scale">
      <Slider :model-value="[step]" :min="0" :max="levels.length - 1" :step="1" :thumb-label="t.level.thumb"
        :value-text="index => levels[index]!.label" @update:model-value="value => choose(value?.[0])" />
      <span class="overview-level-steps" aria-hidden="true">
        <span v-for="(option, index) in levels" :key="option.id" :title="option.label" :data-on="index === step || undefined" @mousedown.prevent @click="choose(index); thumb($event)">{{ letter(index) }}</span>
      </span>
    </span>
  </div>
</template>

<style scoped>
.overview-level { display: inline-flex; align-items: center; gap: .625rem; color: var(--muted, var(--text-dim)); font-size: .8125rem; }
.overview-level-label { font-weight: 600; }
/* Room on both sides for the end names, which reach past the track; under the track room for the thumb (16 px over a 6 px track) and its
   4 px focus ring, so the thumb never covers a name (RC 3.5.1: NONE and XL were hidden under it). */
.overview-level-scale { --step: 2.25rem; display: grid; gap: .625rem; width: 9.5rem; padding: .25rem calc(var(--step) / 2 - 8px) 0; }
/* The step names under the track, each centred under its stop: the thumb's middle runs from 8 px to the track's width less 8 px. */
.overview-level-steps { display: flex; justify-content: space-between; margin: 0 calc(8px - var(--step) / 2); font-size: .6875rem; font-weight: 600; line-height: 1; font-variant-numeric: tabular-nums; }
/* Every name has the same width (V13), NONE included. */
.overview-level-steps > span { display: flex; justify-content: center; width: var(--step); white-space: nowrap; cursor: pointer; transition: color var(--atlas-motion-fast) var(--atlas-ease-out); }
.overview-level-steps > [data-on] { color: var(--accent); }
</style>
