<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from "vue";
import type { AiProvenanceView, AiScope, AiSentenceView, AiSourceView } from "../../ai/assistantApi";
import t from "../../i18n/ai-answer";
import ai from "../../i18n/ai";
import { animateAtlas, flyGhost, motionMs, prefersReducedMotion, revealHeight, revealSentences, riseIn, solidify } from "../../motion/aiMotion";
import EmptyState from "../settings/EmptyState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import type { AgentStepView } from "../../assistant/agentStream";
import ErrorState from "../settings/ErrorState.vue";
import { Button } from "../ui/button";
import AiCitation from "./AiCitation.vue";
import AiFeedback from "./AiFeedback.vue";
import AiGlyph from "./AiGlyph.vue";
import AiProvenance from "./AiProvenance.vue";
import AiSourceList from "./AiSourceList.vue";
import AiStatus from "./AiStatus.vue";
import type { AiGlyphState } from "./AiGlyph.vue";
import { tokenizeSentence, type SentenceToken } from "./aiAnswerText";
import { answerTurnStatus, type AiTaskKind } from "../../ai/aiStatus";

/**
 * Output of a model, with its sources (UX design §5.3, §6.3): sentences with numbered citations, the state model of every AI
 * operation, the line of origin, the thumbs and the notice. One component for the answer card of omnisearch (`compact`), the
 * summary and the message of the assistant (`full`), instead of three look-alike cards. It draws the AI glyph (and, through
 * AiStatus, the working line) only while model output or a model task is on screen: there is no glyph in `unavailable`,
 * `error`, `limit`, `withheld` and `no_sources`.
 *
 * It is presentational: the parent owns the state (see ai/answerTurn.ts) and passes it in; nothing here calls the server.
 *
 * - Sentences are plain text with `code` spans (aiAnswerText.ts); a citation exists only for a number that is in `sources`.
 * - Fokus se nepřesouvá. A finished answer is announced once by a hidden live region.
 * - While the model works, the one AI status line (AiStatus) says what happens: in `compact` the card's heading glyph takes the
 *   state of the step and the line below it says the verb, the sources found, the time and Zastavit, over an indeterminate bar;
 *   in `full` (the assistant) the steps are listed beside the avatar glyph. Zastavit is offered in both phases: stopping aborts
 *   the request of the turn (search, page summary, assistant), it never only hides the card.
 * - Motion (aiMotion.ts primitives, the page's tokens): the offer unfolds (revealHeight), starting joins the dashed outline
 *   (solidify), the sources fly in from `sourceOrigin` (flyGhost, 260 ms apart; without it or with reduced motion they rise in
 *   or fade), a finished answer is revealed sentence by sentence over the finished text (revealSentences). Hover or focus on
 *   a citation lights its sentence, source chip and, through `highlight`, the result row (120 ms).
 */
export type AiAnswerState = "offer" | "retrieving" | "preparing" | "done" | "no_sources" | "unavailable" | "error" | "limit" | "withheld";

const props = withDefaults(defineProps<{
  state: AiAnswerState;
  variant?: "compact" | "full";
  format?: "paragraph" | "bullets";
  truncated?: boolean;
  sentences?: readonly AiSentenceView[];
  sources?: readonly AiSourceView[];
  provenance?: AiProvenanceView | null;
  createdAt?: string | Date | null;
  scope?: AiScope | null;
  scopeName?: string | null;
  rating?: "up" | "down" | null;
  headingLevel?: 2 | 3 | 4 | 5;
  title?: string;
  /** Where source `n` comes from (its result row); the chip flies from there. */
  sourceOrigin?: (number: number) => Element | null;
  /** A finished answer folds its sources into „Zdroje ▸ n“ (the assistant thread); while it works the tray stays open. */
  collapsibleSources?: boolean;
  unavailableText?: string;
  limitText?: string;
  /** Which model task this is, for the words of the status line („Píšu shrnutí“). */
  task?: AiTaskKind;
  /** The tool steps of an agent turn (assistant.agent): they run live in the status line, then fold into „Použité kroky ▸ n“. */
  agentSteps?: readonly AgentStepView[];
}>(), {
  variant: "compact", format: "paragraph", truncated: false, sentences: () => [], sources: () => [], provenance: null, createdAt: null, scope: null, scopeName: null, rating: null,
  headingLevel: 3, title: () => t.answer.title, sourceOrigin: undefined, collapsibleSources: false, agentSteps: () => [], unavailableText: () => ai.unavailable, limitText: () => ai.limit, task: "answer",
});
const emit = defineEmits<{
  stop: [];
  retry: [];
  "ask-again": [];
  "try-other": [];
  highlight: [number: number | null];
  "open-source": [source: AiSourceView, newTab: boolean];
  rate: [rating: "up" | "down"];
  feedback: [feedback: { rating: "down"; reason: string | null; note: string | null }];
}>();

