<script lang="ts">
export type AiGlyphState = "idle" | "ready" | "thinking" | "searching" | "writing" | "done" | "suggest" | "unavailable";
export type AiGlyphSize = "xs" | "s" | "m" | "l" | "xl" | "hero";
/** The named sizes of the board, in pixels. */
export const AI_GLYPH_SIZES: Record<AiGlyphSize, number> = { xs: 12, s: 16, m: 20, l: 28, xl: 44, hero: 72 };
/** The states that loop while a model works; they stop with the result. */
export const AI_GLYPH_WORKING: readonly AiGlyphState[] = ["thinking", "searching", "writing"];
</script>

<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../AtlasIcon.vue";

/**
 * The one AI icon of Atlas (plan §0a): the registry's sparkles drawing in the AI ink, the
 * same geometry the editor's assist module draws. Show it only next to model output or an input
 * that starts a model task, or explicit AI settings identity. Rule-based suggestions carry the icon of their type.
 * The meaning is in the text beside it, so the glyph is hidden from assistive technology.
 *
 * Sizes (refresh 2.5, board „AI znak – velikosti, stavy a průběh“), a number of pixels or a name:
 * `xs` 12 (search field, a suggestion chip, the „AI“ tag), `s` 16 (buttons, menus, the Asistent item, an answer),
 * `m` 20 (the assistant's header, the pre-publish check), `l` 28 (an answer in the assistant, a page summary),
 * `xl` 44 (the empty assistant, first run), `hero` 72 (an AI welcome, onboarding).
 *
 * States move the parts of that one drawing (styles/ai.css):
 * - `idle` still; `ready` turns once when the control around it is pointed at or focused;
 * - `thinking` (also `working`) twinkles while the small stars orbit; `searching` adds a turning ring;
 *   `writing` pulses with the text; these are the working loops and stop with the result;
 * - `done` gathers the small stars into the big one, ticks and is whole again, once; `suggest` glows once;
 * - `unavailable` is grey with an amber dot and never moves.
 * Reduced motion keeps only a slow fade of the working states (styles/ai.css).
 *
 * Production model surfaces are enumerated by aiGlyphBoundary.guard.test.ts; the gallery
 * (#/gallery/ai) shows their actual components.
 */
const props = withDefaults(defineProps<{ working?: boolean; size?: number | AiGlyphSize; state?: AiGlyphState }>(), { working: false, size: 16, state: undefined });
const resolved = computed<AiGlyphState>(() => props.state ?? (props.working ? "thinking" : "idle"));
const working = computed(() => AI_GLYPH_WORKING.includes(resolved.value));
const px = computed(() => (typeof props.size === "number" ? props.size : AI_GLYPH_SIZES[props.size] ?? 16));
</script>

<template>
  <span class="ai-glyph" data-ai-glyph :data-state="resolved" :data-working="working || undefined" :style="{ '--ai-glyph-size': `${px}px` }" aria-hidden="true">
    <AtlasIcon name="sparkles" :size="px" class="ai-glyph-mark" :class="{ 'atlas-ai-twinkle': resolved === 'thinking' }" />
    <svg v-if="resolved === 'done'" class="ai-glyph-check" viewBox="0 0 24 24" aria-hidden="true"><path d="M6.5 12.5l3.5 3.5 7.5-8" /></svg>
  </span>
</template>

<style scoped>
.ai-glyph { position: relative; display: inline-flex; flex: none; color: var(--ai-ink, var(--accent)); }
</style>
