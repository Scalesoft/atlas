<script setup lang="ts">
import type { AiSourceView } from "../../ai/assistantApi";
import { isPlainNavigationClick } from "../../pageLinks";
import t from "../../i18n/ai-answer";
import { Button } from "../ui/button";
import StatusBadge from "../settings/StatusBadge.vue";
import { sourceLabel, sourceRoute } from "./aiAnswerText";

/**
 * The sources of an answer, named wherever they appear (UX design §6.3). Not a search result row: that one is bound to App.vue
 * and its keyboard, and carries no state of staleness.
 *
 * - `tray`: one chip per source, in a row under the answer's heading. It is where the gathered sources land (the answer
 *   flies them in from the result rows); chips listed in `hiddenNumbers` are placeholders that keep their space until then.
 * - `list`: one line per source with its space and a note on sources the text did not cite, for „Podklady odpovědi“.
 *   A source published again since the answer says so, with „Zeptat se znovu“.
 *
 * Entries come from `sources` only. Hover or focus reports the number (`highlight`), so the sentence, the citation and the
 * result row that belong to it can light up; a link opens the page as it is now at the heading (`open-source`).
 */
const props = withDefaults(defineProps<{
  sources: readonly AiSourceView[];
  variant?: "tray" | "list";
  highlighted?: number | null;
  hiddenNumbers?: readonly number[];
}>(), { variant: "tray", highlighted: null, hiddenNumbers: () => [] });
const emit = defineEmits<{ highlight: [number: number | null]; "open-source": [source: AiSourceView, newTab: boolean]; "ask-again": [] }>();

const open = (source: AiSourceView, event: MouseEvent) => {
  // Modified clicks belong to the browser; emitting as well opens a second tab.
  if (!isPlainNavigationClick(event)) return;
  emit("open-source", source, false);
};
</script>

<template>
  <ul v-if="sources.length" class="ai-sources" :data-variant="variant" data-ai-sources>
    <li v-for="source in sources" :key="source.number" class="ai-source" :data-source-number="source.number">
      <a
        class="ai-chip"
        :class="{ 'is-hl': highlighted === source.number }"
        :href="sourceRoute(source)"
        :data-source-chip="source.number"
        :style="hiddenNumbers.includes(source.number) ? { visibility: 'hidden' } : undefined"
        :aria-label="t.citation.source(source.number, source.title)"
        @mouseenter="emit('highlight', source.number)"
        @mouseleave="emit('highlight', null)"
        @focus="emit('highlight', source.number)"
        @blur="emit('highlight', null)"
        @click="open(source, $event)"
      ><span class="ai-chip-num" aria-hidden="true">{{ source.number }}</span><span class="ai-chip-label">{{ sourceLabel(source) }}</span></a>
      <template v-if="variant === 'list'">
        <span class="ai-source-meta">{{ source.spaceName }}<template v-if="!source.cited">{{ t.citation.usedNotCited }}</template></span>
        <span v-if="source.changed" class="ai-source-changed">
          <StatusBadge tone="info" :label="t.citation.changed" size="sm" />
          <Button type="button" variant="ghost" size="xs" @click="emit('ask-again')">{{ t.citation.askAgain }}</Button>
        </span>
      </template>
    </li>
  </ul>
</template>

<style scoped>
.ai-sources { display: flex; flex-wrap: wrap; gap: 6px; margin: 0; padding: 0; list-style: none; min-width: 0; }
.ai-sources[data-variant="list"] { display: grid; gap: 8px; }
.ai-source { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; min-width: 0; max-width: 100%; }
.ai-chip { display: inline-flex; align-items: center; gap: 7px; max-width: 100%; min-height: 26px; padding: 3px 9px 3px 8px; border: 1px solid var(--refresh-soft-line, var(--line)); border-radius: 7px; background: color-mix(in srgb, var(--canvas) 60%, var(--surface)); color: var(--text); font-size: 12px; text-decoration: none; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.ai-chip-label { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.ai-chip-num { flex: none; color: var(--ai-ink); font-size: 12px; font-weight: 700; font-variant-numeric: tabular-nums; line-height: 1; }
.ai-chip.is-hl { background: var(--accent-soft); border-color: var(--accent); }
@media (hover: hover) and (pointer: fine) { .ai-chip:hover { background: var(--accent-soft); border-color: var(--accent); } }
.ai-chip:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.ai-source-meta { color: var(--muted); font-size: 12px; }
.ai-source-changed { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
</style>
