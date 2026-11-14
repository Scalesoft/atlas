<script setup lang="ts">
import shell from "../../i18n/settings-shell";
import t from "../../i18n/settings-flow";
import { computed, nextTick, ref, useId, watch } from "vue";
import { useHeightEase } from "../../composables/useHeightEase";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import type { FlowStep } from "./types";

/**
 * The one wizard of the settings UI (owner decision 27. 9.): one
 * decision per step, a step bar with icons ("1 Co · 2 Kde · 3 Soubor"), Zpět/Další and one step on
 * stage at a time (slot named after the step id). The parent only describes the steps: which
 * apply (absent steps are skipped), whether the current one is `ready`, how to `validate` it on
 * Další, and its button. StepFlow owns navigation, focus and the transitions (rise and fade; only
 * fade with reduced motion). Další moves to the next step and emits `next`; a `manual` step (an
 * async submit) and the last step only emit, the parent decides where to go.
 */
const props = withDefaults(defineProps<{ steps: FlowStep[]; label: string; context?: string; busy?: boolean; cancelLabel?: string }>(), {
  context: undefined,
  busy: false,
  cancelLabel: undefined,
});
const current = defineModel<string>({ required: true });
const emit = defineEmits<{ next: [step: FlowStep]; back: [step: FlowStep]; cancel: [] }>();
const uid = useId();
const stage = ref<HTMLElement>();
const index = computed(() => Math.max(0, props.steps.findIndex((step) => step.id === current.value)));
const step = computed(() => props.steps[index.value]!);
const final = computed(() => !!step.value.final);
let moved = false;
// Which way the flow went decides which way the steps move: forward they leave upwards and the next rises from below,
// back they leave downwards and the previous comes down from above (data-direction, the rise token turned round).
const direction = ref<"forward" | "back">("forward");
watch(index, (now, before) => { direction.value = now < before ? "back" : "forward"; }, { flush: "pre" });
watch(current, () => {
  moved = true;
});
function goTo(id: string) {
  if (props.busy || final.value) return;
  const target = props.steps.findIndex((item) => item.id === id);
  if (target >= 0 && target < index.value) current.value = id;
}
function back() {
  if (props.busy || index.value === 0) return;
  emit("back", step.value);
  current.value = props.steps[index.value - 1]!.id;
}
function next() {
  const active = step.value;
  if (props.busy || active.ready === false || active.validate?.() === false) return;
  emit("next", active);
  const following = props.steps[index.value + 1];
  if (following && !active.manual) current.value = following.id;
}
// The stage eases from the height of the step that left to the height of the one that arrives, so the buttons below
// glide instead of jumping when two steps differ in height (the step leaves first, then the next one rises in).
const { rememberHeight, easeHeight } = useHeightEase(stage);
function focusQuestion() {
  if (!moved) return;
  moved = false;
  void nextTick(() => {
    const root = stage.value;
    const preferred = root?.querySelector<HTMLElement>("[data-step-focus]");
    const target = preferred ?? root?.querySelector<HTMLElement>("[data-step-heading]");
    target?.focus({ preventScroll: true });
    root?.scrollIntoView?.({ block: "nearest" });
  });
}
</script>

<template>
  <div class="step-flow" :data-direction="direction" :aria-busy="busy || undefined">
    <div class="step-flow-top">
      <span class="step-flow-context">{{ context ?? label }}</span>
      <span class="step-flow-count" aria-live="polite">{{ t.flow.step(index + 1, steps.length) }}</span>
    </div>
    <ol class="step-flow-progress" :aria-label="t.flow.progress(label)">
      <li v-for="(item, position) in steps" :key="item.id" :data-state="position < index ? 'done' : position === index ? 'current' : 'todo'">
        <button
          v-if="position < index && !final"
          type="button"
          class="step-chip"
          :disabled="busy"
          :aria-label="t.flow.stepChip(position + 1, item.label, item.summary)"
          :data-step-chip="item.id"
          @click="goTo(item.id)"
        >
          <span class="step-chip-mark"><AtlasIcon name="check" :size="13" /></span>
          <span class="step-chip-text"><b>{{ position + 1 }}</b> {{ item.summary ?? item.label }}</span>
        </button>
        <span v-else class="step-chip" :aria-current="position === index ? 'step' : undefined" :data-step-chip="item.id">
          <span class="step-chip-mark"><AtlasIcon :name="position < index ? 'check' : item.icon ?? 'circle-help'" :size="13" /></span>
          <span class="step-chip-text"><b>{{ position + 1 }}</b> {{ item.label }}</span>
        </span>
        <AtlasIcon v-if="position < steps.length - 1" name="right" :size="13" class="step-flow-sep" />
      </li>
    </ol>
    <div ref="stage" class="step-flow-stage">
      <Transition name="st-step" mode="out-in" @before-leave="rememberHeight" @enter="easeHeight" @after-enter="focusQuestion">
        <section :key="step.id" class="step-flow-step" :aria-labelledby="`${uid}-${step.id}`" :data-step="step.id">
          <div class="step-flow-question">
            <span class="st-icon-tile step-flow-icon"><AtlasIcon :name="step.icon ?? 'circle-help'" :size="20" /></span>
            <div>
              <h3 :id="`${uid}-${step.id}`" tabindex="-1" data-step-heading>{{ step.title }}</h3>
              <p v-if="step.hint" class="step-flow-hint">{{ step.hint }}</p>
            </div>
          </div>
          <slot :name="step.id" :step="step" />
        </section>
      </Transition>
    </div>
    <div v-if="!final || $slots.actions" class="step-flow-actions">
      <Button v-if="!final" variant="ghost" type="button" class="step-flow-cancel" :disabled="busy" data-flow-cancel @click="emit('cancel')"><AtlasIcon name="close" :size="16" />{{ cancelLabel ?? shell.cancel }}</Button>
      <span class="step-flow-spacer" />
      <slot name="actions" :step="step" :index="index" />
      <Button v-if="index > 0 && !final" variant="outline" type="button" :disabled="busy" data-flow-back @click="back"><AtlasIcon name="back" :size="16" />{{ t.flow.back }}</Button>
      <Button v-if="!final && !step.hideNext" type="button" :disabled="busy || step.ready === false" data-flow-next @click="next">{{ step.nextLabel ?? t.flow.next }}<AtlasIcon :name="step.nextIcon ?? 'arrow-right'" :size="16" /></Button>
    </div>
  </div>
