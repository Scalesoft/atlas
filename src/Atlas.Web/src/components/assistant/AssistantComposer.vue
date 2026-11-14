<script setup lang="ts">
import { onBeforeUnmount, ref, useId, watch } from "vue";
import { animateAtlas, motionMs, paperPlaneFlight, PAPER_PLANE_ICON_PATHS } from "../../motion/aiMotion";
import t from "../../i18n/assistant-panel";
import { Textarea } from "../ui/textarea";
import { disclosureHooks } from "../settings/disclosure";

/**
 * The field of the next question (plan §15; board „Asistent – vstupy a průběh“): one rounded field that grows with the text,
 * the send button in the AI colours (a paper plane that leaves with the question). Enter sends,
 * Shift+Enter breaks the line, ArrowUp in the empty field brings back the previous question. The first Escape belongs to the
 * panel (it stops a running answer before it closes anything). The draft is owned by the parent (memory only). A question with
 * a detected password is refused by the parent, which then shows `notice`; the text stays in the field. `maxlength` leaves room
 * for a selection attached to the question. With the `actions` slot (the answer effort picker) the text takes the whole first line and the actions sit with the send
 * button on a second one, as in a chat app; without it the field stays one line.
 */
const props = withDefaults(defineProps<{ modelValue: string; busy?: boolean; notice?: string; recall?: readonly string[]; maxlength?: number; placeholder?: string }>(), {
  busy: false, notice: "", recall: () => [], maxlength: 2000, placeholder: () => t.composer.placeholder,
});
const emit = defineEmits<{ "update:modelValue": [value: string]; send: [] }>();

const field = ref<HTMLTextAreaElement | { $el: HTMLTextAreaElement }>();
let recallIndex = -1;
const hintId = useId();
const plane = ref<SVGSVGElement>();
const away = ref(false);
let flight: Animation | null = null;
let returning = 0;
/** A fresh plane glides into the empty button from below left once the answer is ready (and the old one has flown off). */
function planeBack() {
  away.value = false;
  animateAtlas(plane.value, [
    { opacity: 0, transform: "translate(-7px, 7px) scale(.5)" },
    { opacity: 1, transform: "translate(1px, -1px) scale(1.08)", offset: 0.65 },
    { opacity: 1, transform: "none" },
  ], { duration: plane.value ? motionMs(plane.value, "--atlas-motion-pop") : undefined });
}
// A question that really left (the parent turned busy, not a refused one): the plane of the button takes off with it and the
// button stays empty while the answer is prepared. Under reduced motion nothing flies and the icon simply stays.
watch(() => props.busy, async (busy) => {
  const current = ++returning;
  if (busy) {
    flight?.cancel();
    flight = plane.value ? paperPlaneFlight(plane.value) : null;
    away.value = !!flight;
    return;
  }
  if (!away.value) return;
  await flight?.finished.catch(() => {});
  if (current === returning && !props.busy) planeBack();
});
onBeforeUnmount(() => { returning++; flight?.cancel(); });

function element(): HTMLTextAreaElement | undefined {
  const value = field.value;
  return value instanceof HTMLTextAreaElement ? value : value?.$el;
}
defineExpose({
  focus: (end = false) => {
    const el = element();
    el?.focus({ preventScroll: true });
    if (end && el) el.setSelectionRange(el.value.length, el.value.length);
  },
});

function keydown(event: KeyboardEvent) {
  if (event.isComposing) return;
  if (event.key === "Enter" && !event.shiftKey && !event.ctrlKey && !event.metaKey && !event.altKey) {
    event.preventDefault();
    send();
    return;
  }
  if (event.key === "ArrowUp" && !event.shiftKey && props.recall?.length && (!props.modelValue.trim() || recallIndex >= 0)) {
    event.preventDefault();
    recallIndex = recallIndex < 0 ? props.recall.length - 1 : Math.max(0, recallIndex - 1);
    emit("update:modelValue", props.recall[recallIndex]!);
  }
}
function send() {
  if (props.busy || !props.modelValue.trim()) return;
  recallIndex = -1;
  emit("send");
}
</script>

