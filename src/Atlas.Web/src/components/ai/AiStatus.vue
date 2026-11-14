<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import t from "../../i18n/ai";
import { formatElapsed, type AiStatusStep } from "../../ai/aiStatus";
import AiGlyph, { type AiGlyphState } from "./AiGlyph.vue";

/**
 * The one way Atlas says "the model is working on it" (refresh 2.5, board „AI znak – velikosti, stavy a průběh“,
 * „Jeden stavový řádek všude, kde AI pracuje“): the glyph in the state of the current step, a verb that says what
 * exactly happens, what is known of it, the time it runs and Zastavit where the task can be stopped. Three sizes of
 * the same line, chosen by the surface, never a different indicator:
 *
 * - `xs` inline in a field (the search field while the meaning search still runs): glyph + verb.
 * - `s` in an answer card (Odpověď z wiki, Shrnutí stránky): verb · meta, time, Zastavit and an indeterminate bar.
 *   When the card's heading already carries the glyph (`glyph=false`), the heading's glyph takes the state instead.
 * - `m` in the assistant: the steps of the task (done ✓, now with the glyph, still to come ○), time, a note on what
 *   the answer may read, Zastavit.
 *
 * Why not AiAnswer: AiAnswer owns the whole answer (states, sentences, sources, feedback) and composes this line while it
 * works; a model task without an answer card (the search field) uses the line alone. Why not a spinner: rule-based work
 * has the spinner, model work this line (aiGlyphBoundary.guard.test.ts).
 *
 * The verb and the meta are announced politely; the running time is not (it is hidden from assistive technology).
 * Steps come from ai/aiStatus.ts and name only phases the server reports. Zastavit only where the caller can really
 * stop the task (it aborts the request of the turn).
 */
const props = withDefaults(defineProps<{
  size?: "xs" | "s" | "m";
  state?: AiGlyphState;
  label: string;
  meta?: string | null;
  steps?: readonly AiStatusStep[];
  glyph?: boolean;
  /** Show the running time, counted from `since` (epoch ms) or from when the line appeared. */
  elapsed?: boolean;
  since?: number | null;
  note?: string | null;
  stoppable?: boolean;
  /** The indeterminate bar of a working card (`s`). */
  progress?: boolean;
}>(), { size: "s", state: "thinking", meta: null, steps: () => [], glyph: true, elapsed: false, since: null, note: null, stoppable: false, progress: false });
const emit = defineEmits<{ stop: [] }>();

const started = ref(Date.now());
const now = ref(Date.now());
let timer: number | undefined;
onMounted(() => {
  if (!props.elapsed) return;
  timer = window.setInterval(() => { now.value = Date.now(); }, 1000);
});
onBeforeUnmount(() => window.clearInterval(timer));
const time = computed(() => {
  const ms = now.value - (props.since ?? started.value);
  return props.elapsed && ms >= 1000 ? formatElapsed(ms) : "";
});
const writing = computed(() => props.state === "writing");
const glyphSize = computed(() => (props.size === "xs" ? 12 : 16));
const spoken = computed(() => [props.label, props.meta].filter(Boolean).join(", "));
</script>

