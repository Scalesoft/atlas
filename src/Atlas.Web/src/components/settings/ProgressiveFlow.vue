<script setup lang="ts">
import shell from "../../i18n/settings-shell";
import t from "../../i18n/settings-flow";
import { computed, nextTick, onMounted, ref, useId, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { DisclosureGroup } from "./disclosure";
import FlowProgress from "./FlowProgress.vue";
import InlineReveal from "./InlineReveal.vue";
import type { FlowStep } from "./types";

// Field dependencies reveal themselves; only manual operations and confirmation need an action.
// `autofocus`: a flow opened by a button (Přidat přístup, Pozvat člověka…) starts with the caret in its first field
// (`[data-step-focus]`, else the first control of the current step), and Esc is its Zrušit (UX cycle 5, AD5-1).
const props = withDefaults(defineProps<{ steps: FlowStep[]; label: string; context?: string; busy?: boolean; cancelLabel?: string; cancelDisabled?: boolean; autofocus?: boolean }>(), { context: undefined, busy: false, cancelLabel: undefined, cancelDisabled: false, autofocus: false });
const current = defineModel<string>({ required: true });
const emit = defineEmits<{ next: [step: FlowStep]; cancel: [] }>();
const uid = useId();
const root = ref<HTMLElement>();
const revealed = ref(new Set<string>());
const final = computed(() => props.steps.find((item) => item.id === current.value && item.final));
const inputs = computed(() => props.steps.filter((item) => !item.final));
const passes = (item: FlowStep) => item.ready !== false && (!(item.manual || item.hideNext) || item.complete === true);
const boundary = computed(() => {
  const index = inputs.value.findIndex((item, index) => !passes(item) || index === inputs.value.length - 1);
  return Math.max(0, index);
});
const eligible = (index: number) => index <= boundary.value;
watch(() => [inputs.value, boundary.value, final.value] as const, () => {
  if (final.value) return;
  const ids = new Set(inputs.value.filter((item, index) => eligible(index) || revealed.value.has(item.id)).map((item) => item.id));
  revealed.value = ids;
  const active = inputs.value[boundary.value];
  if (active && current.value !== active.id) current.value = active.id;
}, { immediate: true });
const progress = computed(() => props.steps.map((item, index) => {
  const done = !!final.value ? item.id !== final.value.id : index < boundary.value;
  const active = final.value ? item.id === final.value.id : index === boundary.value;
  const action = item.manual || item.hideNext || index === inputs.value.length - 1;
  return {
    id: item.id,
    label: done && item.summary ? item.summary : item.label,
    // A filled step shows its value, captioned by the step's name: "VPN · Druh", not "VPN · Vyplněno".
    detail: final.value ? (active ? t.flow.completed : t.flow.done) : done ? (item.summary ? item.label : t.flow.filled) : active ? (action && item.ready !== false ? t.flow.readyToConfirm : t.flow.fillIn) : revealed.value.has(item.id) ? t.flow.waiting : t.flow.following,
    state: (done ? "done" : active ? "current" : "todo") as "done" | "current" | "todo",
    available: !final.value && revealed.value.has(item.id),
    controls: `${uid}-${item.id}`,
  };
}));
function goTo(id: string) {
  if (props.busy || final.value || !revealed.value.has(id)) return;
  const target = root.value?.querySelector<HTMLElement>(`[data-step="${id}"]`);
  target?.focus({ preventScroll: true });
  target?.scrollIntoView?.({ block: "nearest" });
}
function next(item: FlowStep, index: number) {
  if (props.busy || item.disabled || !eligible(index)) return;
  // Not ready yet: the button stays in the Tab order (aria-disabled) and leads to what is missing, its step's first invalid field (SH-11).
  if (item.ready === false) {
    root.value?.querySelector<HTMLElement>(`[data-step="${item.id}"] :is(input, textarea, select):invalid, [data-step="${item.id}"] [aria-invalid="true"]`)?.focus();
    return;
  }
  if (inputs.value.slice(0, index + 1).some((entry) => entry.validate?.() === false)) return;
  emit("next", item);
}
onMounted(() => {
  if (!props.autofocus) return;
  void nextTick(() => {
    // Never out of a field someone is typing in elsewhere (the gallery shows several flows at once).
    const active = document.activeElement;
    if (active?.isConnected && active.matches("input, select, textarea, [contenteditable='true']") && !root.value?.contains(active)) return;
    const section = root.value?.querySelector<HTMLElement>(`[data-step="${current.value}"]`);
    const target = section?.querySelector<HTMLElement>("[data-step-focus]")
      ?? section?.querySelector<HTMLElement>('.progressive-fields :is(input, select, textarea, button, [tabindex]):not(:disabled, [type="hidden"], [tabindex="-1"], [aria-hidden="true"])');
    target?.focus({ preventScroll: true });
  });
});
// A cancelled flow returns focus to what opened it (the host removes the flow, so it would fall to the page heading).
const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null;
function cancel() {
  emit("cancel");
  if (props.autofocus) void nextTick(() => { if (opener?.isConnected && !root.value?.contains(opener)) opener.focus({ preventScroll: true }); });
}
/** Esc is Zrušit, unless a field, list or picker inside used it first (they stop or prevent it), as in PageCreationFlow. */
function escape(event: KeyboardEvent) {
  if (!props.autofocus || event.defaultPrevented || event.isComposing || props.busy || props.cancelDisabled || final.value) return;
  event.preventDefault();
  cancel();
}
</script>

<template>
  <div ref="root" class="progressive-flow" :aria-label="label" :aria-busy="busy || undefined" @keydown.esc="escape">
    <p v-if="context" class="progressive-context">{{ context }}</p>
    <!-- One phase is a form with its button, not a stepper of one step (U2-14). -->
    <FlowProgress v-if="steps.length > 1" :items="progress" :busy="busy" @select="goTo" />
    <!-- The final step does not replace the form in one frame: the steps and their actions fold away (and the final step unfolds)
         with the disclosure of a field's error, so the page eases to its new height. The group renders no element: the grid
         and its margins stay as they are. It is not a TransitionGroup, which would measure every step on each keystroke. -->
    <DisclosureGroup>
    <template v-if="!final">
      <InlineReveal v-for="(item, index) in inputs" :key="item.id" :open="revealed.has(item.id)">
        <section :id="`${uid}-${item.id}`" :data-step="item.id" :aria-labelledby="`${uid}-${item.id}-title`" tabindex="-1" class="progressive-section" :class="{ 'is-confirmation': item.manual || item.hideNext || index === inputs.length - 1 }">
          <header class="progressive-heading">
            <span class="st-icon-tile"><AtlasIcon :name="item.icon ?? 'circle-help'" :size="20" /></span>
            <div><h3 :id="`${uid}-${item.id}-title`" data-step-heading>{{ item.title }}</h3><p v-if="item.hint">{{ item.hint }}</p></div>
          </header>
          <p v-if="!eligible(index)" class="progressive-incomplete" role="status"><AtlasIcon name="info" :size="15" />{{ t.flow.firstFill(inputs[boundary]?.label) }}</p>
          <fieldset :disabled="busy || item.disabled || !eligible(index)" :inert="busy || item.disabled || !eligible(index) ? true : undefined" class="progressive-fields">
            <legend class="st-sr-only">{{ item.title }}</legend>
            <slot :name="item.id" :step="item" :eligible="eligible(index)" />
          </fieldset>
          <Button v-if="!item.hideNext && !item.complete && (item.manual || index === inputs.length - 1)" type="button" :disabled="busy || item.disabled || !eligible(index)" :aria-disabled="item.ready === false || undefined" class="aria-disabled:cursor-not-allowed aria-disabled:opacity-50" data-flow-next @click="next(item, index)">{{ item.nextLabel ?? t.flow.save }}<AtlasIcon :name="item.nextIcon ?? 'check'" :size="16" /></Button>
        </section>
      </InlineReveal>
      <div key="actions" class="progressive-actions"><Button variant="ghost" type="button" :disabled="busy || cancelDisabled" data-flow-cancel @click="cancel"><AtlasIcon name="close" :size="16" />{{ cancelLabel ?? shell.cancel }}</Button><slot name="actions" :step="inputs[boundary]" :index="boundary" /></div>
    </template>
      <section v-else key="final" :data-step="final.id" class="progressive-section"><h3>{{ final.title }}</h3><slot :name="final.id" :step="final" :eligible="true" /></section>
    </DisclosureGroup>
    <slot v-if="final" name="actions" :step="final" :index="steps.indexOf(final)" />
  </div>
</template>

<style scoped>
/* The 18 px between the parts is a margin of what is shown, not a grid gap: a step that is still closed (an InlineReveal
   with no height) would otherwise reserve its gap and leave a hole under the open ones, and the space of a step that opens
   or closes would pop in instead of unfolding with it. */
.progressive-flow { display: grid; min-width: 0; }
.progressive-flow > :not(:first-child):not(.inline-reveal-root) { margin-top: 18px; }
.progressive-context { margin: 0; color: var(--muted); font-size: 12px; font-weight: 600; }
.progressive-section { display: grid; gap: 14px; margin-top: 18px; padding: 16px 0; border-top: 1px solid var(--line); scroll-margin-block: 24px; }
.progressive-section:focus-visible { outline: 2px solid var(--accent); outline-offset: 4px; --focus-offset: 4px; border-radius: var(--st-radius-sm); }
/* The band's inset is the card inset inside it: a card nested in its fields (an advanced section) is a band that bleeds by
   --st-pad-x, and with the card's 18 px it stuck out over this band's border by a pixel on each side. */
.progressive-section.is-confirmation { --st-pad-x: 16px; padding: var(--st-pad-x); border: 1px solid var(--accent); border-radius: var(--st-radius); background: var(--accent-soft); }
/* The heading tile and a section's tag are tinted with the band's own colour, so on the band they vanished: they keep a surface. */
.progressive-section.is-confirmation > .progressive-heading > .st-icon-tile,
.progressive-section.is-confirmation :deep(.settings-section-tag) { background: var(--surface); }
/* Fields keep their own surface on the tinted confirmation band, so they still read as fields (shadcn inputs are transparent). */
.progressive-section.is-confirmation :deep(:is(input:not([type="checkbox"], [type="radio"], [type="range"]), textarea)) { background-color: var(--surface); }
.progressive-heading { display: flex; align-items: flex-start; gap: 12px; }
.progressive-heading h3, .progressive-section > h3 { margin: 0; font-size: 17px; font-weight: 650; }
.progressive-heading p { margin: 4px 0 0; color: var(--muted); font-size: 13px; line-height: 1.55; }
.progressive-fields { display: grid; gap: 12px; min-width: 0; margin: 0; padding: 0; border: 0; }
.progressive-incomplete { display: flex; align-items: center; gap: 8px; margin: 0; font-size: 13px; color: var(--muted); }
/* The final action is a button, not a bar across the whole step. */
.progressive-section > [data-flow-next] { justify-self: start; min-width: 160px; }
.progressive-actions { display: flex; flex-wrap: wrap; gap: 8px; border-top: 1px solid var(--line); padding-top: 14px; }
</style>