<template>
  <form class="assistant-composer" data-assistant-composer @submit.prevent="send">
    <div class="assistant-input" :class="{ 'has-actions': !!$slots.actions }">
      <Textarea
        ref="field"
        :model-value="modelValue"
        rows="1"
        :maxlength="maxlength"
        data-autofocus
        :aria-label="t.composer.label"
        :placeholder="placeholder"
        :aria-invalid="notice ? true : undefined"
        :aria-describedby="hintId"
        @update:model-value="(value) => { recallIndex = -1; emit('update:modelValue', String(value)); }"
        @keydown="keydown"
      />
      <div v-if="$slots.actions" class="assistant-actions"><slot name="actions" /></div>
      <button class="assistant-send" type="submit" :aria-label="t.composer.send" :title="t.composer.sendTitle" :disabled="busy || !modelValue.trim()">
        <svg ref="plane" class="assistant-send-plane" :class="{ 'is-away': away }" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path v-for="d in PAPER_PLANE_ICON_PATHS" :key="d" :d="d" /></svg>
      </button>
    </div>
    <Transition :css="false" v-bind="disclosureHooks"><p v-if="notice" class="assistant-composer-note" role="alert" data-assistant-notice>{{ notice }}</p></Transition>
    <!-- Read with the field, never shown: under the field it was a second line of small print in every conversation. What the
         assistant may read and Ctrl J are said once, in the greeting of an empty conversation (AssistantPanel). -->
    <small :id="hintId" class="visually-hidden">{{ t.composer.hint }}</small>
  </form>
</template>

<style scoped>
/* A column of flex items, not a grid: the notice unfolds and gives the gap above it back (settings/disclosure.ts). */
.assistant-composer { display: flex; flex-direction: column; gap: 6px; }
.assistant-input { display: flex; align-items: flex-end; gap: 8px; min-height: 48px; padding: 7px 7px 7px 12px; border: 1px solid var(--input-line); border-radius: 16px; background: var(--surface); transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.assistant-input.has-actions { flex-wrap: wrap; row-gap: 2px; }
.assistant-actions { display: flex; align-items: center; margin-left: auto; }
.assistant-input:focus-within { border-color: var(--ai-line-strong); box-shadow: 0 0 0 3px var(--ai-wash-strong); }
/* :not() pairs: the field lies inside the rounded input, over the dark-mode field fill of palettes.css. */
.assistant-composer .assistant-input textarea:not(.md-editor):not(.md-editor *) { flex: 1; min-width: 0; min-height: 32px; max-height: 9rem; field-sizing: content; resize: none; padding: 6px 0; border: 0; border-radius: 0; background: transparent; box-shadow: none; font-size: 14px; line-height: 1.45; }
.assistant-composer .assistant-input textarea:focus-visible:not(.md-editor):not(.md-editor *) { border: 0; box-shadow: none; outline: none; }
/* Same weight as the field rule above, which sets flex: 1. */
.assistant-composer .assistant-input.has-actions textarea:not(.md-editor):not(.md-editor *) { flex-basis: 100%; }
/* Concentric: the field's 16 px radius less its 1 px border and 7 px padding is the button's 8 px (it was 14 and 9, a corner gap of 8.2 px
   against 7 px along the edges). */
.assistant-send { display: grid; flex: none; place-items: center; width: 32px; height: 32px; padding: 0; overflow: visible; border: 0; border-radius: 8px; background: linear-gradient(135deg, var(--accent), var(--ai-ink)); color: var(--on-accent, #fff); cursor: pointer; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), filter var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .assistant-send:hover:not(:disabled) { filter: brightness(1.08); } }
.assistant-send:active:not(:disabled) { transform: scale(var(--atlas-press)); }
.assistant-send:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.assistant-send:disabled { opacity: .45; cursor: default; }
.assistant-send svg { pointer-events: none; }
/* The plane has flown off with the question (aiMotion paperPlaneFlight); the button stays empty until a new one glides in. */
.assistant-send-plane.is-away { opacity: 0; }
.assistant-composer-note { margin: 0; color: var(--error); font-size: 12.5px; }
</style>
