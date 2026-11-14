<script setup lang="ts">
import { computed } from "vue";
import { EFFORT_STEPS, type AnswerEffort } from "../../ai/answerEffort";
import t from "../../i18n/ai-answer";
import AtlasIcon from "../AtlasIcon.vue";
import { Popover, PopoverContent, PopoverTrigger } from "../ui/popover";
import AiEffortMeter from "./AiEffortMeter.vue";

/**
 * The answer effort where the question is written: a small button with the chosen step (five little blocks lit in the AI
 * colours and its name); a click opens AiEffortMeter in a popover above it, where the step changes. The meter is the
 * picker's content, not a second control for the same choice.
 */
const model = defineModel<AnswerEffort>({ required: true });
defineProps<{ disabled?: boolean }>();
const level = computed(() => Math.max(0, EFFORT_STEPS.findIndex((step) => step.value === model.value)));
const current = computed(() => EFFORT_STEPS[level.value]!);
</script>

<template>
  <Popover>
    <PopoverTrigger as-child>
      <button type="button" class="ai-effort-pick" :disabled="disabled" :data-level="level" :aria-label="t.effort.named(current.label)" :title="t.effort.namedFull(current.label, current.sources)" data-ai-effort-pick>
        <span class="ai-effort-pick-bars" aria-hidden="true"><i v-for="(step, i) in EFFORT_STEPS" :key="step.value" :data-lit="i <= level || undefined" /></span>
        <span class="ai-effort-pick-label">{{ current.label }}</span>
        <AtlasIcon name="chevron-down" :size="12" />
      </button>
    </PopoverTrigger>
    <PopoverContent class="ai-effort-pop" side="top" align="end" :side-offset="8" data-ai-effort-pop>
      <AiEffortMeter v-model="model" />
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.ai-effort-pick { display: inline-flex; flex: none; align-items: center; gap: 6px; height: 32px; padding: 0 8px; border: 0; border-radius: 8px; background: transparent; color: var(--muted); font: inherit; font-size: 12px; font-weight: 550; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .ai-effort-pick:hover { background: var(--ai-wash); color: var(--text); } }
.ai-effort-pick[data-state="open"] { background: var(--ai-wash-strong); color: var(--text); }
.ai-effort-pick:disabled { opacity: .45; cursor: default; }
.ai-effort-pick:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
/* Five rising blocks: the chosen step and those below it lit, in the meter's colours (styles/ai.css, .ai-effort-pick). */
.ai-effort-pick-bars { display: inline-flex; align-items: flex-end; gap: 1.5px; height: 12px; }
.ai-effort-pick-bars > i { width: 3px; border-radius: 1px; background: color-mix(in srgb, var(--text) 18%, transparent); transition: background-color var(--atlas-motion-base) var(--atlas-ease-out); }
.ai-effort-pick-bars > i:nth-child(1) { height: 40%; }
.ai-effort-pick-bars > i:nth-child(2) { height: 55%; }
.ai-effort-pick-bars > i:nth-child(3) { height: 70%; }
.ai-effort-pick-bars > i:nth-child(4) { height: 85%; }
.ai-effort-pick-bars > i:nth-child(5) { height: 100%; }
.ai-effort-pick-bars > i[data-lit] { background: linear-gradient(var(--ai-effort-to), var(--ai-effort-from)); }
</style>

<style>
/* PopoverContent is portalled, out of this component's scope: a narrow card, not the panel's width. */
.ai-effort-pop { width: 280px; padding: 14px 16px 16px; }
</style>
