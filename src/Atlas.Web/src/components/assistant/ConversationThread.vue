<script setup lang="ts">
import type { AiSourceView } from "../../ai/assistantApi";
import type { AssistantContext, ThreadItem } from "../../assistant/useAssistant";
import type { Proposal } from "../../assistant/proposals";
import t from "../../i18n/assistant-panel";
import AiAnswer from "../ai/AiAnswer.vue";
import AssistantProposal from "./AssistantProposal.vue";
import { Button } from "../ui/button";
import { animateAtlas, literalMs, motionMs } from "../../motion/aiMotion";

/**
 * The messages of the Assistant (plan phase 3 §15): a question as plain text, an answer as `AiAnswer variant="full"` (finished
 * answers fold their sources into „Zdroje ▸ n“), and between them the divider that marks a change of scope. The thread draws
 * nothing of its own for the states of an answer; AiAnswer owns them. It is presentational: every action goes up. An answer
 * without sources offers the same question in the whole wiki (when it was asked in a page or space) and with a deeper effort
 * (`deeperLabel`, none at the deepest).
 */
defineProps<{ items: readonly ThreadItem[]; context: AssistantContext; deeperLabel?: string | null }>();
const emit = defineEmits<{
  stop: [];
  retry: [key: string];
  "ask-again": [question: string];
  "try-other": [question: string];
  widen: [question: string];
  deepen: [question: string];
  "open-source": [source: AiSourceView, newTab: boolean];
  proposal: [key: string, proposal: Proposal];
  "open-editor": [pageId: string];
  rate: [key: string, rating: "up" | "down"];
  feedback: [key: string, feedback: { rating: "down"; reason: string | null; note: string | null }];
}>();
/** How a message enters. A question lands from the composer below: it grows out of its own tail corner (bottom right) with a
 *  slight overshoot, the way the paper plane leaves the same corner. Answers and dividers rise like every entering content. */
function enter(element: Element, done: () => void, staggered = false) {
  const el = element as HTMLElement;
  const question = el.dataset.kind === "question" && !staggered;
  const index = staggered ? Number(el.dataset.index ?? 0) : 0;
  const animation = question
    ? animateAtlas(el.firstElementChild, [
      { opacity: 0, transform: "translateY(calc(var(--atlas-rise) * 1.5)) scale(.9)" },
      { opacity: 1, transform: "translateY(-1px) scale(1.015)", offset: 0.6 },
      { opacity: 1, transform: "none" },
    ], { duration: motionMs(el, "--atlas-motion-pop") })
    : animateAtlas(el, [{ opacity: 0, transform: "translateY(var(--atlas-rise))" }, { opacity: 1, offset: 0.6 }, { opacity: 1, transform: "none" }], {
      duration: motionMs(el, "--atlas-motion-slow"),
      // Reopening a conversation lets its messages arrive one after another (first eight), like every list.
      delay: Math.min(index, 7) * literalMs(el, 40),
    });
  if (animation) void animation.finished.then(done, done); else done();
}
const appear = (element: Element, done: () => void) => enter(element, done, true);
const scopeName = (item: Extract<ThreadItem, { kind: "answer" }>, context: AssistantContext) =>
  item.scope?.kind === "space" && item.scope.spaceId === context.spaceId ? context.spaceName : null;
</script>

<template>
  <TransitionGroup tag="ol" appear :css="false" class="assistant-thread" :aria-label="t.thread.label" data-assistant-thread @appear="appear" @enter="enter">
    <li v-for="(item, index) in items" :key="item.key" class="assistant-thread-item" :data-kind="item.kind" :data-index="index">
      <p v-if="item.kind === 'question'" class="assistant-question" data-assistant-question>{{ item.text }}</p>
      <div v-else-if="item.kind === 'scope'" class="assistant-scope-change" role="separator" data-assistant-scope-change><span>{{ item.text }}</span></div>
      <template v-else>
        <AiAnswer
          variant="full"
          :title="t.thread.answerTitle"
          :heading-level="3"
          :state="item.state"
          :sentences="item.sentences"
          :sources="item.sources"
          :provenance="item.provenance"
          :created-at="item.createdAt"
          :scope="item.scope"
          :scope-name="scopeName(item, context)"
          :rating="item.rating"
          :truncated="item.truncated"
          :agent-steps="item.steps"
          collapsible-sources
          :unavailable-text="item.text"
          :limit-text="item.text"
          @stop="emit('stop')"
          @retry="emit('retry', item.key)"
          @ask-again="emit('ask-again', item.question)"
          @try-other="emit('try-other', item.question)"
          @open-source="(source, newTab) => emit('open-source', source, newTab)"
          @rate="(rating) => emit('rate', item.key, rating)"
          @feedback="(feedback) => emit('feedback', item.key, feedback)"
        >
          <template #retry>
            <Button v-if="item.scope && item.scope.kind !== 'wiki'" type="button" size="sm" data-ai-widen @click="emit('widen', item.question)">{{ t.thread.widen }}</Button>
            <Button v-if="deeperLabel" type="button" variant="outline" size="sm" data-ai-deepen @click="emit('deepen', item.question)">{{ deeperLabel }}</Button>
          </template>
        </AiAnswer>
        <AssistantProposal v-if="item.proposal || item.proposalId" :proposal="item.proposal" :proposal-id="item.proposalId" @update="(proposal) => emit('proposal', item.key, proposal)" @open-editor="(pageId) => emit('open-editor', pageId)" />
      </template>
    </li>
  </TransitionGroup>
</template>

<style scoped>
.assistant-thread { display: grid; gap: 14px; margin: 0; padding: 0; list-style: none; }
/* A new question or answer enters through the TransitionGroup hooks above; nothing is kept after it. */
.assistant-question { margin: 0; justify-self: end; transform-origin: 100% 100%; max-width: 88%; padding: 9px 12px; border-radius: 12px 12px 4px 12px; background: var(--accent-soft); color: var(--text); font-size: 14px; line-height: 1.5; overflow-wrap: anywhere; white-space: pre-wrap; }
/* A long attached selection reads as a quote and stays short until it is opened by scrolling the bubble. */
.assistant-question { max-height: 14em; overflow: auto; }
.assistant-scope-change { display: flex; align-items: center; gap: 10px; color: var(--muted); font-size: 12px; }
.assistant-scope-change::before, .assistant-scope-change::after { content: ""; flex: 1; height: 1px; background: var(--line); }
</style>
