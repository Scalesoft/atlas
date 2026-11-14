<script setup lang="ts">
import { ref, watch } from "vue";
import type { AiFeedbackReason } from "../../ai/assistantApi";
import { animateAtlas, motionMs } from "../../motion/aiMotion";
import t from "../../i18n/ai-answer";
import { Button } from "../ui/button";
import { Popover, PopoverAnchor, PopoverContent } from "../ui/popover";
import { Textarea } from "../ui/textarea";

/**
 * Thumbs for one model output (UX design §5.5), the same for answers, summaries and edits.
 * - Thumbs up is one click and only changes the button (`rate`); there is no thank-you.
 * - Thumbs down opens an anchored menu without a title or a close button: the reasons, an optional note (up to 500
 *   characters) and „Odeslat“, which sends `feedback` with the reason and note.
 * The parent saves; the `rating` prop only restores the state of a rating that was saved before.
 * Pasive suggestions have no thumbs (their feedback is Použít / Zahodit), so use this only under model output.
 */
const props = withDefaults(defineProps<{ rating?: "up" | "down" | null }>(), { rating: null });
const emit = defineEmits<{
  rate: [rating: "up" | "down"];
  feedback: [feedback: { rating: "down"; reason: AiFeedbackReason | null; note: string | null }];
}>();

const reasons: AiFeedbackReason[] = ["inaccurate", "missing_info", "wrong_sources", "unsuitable", "other"];
const NOTE_MAX = 500;

const current = ref<"up" | "down" | null>(props.rating);
watch(() => props.rating, (rating) => { current.value = rating; });
const menuOpen = ref(false);
const reason = ref<AiFeedbackReason | null>(null);
const note = ref("");
const sent = ref(false);

/** A new rating is confirmed where the eye is: the pressed thumb settles in once (the pop of a new status, `--atlas-motion-pop`).
 *  A rating restored from the server, or pressed again, is not news and does not move. */
function confirm(event: Event) {
  const thumb = event.currentTarget instanceof Element ? event.currentTarget.querySelector<SVGElement>("svg") : null;
  if (thumb) animateAtlas(thumb, [{ transform: "scale(.8)" }, { transform: "scale(1.12)", offset: 0.55 }, { transform: "none" }], { duration: motionMs(thumb, "--atlas-motion-pop") });
}
function up(event: Event) {
  menuOpen.value = false;
  if (current.value === "up") return;
  current.value = "up";
  confirm(event);
  emit("rate", "up");
}
function down(event: Event) {
  if (menuOpen.value) { menuOpen.value = false; return; }
  if (current.value !== "down") { current.value = "down"; confirm(event); emit("rate", "down"); }
  sent.value = false;
  menuOpen.value = true;
}
function send() {
  emit("feedback", { rating: "down", reason: reason.value, note: note.value.trim() ? note.value.trim().slice(0, NOTE_MAX) : null });
  sent.value = true;
  menuOpen.value = false;
}
function choose(code: AiFeedbackReason) {
  reason.value = code;
}
/** The thumbs-down button toggles the menu itself; the outside-click that precedes its click must not close it first. */
function keepOpenOnThumbs(event: Event) {
  const target = (event as CustomEvent<{ originalEvent?: Event }>).detail?.originalEvent?.target ?? event.target;
  if (target instanceof Element && target.closest("[data-ai-feedback-thumbs]")) event.preventDefault();
}
</script>

<template>
  <Popover v-model:open="menuOpen">
    <PopoverAnchor as-child>
      <span class="ai-feedback" role="group" :aria-label="t.feedback.group" data-ai-feedback data-ai-feedback-thumbs>
        <button type="button" class="ai-feedback-button" data-ai-feedback-up :aria-pressed="current === 'up'" :aria-label="t.feedback.up" @click="up">
          <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M7 10v12" /><path d="M15 5.88 14 10h5.83a2 2 0 0 1 1.92 2.56l-2.33 8A2 2 0 0 1 17.5 22H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2.76a2 2 0 0 0 1.79-1.11L12 2a3.13 3.13 0 0 1 3 3.88Z" /></svg>
        </button>
        <button type="button" class="ai-feedback-button" data-ai-feedback-down :aria-pressed="current === 'down'" :aria-expanded="menuOpen" :aria-label="t.feedback.down" @click="down">
          <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M17 14V2" /><path d="M9 18.12 10 14H4.17a2 2 0 0 1-1.92-2.56l2.33-8A2 2 0 0 1 6.5 2H20a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-2.76a2 2 0 0 0-1.79 1.11L12 22a3.13 3.13 0 0 1-3-3.88Z" /></svg>
        </button>
        <span v-if="sent" class="visually-hidden" role="status">{{ t.feedback.sent }}</span>
      </span>
    </PopoverAnchor>
    <PopoverContent class="ai-feedback-menu" align="end" :aria-label="t.feedback.menu" data-ai-feedback-menu @interact-outside="keepOpenOnThumbs">
      <div class="ai-feedback-reasons" role="radiogroup" :aria-label="t.feedback.reason">
        <button v-for="code in reasons" :key="code" type="button" role="radio" class="ai-feedback-reason" :aria-checked="reason === code" :data-reason="code" @click="choose(code)">{{ t.feedback.reasons[code] }}</button>
      </div>
      <Textarea v-model="note" class="ai-feedback-note" rows="2" :maxlength="NOTE_MAX" :aria-label="t.feedback.note" :placeholder="t.feedback.note" data-ai-feedback-note />
      <div class="ai-feedback-actions"><Button type="button" size="sm" data-ai-feedback-send @click="send">{{ t.feedback.send }}</Button></div>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.ai-feedback { display: inline-flex; gap: 2px; }
.ai-feedback-button { display: inline-grid; place-items: center; width: 26px; height: 26px; border: 0; border-radius: 6px; background: none; color: var(--muted); cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .ai-feedback-button:hover { background: var(--hover); color: var(--text); } }
.ai-feedback-button[aria-pressed="true"] { background: var(--accent-soft); color: var(--accent); }
.ai-feedback-button:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
</style>

<style>
.ai-feedback-menu { display: grid; gap: 8px; width: min(280px, calc(100vw - 2rem)); padding: 8px; font-size: 13px; }
.ai-feedback-reasons { display: grid; gap: 2px; }
.ai-feedback-reason { display: flex; align-items: center; width: 100%; padding: 6px 8px; border: 0; border-radius: 6px; background: none; color: var(--text); font-size: 13px; text-align: left; cursor: pointer; }
@media (hover: hover) and (pointer: fine) { .ai-feedback-reason:hover { background: var(--hover); } }
.ai-feedback-reason[aria-checked="true"] { background: var(--accent-soft); font-weight: 600; }
.ai-feedback-reason:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.ai-feedback-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 6px; }
</style>