<template>
  <div class="ai-status" :data-size="size">
    <template v-if="size === 'm' && steps.length">
      <ol class="ai-status-steps" :aria-label="t.status.progress">
        <li v-for="(step, index) in steps" :key="index" class="ai-status-step" :data-step="step.state">
          <span class="ai-status-mark" aria-hidden="true">
            <svg v-if="step.state === 'done'" viewBox="0 0 24 24" width="16" height="16" class="ai-status-check"><path d="M5 12l5 5 9-10" /></svg>
            <AiGlyph v-else-if="step.state === 'now'" :state="state" :size="16" />
            <i v-else class="ai-status-todo" />
          </span>
          <span class="ai-status-step-label" :class="{ 'atlas-ai-shimmer': step.state === 'now' }" :data-tone="step.state === 'now' ? 'ink' : undefined">{{ step.label }}<span v-if="step.state === 'now' && writing" class="ai-status-dots" aria-hidden="true"><i /><i /><i /></span></span>
          <span v-if="step.meta" class="ai-status-step-meta">{{ step.meta }}</span>
        </li>
      </ol>
      <span class="visually-hidden" role="status">{{ spoken }}</span>
      <slot />
      <div class="ai-status-foot">
        <span class="ai-status-foot-text"><span v-if="time" aria-hidden="true" data-ai-elapsed>{{ time }}</span><template v-if="time && note"> · </template><span v-if="note">{{ note }}</span></span>
        <button v-if="stoppable" type="button" class="ai-status-stop" data-ai-stop @click="emit('stop')"><i aria-hidden="true" />{{ t.status.stop }}</button>
      </div>
    </template>
    <template v-else>
      <div class="ai-status-row">
        <span class="ai-status-text" role="status">
          <AiGlyph v-if="glyph" :state="state" :size="glyphSize" />
          <span class="ai-status-label atlas-ai-shimmer" :data-tone="size === 'xs' ? undefined : 'ink'">{{ label }}</span>
          <span v-if="writing && size !== 'xs'" class="ai-status-dots" aria-hidden="true"><i /><i /><i /></span>
          <span v-if="meta" class="ai-status-meta"> · {{ meta }}</span>
        </span>
        <span v-if="time" class="ai-status-time" aria-hidden="true" data-ai-elapsed>{{ time }}</span>
        <button v-if="stoppable" type="button" class="ai-status-stop" data-ai-stop @click="emit('stop')"><i aria-hidden="true" />{{ t.status.stop }}</button>
      </div>
      <slot />
      <span v-if="progress && size === 's'" class="ai-status-bar" aria-hidden="true"><i /></span>
    </template>
  </div>
</template>

<style scoped>
.ai-status { display: grid; gap: 8px; min-width: 0; color: var(--text); font-size: 13px; line-height: 1.4; }
.ai-status[data-size="xs"] { display: inline-flex; gap: 6px; color: var(--muted); font-size: 12px; }
.ai-status-row { display: flex; align-items: center; gap: 8px; min-width: 0; }
.ai-status[data-size="xs"] .ai-status-row { gap: 6px; }
.ai-status-text { display: inline-flex; flex: 1; align-items: center; gap: 8px; min-width: 0; }
.ai-status[data-size="xs"] .ai-status-text { gap: 6px; }
.ai-status-label { flex: 0 1 auto; min-width: 3em; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
.ai-status[data-size="xs"] .ai-status-label { font-weight: 450; }
.ai-status-meta, .ai-status-time, .ai-status-step-meta, .ai-status-foot-text { color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; white-space: nowrap; }
.ai-status-meta { flex: 0 100 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.ai-status-dots, .ai-status-time { flex: none; }
.ai-status-time { margin-left: auto; }
.ai-status-dots { display: inline-flex; align-items: center; gap: 3px; margin-left: 2px; }
.ai-status-dots > i { width: 4px; height: 4px; border-radius: 2px; background: var(--ai-ink); }
.ai-status-bar { position: relative; display: block; height: 3px; overflow: hidden; border-radius: 2px; background: var(--ai-wash-strong); }
.ai-status-bar > i { position: absolute; inset: 0 auto 0 0; width: 40%; border-radius: inherit; background: linear-gradient(90deg, var(--accent), var(--ai-ink)); }
.ai-status-stop { display: inline-flex; flex: none; align-items: center; gap: 6px; height: 28px; padding: 0 10px; border: 1px solid var(--input-line); border-radius: 8px; background: var(--surface); color: var(--text); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.ai-status-stop > i { width: 8px; height: 8px; border-radius: 2px; background: currentColor; }
@media (hover: hover) and (pointer: fine) { .ai-status-stop:hover { background: var(--hover); border-color: var(--muted); } }
.ai-status-stop:focus-visible { outline: var(--focus-ring, 2px solid var(--accent)); outline-offset: 2px; }
.ai-status-steps { display: grid; gap: 2px; margin: 0; padding: 0; list-style: none; }
.ai-status-step { display: flex; align-items: center; gap: 10px; min-height: 26px; color: var(--text); font-weight: 500; }
.ai-status-step[data-step="now"] { font-weight: 650; }
.ai-status-step[data-step="todo"] { color: var(--muted); }
.ai-status-step-label { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ai-status-mark { display: inline-grid; flex: none; place-items: center; width: 16px; height: 16px; }
.ai-status-check { fill: none; stroke: var(--success); stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }
.ai-status-todo { width: 8px; height: 8px; border: 1.5px solid var(--muted); border-radius: 50%; opacity: .7; }
.ai-status-foot { display: flex; align-items: center; gap: 8px; min-width: 0; }
.ai-status-foot-text { flex: 1; min-width: 0; white-space: normal; }
</style>