</template>

<style scoped>
.step-flow { display: grid; gap: 14px; min-width: 0; container: step-flow / inline-size; --step-rise-back: calc(var(--atlas-rise) * -1); }
/* Only while the step travels: the turned rise on a step that has settled would make whatever rises in inside it later (a row, a card,
   a tile) come down from above until the next move forward. */
.step-flow[data-direction="back"] .step-flow-step:is(.st-step-enter-active, .st-step-leave-active) { --atlas-rise: var(--step-rise-back); }
.step-flow-top { display: flex; justify-content: space-between; gap: 12px; color: var(--muted); font-size: 12px; }
.step-flow-context { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.step-flow-count { flex-shrink: 0; font-variant-numeric: tabular-nums; }
.step-flow-progress { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; margin: 0; padding: 0; list-style: none; }
.step-flow-progress li { display: inline-flex; align-items: center; gap: 4px; min-width: 0; }
.step-flow-sep { color: var(--line); flex-shrink: 0; }
.step-chip {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  max-width: 220px;
  min-height: 32px;
  padding: 3px 11px 3px 4px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--surface);
  color: var(--muted);
  font: inherit;
  font-size: 12.5px;
  transition: border-color var(--atlas-motion-fast), background-color var(--atlas-motion-base), color var(--atlas-motion-base), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
.step-chip b { font-weight: 700; font-variant-numeric: tabular-nums; }
button.step-chip { cursor: pointer; color: var(--text); }
@media (hover: hover) and (pointer: fine) {
  button.step-chip:hover:not(:disabled) { border-color: var(--accent); }
}
button.step-chip:active:not(:disabled) { transform: scale(var(--atlas-press)); }
button.step-chip:disabled { cursor: default; }
button.step-chip:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.step-chip-text { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.step-chip-mark {
  display: grid;
  place-items: center;
  flex-shrink: 0;
  width: 24px;
  height: 24px;
  border-radius: 50%;
  background: var(--hover);
  color: var(--muted);
  transition: background-color var(--atlas-motion-base), color var(--atlas-motion-base);
}
[data-state="done"] .step-chip-mark { background: var(--success-bg); color: var(--success); }
[data-state="done"] .step-chip-mark :deep(svg) { animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
[data-state="current"] .step-chip { border-color: var(--accent); color: var(--accent); font-weight: 600; background: var(--accent-soft); }
[data-state="current"] .step-chip-mark { background: var(--accent); color: var(--on-accent); }
.step-flow-stage { min-width: 0; min-height: 160px; }
.step-flow-step { display: grid; gap: 14px; min-width: 0; }
.step-flow-question { display: flex; align-items: flex-start; gap: 12px; }
.step-flow-icon { width: 42px; height: 42px; }
.step-flow-question h3 { margin: 2px 0 0; font-size: 19px; font-weight: 650; letter-spacing: -.02em; outline: none; }
.step-flow-question h3:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; --focus-offset: 3px; border-radius: 4px; }
.step-flow-hint { margin: 3px 0 0; color: var(--muted); font-size: 13px; line-height: 1.55; }
.step-flow-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding-top: 14px; border-top: 1px solid var(--line); }
.step-flow-spacer { flex: 1; }
@container step-flow (max-width: 560px) {
  .step-flow-progress [data-state="todo"] .step-chip-text, .step-flow-progress [data-state="done"] .step-chip-text { display: none; }
  .step-flow-progress [data-state="todo"] .step-chip, .step-flow-progress [data-state="done"] .step-chip { padding-right: 4px; }
  .step-flow-sep { display: none; }
}
</style>