let gathering: Promise<void> = Promise.resolve();
let gatherEpoch = 0;
let textHold: Animation | null = null;
const root = ref<HTMLElement>();
const text = ref<HTMLElement>();
const outline = ref<SVGRectElement>();
const highlighted = ref<number | null>(null);
const hiddenNumbers = ref<number[]>([]);
const announcement = ref("");

const byNumber = computed(() => new Map(props.sources.map((source) => [source.number, source])));
/** Citations of a sentence: only numbers that name a source of the list, once each, in order. */
const cited = (sentence: AiSentenceView) => [...new Set(sentence.sources)].map((n) => byNumber.value.get(n)).filter((source): source is AiSourceView => !!source);
/**
 * A citation is an inline box, and the line may break before it: the marker then opened the next line on its own, away from
 * its sentence. The last word of a cited sentence goes into a tail that does not wrap, together with its markers. A very
 * long last word (a URL) stays outside, where it may break.
 */
function splitTail(tokens: SentenceToken[]): { tokens: SentenceToken[]; tail: SentenceToken[] } {
  const last = tokens.at(-1);
  if (!last) return { tokens, tail: [] };
  if (last.kind === "code") return last.text.length <= 32 ? { tokens: tokens.slice(0, -1), tail: [last] } : { tokens, tail: [] };
  const word = /\S+\s*$/.exec(last.text);
  if (!word || word[0].length > 32) return { tokens, tail: [] };
  const head = last.text.slice(0, word.index);
  return { tokens: [...tokens.slice(0, -1), ...(head ? [{ kind: "text" as const, text: head }] : [])], tail: [{ kind: "text", text: word[0] }] };
}
/** The finished answer lists the sources its sentences cite; the rest stay in its provenance („Podklady odpovědi“). */
const listed = computed(() => {
  const numbers = new Set(props.sentences.flatMap((sentence) => sentence.sources));
  const used = props.sources.filter((source) => numbers.has(source.number));
  return used.length ? used : props.sources;
});
const rows = computed(() => props.sentences.map((sentence) => {
  const citations = cited(sentence);
  const tokens = tokenizeSentence(sentence.text);
  return { ...(citations.length ? splitTail(tokens) : { tokens, tail: [] as SentenceToken[] }), citations };
}));
const shown = computed(() => props.state === "done" || props.state === "preparing" || props.state === "retrieving");
const hasCard = computed(() => props.variant === "compact" && ["offer", "retrieving", "preparing", "done"].includes(props.state));
const hasHeader = computed(() => ["offer", "retrieving", "preparing", "done"].includes(props.state));
const heading = computed(() => `h${props.headingLevel}`);
const outlineRadius = 11.5;
const working = computed(() => props.state === "retrieving" || props.state === "preparing");
const baseStatus = computed(() => answerTurnStatus(props.state, { sources: props.sources.length, kind: props.task, scope: props.scope, scopeName: props.scopeName }));
/** An agent turn names its own steps (one line per tool call, then the writing); a failed call reads as finished with a note. */
const status = computed(() => {
  const base = baseStatus.value;
  if (!base || !props.agentSteps.length) return base;
  const running = props.agentSteps.find((step) => step.state === "now");
  const steps = props.agentSteps.map((step) => ({ label: step.label, meta: step.state === "failed" ? t.status.noResult : null, state: step.state === "now" ? "now" as const : "done" as const }));
  return { glyph: running ? "searching" as const : "writing" as const, label: running ? `${running.label}…` : ai.status.writeAnswer, meta: null,
    steps: [...steps, { label: ai.status.writeAnswer, state: running ? "todo" as const : "now" as const }] };
});
const stepsOpen = ref(false);
const stepsId = useId();
/** The glyph says "done" once, when this answer has just been finished on screen; a loaded answer is simply there. */
const finishedHere = ref(false);
const headGlyph = computed<AiGlyphState>(() => status.value?.glyph ?? (props.state === "done" && finishedHere.value ? "done" : "idle"));
const avatarGlyph = computed<AiGlyphState>(() => status.value?.glyph ?? (props.state === "done" && finishedHere.value ? "done" : "idle"));

