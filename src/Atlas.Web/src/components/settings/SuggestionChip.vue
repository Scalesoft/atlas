<script setup lang="ts">
import t from "../../i18n/settings-flow";
import { computed, onMounted, ref, useId, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "../ui/popover";
import { animateAtlas, dismissOut, glow, motionMs, revealHeight, solidify } from "../../motion/aiMotion";

/**
 * One suggestion: a value that is not saved yet, which the person may use, dismiss or ask about
 * (plan §4 A1; P1 similar pages now, placement, tags and change notes later). Not a badge (static
 * label), StatusBadge (a state), ResultBanner (a finished action), ChoiceCards/OptionPills (picking
 * from options) or SettingsListRow (a row without proposal semantics). Inside the editor the assist
 * module's block marker plays this role instead.
 *
 * The dashed outline is an SVG rect, so applying it can join the dashes (solidify); once applied the
 * outline goes and the place glows. `icon` is the icon of the suggestion's type; a suggestion that a
 * model wrote puts AiGlyph into the `glyph` slot instead (from phase 3). It never announces itself
 * and never takes focus; after dismiss the parent returns focus to where the person was.
 *
 * - `layout="inline"`: one line next to a field (radius 8); the default slot is the value.
 * - `layout="panel"`: a header and content below it, e.g. a list of similar pages (radius 10).
 * - `applyLabel: null`: an informational suggestion without "Použít".
 */
export type SuggestionState = "idle" | "applying" | "applied" | "stale";

const props = withDefaults(
  defineProps<{
    label?: string;
    icon?: string;
    reason?: string;
    applyLabel?: string | null;
    dismissLabel?: string;
    state?: SuggestionState;
    layout?: "inline" | "panel";
  }>(),
  { label: undefined, icon: undefined, reason: undefined, applyLabel: undefined, dismissLabel: undefined, state: "idle", layout: "inline" },
);
const emit = defineEmits<{ apply: []; dismiss: [] }>();

// `null` for the apply label means no apply button; only an absent one falls back to the default.
const applyText = computed(() => (props.applyLabel === undefined ? t.suggestion.apply : props.applyLabel));
const id = `suggestion-${useId()}`;
const root = ref<HTMLElement>();
const outline = ref<SVGRectElement>();
const leaving = ref(false);
const inline = computed(() => props.layout === "inline");
const radius = computed(() => (inline.value ? 7.5 : 9.5));

onMounted(() => {
  const el = root.value;
  if (!el) return;
  // A suggestion appears where it stays: a panel unfolds below its field, a chip fades in from .94.
  if (!inline.value) revealHeight(el);
  else animateAtlas(el, [{ opacity: 0, transform: "scale(.94)" }, { opacity: 1, transform: "none" }], { duration: motionMs(el, "--atlas-motion-surface") });
});

watch(() => props.state, (state, previous) => {
  if (state === "applying" && outline.value) solidify(outline.value);
  if (state === "applied" && previous !== "applied" && root.value) glow(root.value);
}, { flush: "post" });

function dismiss() {
  const el = root.value;
  if (!el || leaving.value) return;
  leaving.value = true;
  // Under reduced motion dismissOut keeps only its short fade: nothing vanishes at once.
  const animation = dismissOut(el);
  if (!animation) return emit("dismiss");
  void animation.finished.then(() => emit("dismiss"), () => emit("dismiss"));
}
</script>

<template>
  <div ref="root" class="suggestion-chip" role="group" :aria-labelledby="`${id}-head`" :data-layout="layout" :data-state="state">
    <svg v-if="state !== 'applied'" class="atlas-suggestion-outline" aria-hidden="true"><rect ref="outline" x=".5" y=".5" :rx="radius" :ry="radius" /></svg>
    <div class="suggestion-chip-head">
      <span v-if="icon || $slots.glyph" class="suggestion-chip-icon"><slot name="glyph"><AtlasIcon :name="icon!" :size="inline ? 13 : 14" /></slot></span>
      <span :id="`${id}-head`" class="suggestion-chip-label">{{ label ?? t.suggestion.label }}<template v-if="inline">: <b class="suggestion-chip-value"><slot /></b></template></span>
    </div>
    <div v-if="!inline" class="suggestion-chip-body"><slot /></div>
    <div class="suggestion-chip-foot">
      <span v-if="state === 'stale'" class="suggestion-chip-note is-stale"><AtlasIcon name="triangle-alert" :size="13" />{{ t.suggestion.stale }}</span>
      <span v-else-if="state === 'applied'" class="suggestion-chip-note is-applied"><AtlasIcon name="check" :size="13" />{{ t.suggestion.applied }}</span>
      <slot name="actions" />
      <Button
        v-if="applyText && state !== 'applied'"
        :id="`${id}-apply`"
        type="button"
        size="xs"
        variant="outline"
        data-suggestion-apply
        :aria-labelledby="`${id}-apply ${id}-head`"
        :disabled="state === 'stale' || state === 'applying'"
        :aria-busy="state === 'applying' || undefined"
        @click="emit('apply')"
      ><AtlasIcon v-if="state === 'applying'" name="loader-circle" :size="13" class="atlas-spinner" />{{ applyText }}</Button>
      <Popover v-if="reason">
        <PopoverTrigger as-child><button type="button" class="suggestion-chip-why" data-suggestion-why>{{ t.suggestion.why }}</button></PopoverTrigger>
        <PopoverContent class="suggestion-chip-reason" align="start">{{ reason }}</PopoverContent>
      </Popover>
      <Button
        type="button"
        :size="inline ? 'icon-xs' : 'xs'"
        variant="ghost"
        class="suggestion-chip-dismiss"
        data-suggestion-dismiss
        :aria-labelledby="`${id}-dismiss ${id}-head`"
        @click="dismiss"
      ><AtlasIcon v-if="inline" name="close" :size="13" /><span :id="`${id}-dismiss`" :class="{ 'sr-only': inline }">{{ dismissLabel ?? t.suggestion.dismiss }}</span></Button>
    </div>
  </div>
</template>

<style scoped>
.suggestion-chip { position: relative; background: var(--surface); color: var(--muted); font-size: 12.5px; line-height: 1.45; }
.suggestion-chip[data-layout="inline"] { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; max-width: 100%; padding: 4px 6px 4px 9px; border-radius: 8px; }
/* Icon and text stay together and wrap inside; the actions follow and wrap below in a narrow column. */
.suggestion-chip[data-layout="inline"] .suggestion-chip-head { display: flex; align-items: center; gap: 8px; min-width: 0; }
.suggestion-chip[data-layout="inline"] .suggestion-chip-foot { display: contents; }
.suggestion-chip[data-layout="panel"] { display: grid; gap: 8px; padding: 10px 12px; border-radius: 10px; overflow: hidden; }
.suggestion-chip[data-layout="panel"] .suggestion-chip-head { display: flex; align-items: center; gap: 6px; }
.suggestion-chip[data-layout="panel"] .suggestion-chip-foot { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 6px; }
.suggestion-chip[data-layout="panel"] .suggestion-chip-note { margin-right: auto; }
.suggestion-chip-icon { display: inline-flex; flex: none; }
.suggestion-chip-label { min-width: 0; }
.suggestion-chip-value { color: var(--text); font-weight: 600; overflow-wrap: anywhere; }
.suggestion-chip-body { min-width: 0; color: var(--text); font-size: 13px; }
.suggestion-chip-note { display: inline-flex; align-items: center; gap: 4px; }
.suggestion-chip-note.is-stale { color: var(--warning); }
.suggestion-chip-note.is-applied { color: var(--success); }
.suggestion-chip-why { padding: 0; border: 0; background: none; color: var(--muted); font-size: 12px; text-decoration: underline dotted; text-underline-offset: 3px; cursor: pointer; }
@media (hover: hover) and (pointer: fine) {
  .suggestion-chip-why:hover { color: var(--text); }
}
.suggestion-chip-why:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 3px; }
/* While it runs, the outline stays joined (solidify animates into this). */
.suggestion-chip[data-state="applying"] .atlas-suggestion-outline rect { stroke-dasharray: 11 0; stroke: var(--line); }
</style>
