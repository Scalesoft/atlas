<script setup lang="ts">
import { computed } from "vue";
import { useAiCapabilities } from "../../composables/useAiCapabilities";
import t from "../../i18n/assistant";
import AiGlyph from "../ai/AiGlyph.vue";
import { Button } from "../ui/button";

/**
 * The two entries that open the Assistant (plan phase 3 §15): the item of the sidebar below „Hledat ve wiki“ and the button
 * „Zeptat se na tuto stránku“ beside the page's metadata. Both start a model task, so both carry the AI glyph; this is the one
 * place of the entries that draws it. Without the `assistant` capability nothing is rendered (AI-24): no element, no
 * placeholder. The page entry is for reading a published page only (O12: in the editor the answer would come from another text),
 * and asks, so it follows P11's one rule (`canAsk`, App.vue) like the palette row of the same name and the mobile bar.
 */
const props = withDefaults(defineProps<{ variant?: "sidebar" | "page"; pageReading?: boolean; canAsk?: boolean }>(), { variant: "sidebar", pageReading: false, canAsk: false });
const emit = defineEmits<{ launch: [] }>();
const { capabilities } = useAiCapabilities();
const shown = computed(() => (capabilities.value.assistant || capabilities.value.agent) && (props.variant === "sidebar" || (props.pageReading && props.canAsk)));
</script>

<template>
  <button v-if="shown && variant === 'sidebar'" type="button" class="assistant-launch" data-assistant-launch="sidebar" @click="emit('launch')">
    <AiGlyph :size="16" state="ready" /><span class="assistant-launch-label">{{ t.launcher.sidebar }}</span><kbd>Ctrl J</kbd>
  </button>
  <Button v-else-if="shown" type="button" variant="ghost" size="sm" class="assistant-launch-page" data-assistant-launch="page" @click="emit('launch')">
    <AiGlyph :size="15" state="ready" />{{ t.launcher.askPage }}
  </Button>
</template>

<style scoped>
/* Directly under „Hledat ve wiki“ (refresh 2.5): the AI entry carries the AI wash, the search stays neutral. */
.assistant-launch {
  display: flex;
  align-items: center;
  justify-content: flex-start;
  gap: 8px;
  width: 100%;
  min-height: 36px;
  padding: 0 10px;
  border: 1px solid var(--ai-line-strong);
  border-radius: var(--radius-md);
  background: linear-gradient(90deg, color-mix(in srgb, var(--accent) 12%, transparent), color-mix(in srgb, var(--ai-ink) 12%, transparent)), var(--surface);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  font-weight: 600;
  text-align: left;
  cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), filter var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
/* A press answers like every control of the shell (scale to --atlas-press); the launcher had no pressed state. */
.assistant-launch:active { transform: scale(var(--atlas-press)); }
/* Only the label takes the free room: `> span` also matched the glyph's own span, so the two shared it and the label began at x 117
   instead of beside the glyph, where the label of "Hledat ve wiki" above it begins (x 47). */
.assistant-launch > .assistant-launch-label { flex: 1; min-width: 0; text-align: left; }
.assistant-launch kbd { border-color: transparent; background: color-mix(in srgb, var(--surface) 80%, transparent); color: var(--ai-ink); font-weight: 600; }
@media (hover: hover) and (pointer: fine) { .assistant-launch:hover { border-color: var(--ai-ink); filter: brightness(.99); } }
.assistant-launch:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