function highlight(number: number | null) {
  highlighted.value = number;
  emit("highlight", number);
}

// --- Motion ---------------------------------------------------------------------------------------------------------
const timers = new Set<number>();
let disposed = false;
const pause = (ms: number) => new Promise<void>((resolve) => {
  const timer = window.setTimeout(() => { timers.delete(timer); resolve(); }, ms);
  timers.add(timer);
});
onBeforeUnmount(() => { disposed = true; gatherEpoch++; textHold?.cancel(); for (const timer of timers) window.clearTimeout(timer); timers.clear(); });

function chip(number: number) {
  return root.value?.querySelector<HTMLElement>(`[data-source-chip="${number}"]`) ?? null;
}

/** The ghost is the chip itself, so it carries the chip's own scoped styles. */
function ghostOf(element: HTMLElement) {
  const copy = element.cloneNode(true) as HTMLElement;
  copy.style.visibility = "";
  copy.style.boxShadow = "var(--shadow-menu)";
  copy.removeAttribute("aria-label");
  copy.removeAttribute("href");
  return copy.outerHTML;
}

/** The sources gather in the tray: each flies from its result row, 260 ms after the one before it. */
async function gather(sources: readonly AiSourceView[]) {
  const el = root.value;
  if (!el) return;
  const mine = ++gatherEpoch;
  const flights: Animation[] = [];
  const flies = !!props.sourceOrigin && !prefersReducedMotion(el) && typeof el.animate === "function";
  if (!flies) {
    riseIn(sources.map((source) => chip(source.number)).filter((c): c is HTMLElement => !!c));
    return;
  }
  hiddenNumbers.value = sources.map((source) => source.number);
  await nextTick();
  for (const [index, source] of sources.entries()) {
    if (index > 0) await pause(motionMs(el, "--atlas-ai-gather") / 2);
    if (disposed || mine !== gatherEpoch) return;
    const target = chip(source.number);
    const from = props.sourceOrigin?.(source.number);
    hiddenNumbers.value = hiddenNumbers.value.filter((n) => n !== source.number);
    await nextTick();
    const landed = chip(source.number) ?? target;
    if (landed && from) { const flight = flyGhost(from, landed, ghostOf(landed)); if (flight) flights.push(flight); }
    else if (landed) riseIn([landed]);
  }
  await Promise.all(flights.map(flight => flight.finished.catch(() => {})));
  if (flights.length && !disposed && mine === gatherEpoch) await pause(motionMs(el, "--atlas-motion-base"));
}

watch(() => props.sources.length > 0 && (props.state === "retrieving" || props.state === "preparing"), (arrived, was) => {
  if (arrived && !was) gathering = gather(props.sources);
  else if (!arrived && props.state !== "done") { gatherEpoch++; hiddenNumbers.value = []; }
}, { flush: "post" });

watch(() => props.state, (next, previous) => {
  finishedHere.value = next === "done" && (previous === "retrieving" || previous === "preparing");
  const el = root.value;
  if (!el) return;
  const before = el.offsetHeight;
  textHold?.cancel(); textHold = null;
  void nextTick(async () => {
    if (disposed || !root.value) return;
    if (previous === "offer" && next !== "offer" && outline.value) solidify(outline.value);
    if (hasCard.value) {
      const after = root.value.offsetHeight;
      if (before !== after) animateAtlas(root.value, [{ height: `${before}px` }, { height: `${after}px` }], { duration: motionMs(root.value, "--atlas-motion-base"), fill: "none" });
    }
    if (next === "done" && previous !== "done") {
      announcement.value = t.answer.ready;
      const body = text.value;
      if (body && !prefersReducedMotion(el)) {
        const held = animateAtlas(body, [{ opacity: 0 }, { opacity: 0 }], { duration: motionMs(el, "--atlas-ai-gather"), fill: "forwards" });
        textHold = held;
        await gathering;
        held?.cancel(); if (textHold === held) textHold = null;
      }
      if (!disposed && props.state === "done" && body === text.value && body) revealSentences(body);
    } else if (next !== "done") announcement.value = "";
  });
});

