<script setup lang="ts">
import type { AiSourceView } from "../../ai/assistantApi";
import t from "../../i18n/ai-answer";
import { Button } from "../ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "../ui/popover";
import StatusBadge from "../settings/StatusBadge.vue";
import { sourceLabel, sourceRoute } from "./aiAnswerText";
import { cleanSnippet } from "../../searchHighlight";

/**
 * The inline [n] after a sentence. The number and everything in the preview come from the `source` of the answer's `sources`
 * list; the component never reads a number out of text. Hover or focus reports it (`highlight`) so the sentence, the source chip
 * and the result row light up (120 ms); a click opens the preview: page › heading, space, the passage (markdown syntax cleaned
 * like a search snippet), and a link to the page
 * as it is now at that heading (Ctrl or Cmd + click opens a new tab, a historical revision is never linked). A source published
 * again since the answer shows „Zdroj se od té doby změnil.“ and offers „Zeptat se znovu“.
 */
const props = withDefaults(defineProps<{ source: AiSourceView; highlighted?: boolean }>(), { highlighted: false });
const emit = defineEmits<{ highlight: [number: number | null]; "open-source": [source: AiSourceView, newTab: boolean]; "ask-again": [] }>();

const open = (event: MouseEvent) => emit("open-source", props.source, event.ctrlKey || event.metaKey);
</script>

<template>
  <Popover>
    <PopoverTrigger as-child>
      <button
        type="button"
        class="ai-cite"
        :class="{ 'is-hl': highlighted }"
        :data-cite="source.number"
        :aria-label="t.citation.source(source.number, source.title)"
        @mouseenter="emit('highlight', source.number)"
        @mouseleave="emit('highlight', null)"
        @focus="emit('highlight', source.number)"
        @blur="emit('highlight', null)"
      >{{ source.number }}</button>
    </PopoverTrigger>
    <PopoverContent class="ai-cite-preview" align="start" data-ai-citation-preview>
      <strong class="ai-cite-title">{{ sourceLabel(source) }}</strong>
      <small class="ai-cite-space">{{ source.spaceName }}</small>
      <blockquote v-if="source.excerpt" class="ai-cite-excerpt">{{ cleanSnippet(source.excerpt) }}</blockquote>
      <div v-if="source.changed" class="ai-cite-changed">
        <StatusBadge tone="info" :label="t.citation.changed" size="sm" />
        <Button type="button" variant="outline" size="xs" @click="emit('ask-again')">{{ t.citation.askAgain }}</Button>
      </div>
      <a class="ai-cite-open" :href="sourceRoute(source)" data-ai-open-source @click="open">{{ t.citation.open }}</a>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.ai-cite { display: inline-grid; place-items: center; min-width: 17px; height: 17px; margin: 0 1px; padding: 0 3px; border: 0; border-radius: 4px; background: var(--accent-soft); color: var(--accent); font: 600 10.5px/1 ui-monospace, "SFMono-Regular", Consolas, monospace; vertical-align: 2px; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
/* Hover moves nothing (a lifted 17 px marker left the pointer on its edge and flickered): colour only. */
.ai-cite.is-hl { background: var(--accent); color: var(--on-action, #fff); }
@media (hover: hover) and (pointer: fine) { .ai-cite:hover { background: var(--accent); color: var(--on-action, #fff); } }
.ai-cite:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>

<style>
.ai-cite-preview { display: grid; gap: 6px; width: min(320px, calc(100vw - 2rem)); padding: 12px; font-size: 13px; }
.ai-cite-title { overflow-wrap: anywhere; }
.ai-cite-space { color: var(--muted); font-size: 12px; }
.ai-cite-excerpt { margin: 0; padding: 8px 10px; border-radius: 8px; background: var(--canvas); color: var(--muted); line-height: 1.5; overflow-wrap: anywhere; }
.ai-cite-changed { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; }
.ai-cite-open { width: fit-content; color: var(--accent); font-weight: 600; text-decoration: underline; text-underline-offset: 3px; }
</style>
