<script setup lang="ts">
import { computed, onUnmounted, ref, useAttrs, watch } from "vue";
import t from "../i18n/components-a";
// The field's own attributes and listeners (maxlength, @blur, @keydown, aria-*) belong to the textarea, not to the row holding the icon.
// `class` and `style` style the root (the h1 when read-only, the row holding the icon when writing), so a caller styles one element in both modes.
defineOptions({ inheritAttrs: false });
const attrs = useAttrs();
const rootAttrs = computed(() => ({ class: attrs.class, style: attrs.style }));
const fieldAttrs = computed(() => { const { class: _class, style: _style, ...rest } = attrs; return rest; });
import type { PageDecoration } from '../pageDecorations';
import PageIcon from './PageIcon.vue';

// `label`: the field's name where the title is not a page's (a board's, „Název tabule“).
const props = withDefaults(defineProps<{ modelValue: string; readonly?: boolean; decoration?: PageDecoration | null; label?: string;
  /** Where a navigation lands on a writer's view (true); a view with its own landing (a board's canvas) says false. */
  landing?: boolean }>(), { label: () => t.documentTitle.pageName, landing: true });
// `continue`: Enter, or Down at the end, hands the caret on to the text below (keyboard pass).
const emit = defineEmits<{ "update:modelValue": [value: string]; continue: [] }>();
const textarea = ref<HTMLTextAreaElement>();
const composing = ref(false);
let observer: ResizeObserver | undefined;
// The field grows with its text by CSS (`field-sizing`); the script measures only where that is not supported: height auto, then
// scrollHeight forced a layout of the page as it opened (~100 ms at 4× CPU).
const sizedByCss = typeof CSS !== "undefined" && !!CSS.supports?.("field-sizing", "content");

function resizeTitle() {
  const element = textarea.value;
  if (!element || sizedByCss) return;
  element.style.height = "auto";
  element.style.height = `${element.scrollHeight}px`;
}

watch(textarea, (element) => {
  observer?.disconnect();
  if (!element || sizedByCss) return;
  let width: number | undefined;
  observer = new ResizeObserver(([entry]) => {
    if (entry && entry.contentRect.width !== width) {
      width = entry.contentRect.width;
      // Not inside the callback: the height set here is a size change of the observed element itself ("ResizeObserver loop completed").
      requestAnimationFrame(resizeTitle);
    }
  });
  observer.observe(element);
}, { flush: "post" });
watch([textarea, () => props.modelValue], resizeTitle, { flush: "post" });
onUnmounted(() => observer?.disconnect());

function input(event: Event) {
  if (composing.value || (event as InputEvent).isComposing) return;
  const element = event.target as HTMLTextAreaElement;
  const original = element.value;
  const normalized = original.replace(/[\r\n]+/g, " ");
  if (normalized !== original) {
    const start = original.slice(0, element.selectionStart).replace(/[\r\n]+/g, " ").length;
    const end = original.slice(0, element.selectionEnd).replace(/[\r\n]+/g, " ").length;
    const direction = element.selectionDirection;
    element.value = normalized;
    element.setSelectionRange(start, end, direction);
  }
  emit("update:modelValue", normalized);
  resizeTitle();
}

function keydown(event: KeyboardEvent) {
  if (event.isComposing || composing.value || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) {
    if (event.key === "Enter" && !event.isComposing && !composing.value) event.preventDefault();
    return;
  }
  const field = event.target as HTMLTextAreaElement;
  if (event.key === "Enter" || (event.key === "ArrowDown" && field.selectionStart === field.value.length)) {
    event.preventDefault();
    emit("continue");
  }
}

function compositionEnd(event: CompositionEvent) {
  composing.value = false;
  input(event);
}
</script>
<template>
  <h1 v-if="readonly" class="document-title" v-bind="$attrs"><PageIcon :decoration="decoration" :size="26" />{{ modelValue }}</h1>
  <!-- The field keeps the heading's geometry (3.6-A): the icon before the text and the same box, so locking or unlocking moves no text. -->
  <!-- A writer's page has no h1 to land on after a navigation (keyboard/navigation.ts landFocus): the field itself is the place, the
       caret at its end, so typing goes on with the title and Enter into the text (F4: the box held focus and keys did nothing).
       A page with a title and text puts the caret into its text instead (App.vue openPage), so a slip never renames the page. -->
  <div v-else class="document-title document-title-input" v-bind="rootAttrs"><PageIcon :decoration="decoration" :size="26" /><textarea
    v-bind="fieldAttrs"
    ref="textarea"
    class="document-title-text"
    :data-focus-landing="landing || undefined"
    :value="modelValue"
    rows="1"
    :aria-label="label"
    @input="input"
    @keydown="keydown"
    @compositionstart="composing = true"
    @compositionend="compositionEnd"
  /></div>
</template>

<style scoped>
.document-title {
  box-sizing: border-box;
  width: 100%;
  min-width: 0;
  margin: 0;
  padding: 0.15em 0;
  font: inherit;
  font-size: 2rem;
  font-weight: 700;
  line-height: 1.25;
  color: inherit;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}
.document-title-input { display: flex; align-items: baseline; }
.document-title-input > :deep(.page-decoration-icon) { align-self: center; }
.document-title-text {
  display: block;
  flex: 1;
  min-width: 0;
  margin: 0;
  padding: 0;
  font: inherit;
  letter-spacing: inherit;
  color: inherit;
  resize: none;
  field-sizing: content;
  overflow: hidden;
  border: 0;
  border-radius: var(--radius-sm);
  background: transparent;
}
.document-title-text:focus-visible {
  outline: var(--focus-ring);
  outline-offset: var(--focus-offset);
}
@media (max-width: 640px) {
  .document-title { font-size: 1.75rem; }
}
</style>