onMounted(() => {
  if (props.state === "offer" && root.value && hasCard.value) revealHeight(root.value);
  if (props.sources.length && (props.state === "retrieving" || props.state === "preparing")) gathering = gather(props.sources);
});
</script>

<template>
  <section
    ref="root"
    class="ai-answer"
    :class="{ 'is-card': hasCard, 'is-solid': hasCard && state !== 'offer', 'has-avatar': variant === 'full' && hasHeader }"
    :data-state="state"
    :data-variant="variant"
    data-ai-answer
  >
    <svg v-if="hasCard" class="atlas-suggestion-outline" aria-hidden="true"><rect ref="outline" x=".5" y=".5" :rx="outlineRadius" :ry="outlineRadius" /></svg>
    <span class="visually-hidden" role="status" aria-live="polite" data-ai-live>{{ announcement }}</span>

    <span v-if="variant === 'full' && hasHeader" class="ai-answer-avatar" aria-hidden="true" data-ai-avatar><AiGlyph :state="avatarGlyph" :size="22" /></span>

    <div class="ai-answer-main">
      <component :is="heading" v-if="hasHeader" class="ai-answer-title" :class="{ 'visually-hidden': variant === 'full' }">
        <template v-if="variant === 'compact'"><AiGlyph :state="headGlyph" :size="16" /><b>{{ title }}</b><span class="ai-answer-tag">AI</span></template>
        <template v-else>{{ title }}</template>
      </component>

      <div v-if="state === 'offer'" class="ai-answer-offer" data-ai-offer><slot name="offer" /></div>

      <template v-else-if="shown">
        <AiStatus
          v-if="working && status && variant === 'compact'"
          size="s"
          :glyph="false"
          :state="status.glyph"
          :label="status.label"
          :meta="status.meta"
          elapsed
          progress
          stoppable
          :data-ai-status="state"
          @stop="emit('stop')"
        >
          <span class="ai-answer-skeleton" aria-hidden="true"><i /><i /></span>
          <AiSourceList
            v-if="sources.length"
            class="ai-answer-tray"
            :sources="sources"
            :highlighted="highlighted"
            :hidden-numbers="hiddenNumbers"
            @highlight="highlight"
            @open-source="(source, newTab) => emit('open-source', source, newTab)"
          />
        </AiStatus>
        <AiStatus
          v-else-if="working && status"
          size="m"
          :state="status.glyph"
          :label="status.label"
          :meta="status.meta"
          :steps="status.steps"
          elapsed
          :note="t.status.note"
          stoppable
          class="ai-answer-bubble"
          :data-ai-status="state"
          @stop="emit('stop')"
        >
          <AiSourceList
            v-if="sources.length"
            class="ai-answer-tray"
            :sources="sources"
            :highlighted="highlighted"
            :hidden-numbers="hiddenNumbers"
            @highlight="highlight"
            @open-source="(source, newTab) => emit('open-source', source, newTab)"
          />
        </AiStatus>
        <div v-else class="ai-answer-body" data-ai-body>
          <component :is="format === 'bullets' ? 'ul' : 'div'" ref="text" class="ai-answer-sentences" :class="{ 'is-bullets': format === 'bullets' }">
          <component :is="format === 'bullets' ? 'li' : 'span'" v-for="(row, index) in rows" :key="index" class="ai-sentence" :class="{ 'is-hl': highlighted !== null && row.citations.some((c) => c.number === highlighted) }" data-ai-sentence>
            <template v-for="(token, t) in row.tokens" :key="t"><code v-if="token.kind === 'code'">{{ token.text }}</code><template v-else>{{ token.text }}</template></template>
            <span :class="{ 'ai-sentence-tail': row.tail.length }"><template v-for="(token, t) in row.tail" :key="t"><code v-if="token.kind === 'code'">{{ token.text }}</code><template v-else>{{ token.text }}</template></template><AiCitation
              v-for="source in row.citations"
              :key="source.number"
              :source="source"
              :highlighted="highlighted === source.number"
              @highlight="highlight"
              @open-source="(s, newTab) => emit('open-source', s, newTab)"
              @ask-again="emit('ask-again')"
            /></span>
          </component>
          </component>
          <p v-if="truncated" class="ai-answer-notice" role="status" data-ai-truncated>{{ t.answer.truncated }}</p>
          <div v-if="agentSteps.length" class="ai-answer-fold ai-answer-steps" data-ai-steps-fold>
            <button type="button" class="ai-answer-fold-toggle" :aria-expanded="stepsOpen" :aria-controls="stepsId" @click="stepsOpen = !stepsOpen">{{ t.answer.stepsToggle(agentSteps.length) }}</button>
            <InlineReveal :id="stepsId" v-model:open="stepsOpen">
              <ol class="ai-answer-steps-list" :aria-label="t.answer.stepsUsed"><li v-for="step in agentSteps" :key="step.id">{{ step.label }}<template v-if="step.state === 'failed'">{{ t.answer.stepNoResult }}</template></li></ol>
            </InlineReveal>
          </div>
          <details v-if="sources.length && collapsibleSources && !provenance" class="ai-answer-fold" data-ai-sources-fold>
            <summary>{{ t.answer.sourcesToggle(listed.length) }}</summary>
            <AiSourceList
              class="ai-answer-tray"
              :sources="listed"
              :highlighted="highlighted"
              @highlight="highlight"
              @open-source="(source, newTab) => emit('open-source', source, newTab)"
            />
          </details>
          <div v-else-if="sources.length && !(collapsibleSources && provenance)" class="ai-answer-sources">
            <span class="ai-answer-sources-label" aria-hidden="true">{{ t.answer.sourcesLabel }}</span>
            <AiSourceList
              class="ai-answer-tray"
              :sources="listed"
              :highlighted="highlighted"
              :hidden-numbers="hiddenNumbers"
              @highlight="highlight"
              @open-source="(source, newTab) => emit('open-source', source, newTab)"
            />
          </div>
          <AiProvenance @open-source="(source, newTab) => emit('open-source', source, newTab)" v-if="provenance" :provenance="provenance" :sources="sources" :created-at="createdAt" :scope="scope" :scope-name="scopeName">
            <template #trailing><AiFeedback :rating="rating" @rate="(r) => emit('rate', r)" @feedback="(f) => emit('feedback', f)" /></template>
          </AiProvenance>
          <div v-else class="ai-answer-rate"><AiFeedback :rating="rating" @rate="(r) => emit('rate', r)" @feedback="(f) => emit('feedback', f)" /></div>
          <p class="ai-answer-notice">{{ t.answer.notice }}</p>
          <div v-if="$slots.actions" class="ai-answer-actions"><slot name="actions" /></div>
        </div>
      </template>

      <EmptyState v-else-if="state === 'no_sources'" compact icon="search" :title="t.answer.noSources">
        <!-- The `retry` slot offers wider or deeper searches before „Zkusit jiný dotaz“; they slide in one after another. -->
        <div class="ai-answer-retry atlas-stagger" data-ai-retry>
          <slot name="retry" />
          <Button type="button" variant="outline" size="sm" data-ai-try-other @click="emit('try-other')">{{ t.answer.tryOther }}</Button>
        </div>
      </EmptyState>
      <ErrorState v-else-if="state === 'error'" :title="t.answer.failed" @retry="emit('retry')" />
      <p v-else-if="state === 'unavailable'" class="ai-answer-plain" role="status" data-ai-unavailable>{{ unavailableText }}</p>
      <p v-else-if="state === 'limit'" class="ai-answer-plain" role="status" data-ai-limit>{{ limitText }}</p>
      <p v-else-if="state === 'withheld'" class="ai-answer-plain" role="status" data-ai-withheld>{{ t.answer.withheld }}</p>
    </div>
  </section>
