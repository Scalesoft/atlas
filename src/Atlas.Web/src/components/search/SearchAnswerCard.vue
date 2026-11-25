<script setup lang="ts">
import { nextTick, ref, watch } from "vue";
import type { useSearchAnswer } from "../../search/useSearchAnswer";
import type { AiFeedbackBody, AiSourceView } from "../../ai/assistantApi";
import { animateAtlas, motionMs } from "../../motion/aiMotion";
import AiAnswer from "../ai/AiAnswer.vue";
import AiGlyph from "../ai/AiGlyph.vue";
import { Button } from "../ui/button";
import componentsSearch from "../../i18n/components-search";
const props = defineProps<{ answer: ReturnType<typeof useSearchAnswer>; canContinue: boolean; sourceOrigin?: (number: number) => Element | null }>();
const emit = defineEmits<{ continue: []; highlight: [number: number | null]; "open-source": [source: AiSourceView, newTab: boolean]; "try-other": [] }>();
const root = ref<HTMLElement>();
defineExpose({ element: () => root.value });
// A finished answer lies on the pane (see the style below): the working card gives up its padding, border and fill, and the content
// that sat 16 px in and 14 px down moved to the pane's edge in one frame. It glides there in the base token instead; the height of
// the card is eased by AiAnswer itself.
watch(() => props.answer.state.value, async (state, previous) => {
  if (state !== "done" || previous === "done") return;
  const main = root.value?.querySelector<HTMLElement>(".ai-answer-main");
  if (!main) return;
  const before = main.getBoundingClientRect();
  await nextTick();
  const after = main.getBoundingClientRect();
  const dx = Math.round(before.x - after.x), dy = Math.round(before.y - after.y);
  if (dx || dy) animateAtlas(main, [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: "none" }], { duration: motionMs(main, "--atlas-motion-base") });
}, { flush: "pre" });
function submitFeedback(feedback: { rating: "down"; reason: string | null; note: string | null }) { void props.answer.feedback({ ...feedback, reason: feedback.reason as AiFeedbackBody["reason"] }); }
</script>
<template>
  <div v-if="answer.visible.value" ref="root" class="search-answer-card" data-search-answer>
    <AiAnswer :state="answer.state.value === 'canceled' ? 'offer' : answer.state.value" variant="compact" format="lead"
      :sources="answer.sources.value" :sentences="answer.message.value?.sentences" :provenance="answer.message.value?.provenance"
      :created-at="answer.message.value?.createdAt" :scope="answer.message.value?.scope" :rating="answer.rating.value"
      :source-origin="sourceOrigin"
      @stop="answer.stop()" @retry="answer.ask()" @ask-again="answer.ask()" @try-other="emit('try-other')"
      @highlight="emit('highlight', $event)" @open-source="(source, newTab) => emit('open-source', source, newTab)"
      @rate="rating => answer.feedback({ rating })" @feedback="submitFeedback">
      <template #offer>{{ answer.preference.value === 'auto' ? componentsSearch.searchAnswer.offerAuto : componentsSearch.searchAnswer.offerManual }}
        <Button type="button" variant="ghost" size="sm" data-search-ask @click="answer.ask()"><AiGlyph :size="15" state="ready" />{{ componentsSearch.searchAnswer.askAssistant }}</Button>
        <kbd>Ctrl + Enter</kbd>
      </template>
      <template v-if="canContinue && answer.conversationId.value" #actions>
        <Button type="button" variant="outline" size="sm" data-search-continue @click="emit('continue')">{{ componentsSearch.searchAnswer.continueAssistant }}</Button>
      </template>
    </AiAnswer>
    <p v-if="answer.notice.value && answer.state.value !== 'unavailable'" class="search-answer-notice" role="status">{{ answer.notice.value }}</p>
  </div>
</template>
<style scoped>
.search-answer-card { min-width: 0; margin-bottom: 14px; }
/* Finished, the answer lies on the pane itself (board „Hledání (Ctrl K)“); while it works it is the card of the status line. */
.search-answer-card :deep(.ai-answer.is-card[data-state="done"]) { padding: 2px 0 0; background: transparent; overflow: visible; }
.search-answer-card :deep(.ai-answer[data-state="done"] > .atlas-suggestion-outline) { display: none; }
.search-answer-card :deep([data-search-continue]) { width: 100%; height: 34px; border-radius: 10px; font-weight: 600; }
.search-answer-notice { margin: 8px 0 0; color: var(--muted); font-size: 13px; }
</style>
