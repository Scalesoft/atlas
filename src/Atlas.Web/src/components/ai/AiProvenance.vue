<script setup lang="ts">
import { computed, ref } from "vue";
import type { AiProvenanceView, AiScope, AiSourceView } from "../../ai/assistantApi";
import t from "../../i18n/ai-answer";
import { formatAgo, formatDateTime } from "../../formatDate";
import { Popover, PopoverAnchor, PopoverContent } from "../ui/popover";
import { Tooltip, TooltipContent, TooltipTrigger } from "../ui/tooltip";
import AiGlyph from "./AiGlyph.vue";
import AiSourceList from "./AiSourceList.vue";
import { sourceCount } from "./aiAnswerText";

/**
 * The line under every model output (UX design §5.4): „Firemní model · 3 zdroje · před 2 min · Podklady odpovědi“, one
 * component so the wording is the same everywhere. It is output of a model, so it carries the AI glyph. The tooltip names the
 * provider and model; common wording only says „Firemní model“ or „Cloudový model“. The count and „Podklady odpovědi“ open one
 * popover: the scope, the sentence on what the answer used, and every source it was given, cited or not. The time is relative
 * for a day and absolute after. `trailing` is where the feedback buttons go, right-aligned.
 */
const props = defineProps<{
  provenance: AiProvenanceView;
  sources?: readonly AiSourceView[];
  /** When the answer was written; omitted for an answer that was just written (shows „právě teď“). */
  createdAt?: string | Date | null;
  scope?: AiScope | null;
  /** Name of the space of a `space` scope. */
  scopeName?: string | null;
}>();

const emit = defineEmits<{ "open-source": [source: AiSourceView, newTab: boolean] }>();
const open = ref(false);
/** The count and „Podklady odpovědi“ toggle the popover themselves; the outside-click that precedes their click must not close it first. */
function keepOpenOnOwnLine(event: Event) {
  const target = (event as CustomEvent<{ originalEvent?: Event }>).detail?.originalEvent?.target ?? event.target;
  if (target instanceof Element && target.closest("[data-ai-provenance-line]")) event.preventDefault();
}
const DAY = 24 * 60 * 60 * 1000;
const when = computed(() => {
  if (!props.createdAt) return t.provenance.now;
  const at = props.createdAt instanceof Date ? props.createdAt.getTime() : new Date(props.createdAt).getTime();
  if (Number.isNaN(at)) return "";
  return Date.now() - at < DAY ? formatAgo(props.createdAt) : formatDateTime(props.createdAt);
});
const label = computed(() => (props.provenance.location === "cloud" ? t.provenance.cloud : t.provenance.company));
const scopeText = computed(() => {
  const scope = props.scope;
  if (scope?.kind === "page") return t.provenance.page;
  if (scope?.kind === "space") return t.provenance.space(props.scopeName);
  return t.provenance.wiki;
});
const used = computed(() => props.sources ?? []);
</script>

<template>
  <div class="ai-provenance" data-ai-provenance>
    <Popover v-model:open="open">
      <PopoverAnchor as-child>
        <span class="ai-provenance-line" data-ai-provenance-line>
          <AiGlyph :size="13" />
          <Tooltip>
            <TooltipTrigger as-child><span class="ai-provenance-model" tabindex="0" data-ai-provenance-model>{{ label }}</span></TooltipTrigger>
            <TooltipContent inline>{{ provenance.provider }} · {{ provenance.model }}</TooltipContent>
          </Tooltip>
          <span aria-hidden="true">·</span>
          <button type="button" class="ai-provenance-link" data-ai-provenance-count data-ai-provenance-why :aria-expanded="open" @click="open = !open">{{ used.length ? sourceCount(used.length) : t.provenance.basis }}</button>
          <template v-if="when"><span aria-hidden="true">·</span><span data-ai-provenance-when>{{ when }}</span></template>
        </span>
      </PopoverAnchor>
      <PopoverContent class="ai-provenance-popover" align="start" data-ai-provenance-popover @interact-outside="keepOpenOnOwnLine">
        <strong>{{ t.provenance.basis }}</strong>
        <dl class="ai-provenance-facts">
          <div><dt>{{ t.provenance.scope }}</dt><dd>{{ scopeText }}</dd></div>
          <div><dt>{{ t.provenance.model }}</dt><dd>{{ label }}</dd></div>
        </dl>
        <p>{{ t.provenance.publishedOnly }}</p>
        <template v-if="used.length">
          <small class="ai-provenance-heading">{{ t.provenance.used(used.length) }}</small>
          <AiSourceList :sources="used" variant="list" @open-source="(source, newTab) => emit('open-source', source, newTab)" />
        </template>
      </PopoverContent>
    </Popover>
    <span v-if="$slots.trailing" class="ai-provenance-trailing"><slot name="trailing" /></span>
  </div>
</template>

<style scoped>
.ai-provenance { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; margin-top: 10px; color: var(--muted); font-size: 12px; }
.ai-provenance-line { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 4px 6px; min-width: 0; }
.ai-provenance-model { border-radius: 3px; cursor: default; }
.ai-provenance-model:focus-visible, .ai-provenance-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.ai-provenance-link { padding: 0; border: 0; background: none; color: inherit; font-size: inherit; cursor: pointer; border-radius: 3px; }
@media (hover: hover) and (pointer: fine) { .ai-provenance-link:hover { color: var(--text); } }
.ai-provenance-trailing { display: inline-flex; gap: 2px; margin-left: auto; }
</style>

<style>
.ai-provenance-popover { display: grid; gap: 8px; width: min(360px, calc(100vw - 2rem)); padding: 12px; font-size: 13px; }
.ai-provenance-popover p { margin: 0; color: var(--muted); line-height: 1.5; }
.ai-provenance-facts { display: grid; gap: 2px; margin: 0; }
.ai-provenance-facts > div { display: flex; gap: 8px; }
.ai-provenance-facts dt { min-width: 52px; color: var(--muted); }
.ai-provenance-facts dd { margin: 0; font-weight: 600; }
.ai-provenance-heading { color: var(--muted); font-size: 12px; font-weight: 600; }
</style>