</template>

<style scoped>
.ai-answer { position: relative; display: grid; gap: 10px; min-width: 0; color: var(--text); font-size: 14px; line-height: 1.6; }
.ai-answer-main { display: grid; gap: 10px; min-width: 0; }
/* The assistant: the avatar glyph beside the message, as on the board „Asistent – vstupy a průběh“. */
.ai-answer.has-avatar { grid-template-columns: 32px minmax(0, 1fr); align-items: start; column-gap: 10px; }
.ai-answer-avatar { display: grid; place-items: center; width: 32px; height: 32px; border-radius: 10px; background: var(--ai-wash-strong); }
.ai-answer-bubble { padding: 10px 12px; border: 1px solid var(--refresh-soft-line, var(--line)); border-radius: 12px; background: var(--surface); }
.ai-answer-bubble :deep(.ai-answer-tray) { padding-top: 8px; border-top: 1px solid var(--refresh-soft-line, var(--line)); }
.ai-answer-sentences.is-bullets { margin: 0; padding-left: 20px; }
.ai-answer-sentences.is-bullets .ai-sentence { margin: 4px 0; }
.ai-answer.is-card { padding: 14px 16px; border-radius: 12px; background: var(--surface); overflow: hidden; }
.ai-answer.is-solid { background: linear-gradient(180deg, var(--ai-wash), var(--surface) 60%); }
/* Once it runs, the outline is the joined line solidify() ends on. */
.ai-answer.is-solid .atlas-suggestion-outline rect { stroke-dasharray: 11 0; stroke: var(--line); }
.ai-answer-title { display: flex; align-items: center; gap: 8px; margin: 0; font-size: 14px; font-weight: 400; line-height: 1.4; }
.ai-answer-title b { font-weight: 650; }
.ai-answer-tag { padding: 0 5px; border: 1px solid var(--ai-line); border-radius: 4px; color: var(--ai-ink); font-size: 10.5px; font-weight: 650; line-height: 16px; }
.ai-answer-offer { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; color: var(--muted); font-size: 13px; }
.ai-answer-skeleton { display: grid; gap: 8px; padding: 2px 0; }
.ai-answer-skeleton > i { display: block; height: 6px; border-radius: 3px; background: color-mix(in srgb, var(--line) 80%, transparent); }
.ai-answer-skeleton > i:first-child { width: 92%; }
.ai-answer-skeleton > i:last-child { width: 70%; }
.ai-answer-tray { min-height: 0; }
.ai-answer-sources { display: grid; gap: 6px; margin-top: 12px; }
/* A card lists its sources as rows (board „Hledání (Ctrl K)“); the assistant keeps them as chips in a row. */
.ai-answer[data-variant="compact"] .ai-answer-sources :deep(.ai-sources) { flex-direction: column; gap: 4px; }
.ai-answer[data-variant="compact"] .ai-answer-sources :deep(.ai-chip) { width: 100%; min-height: 32px; padding: 6px 9px; border-radius: 8px; background: var(--surface); font-size: 13px; }
.ai-answer-sources-label { color: var(--muted); font-size: 11px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.ai-answer-fold { margin-top: 8px; color: var(--muted); font-size: 12.5px; }
.ai-answer-fold summary { width: fit-content; cursor: pointer; }
.ai-answer-fold summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.ai-answer-fold[open] summary { margin-bottom: 6px; }
.ai-answer-fold-toggle { width: fit-content; padding: 0; border: 0; background: none; color: inherit; font: inherit; cursor: pointer; }
.ai-answer-fold-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.ai-answer-steps-list { margin: 6px 0 0; padding-left: 18px; }
.ai-answer-body { display: block; max-width: 68ch; }
.ai-answer[data-variant="full"] .ai-answer-body { padding-top: 4px; }
.ai-sentence { margin-right: .3em; border-radius: 4px; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.ai-sentence.is-hl { background: var(--accent-soft); box-shadow: 0 0 0 2px var(--accent-soft); }
.ai-sentence-tail { white-space: nowrap; }
.ai-sentence code { padding: 1px 4px; border-radius: 4px; background: var(--hover); font-size: .92em; }
.ai-answer-notice { margin: 4px 0 0; color: var(--muted); font-size: 11.5px; line-height: 1.5; }
.ai-answer-rate { display: flex; justify-content: flex-end; margin-top: 8px; }
.ai-answer-actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
.ai-answer-plain { margin: 0; color: var(--muted); font-size: 13px; }
/* The offers of an answer without sources: in a row that wraps, each sliding in a beat after the one before. */
.ai-answer-retry { display: flex; flex-wrap: wrap; justify-content: center; gap: 8px; --st-stagger: 90ms; }
</style>
