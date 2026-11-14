<script setup lang="ts">
import t from "../../i18n/settings-shell";
import { computed, ref, useId } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import CommandIcon from "../CommandIcon.vue";
import InlineReveal from "./InlineReveal.vue";
import type { StatusTone } from "./types";

/**
 * A card of related settings or a list: icon, title, optional count and `#actions` in the header.
 * `advanced` starts collapsed but always says in its header what is inside and what is in effect
 * (`summary`), so nothing active hides behind it. Without a title it is a plain card (a wizard's
 * frame); give it a `label` then.
 */
const props = withDefaults(
  defineProps<{
    title?: string;
    description?: string;
    icon?: string;
    count?: number;
    countTone?: StatusTone;
    advanced?: boolean;
    summary?: string;
    flush?: boolean;
    label?: string;
    /** Tints the header band: `warning` for "Vyžaduje pozornost" (board 06). */
    tone?: StatusTone;
  }>(),
  { title: undefined, description: undefined, icon: undefined, count: undefined, countTone: undefined, advanced: false, summary: undefined, flush: false, label: undefined, tone: undefined },
);
// Forms can reveal invalid advanced fields without replacing the section (and its draft).
const controlledOpen = defineModel<boolean | undefined>("open", { default: undefined });
const localOpen = ref(!props.advanced);
const open = computed({
  get: () => controlledOpen.value ?? localOpen.value,
  set: (value: boolean) => { localOpen.value = value; controlledOpen.value = value; },
});
const id = `settings-section-${useId()}`;
</script>

<template>
  <section class="settings-section" :class="{ 'is-advanced': advanced, 'is-flush': flush, 'is-bare': !title }" :aria-labelledby="title ? `${id}-title` : undefined" :aria-label="title ? undefined : label">
    <button
      v-if="advanced && title"
      type="button"
      class="settings-section-header is-toggle"
      :aria-expanded="open"
      :aria-controls="`${id}-content`"
      @click="open = !open"
    >
      <span v-if="icon" class="st-icon-tile settings-section-icon" data-tone="neutral"><CommandIcon :name="icon" :size="17" /></span>
      <span class="settings-section-heading">
        <span :id="`${id}-title`" class="settings-section-title">{{ title }}<span class="settings-section-tag">{{ t.advanced }}</span></span>
        <span v-if="summary" class="settings-section-summary">{{ summary }}</span>
      </span>
      <AtlasIcon name="down" :size="18" class="settings-section-chevron" />
    </button>
    <header v-else-if="title" class="settings-section-header" :data-tone="tone" :data-has-description="description ? '' : undefined">
      <slot name="icon"><span v-if="icon" class="st-icon-tile settings-section-icon" :data-tone="tone === 'success' ? 'success' : 'neutral'"><CommandIcon :name="icon" :size="17" /></span></slot>
      <span class="settings-section-heading">
        <h3 :id="`${id}-title`" class="settings-section-title">{{ title }}<span v-if="count !== undefined" class="st-count" :data-tone="countTone">{{ count }}</span><slot name="title-extra" /></h3>
        <span v-if="description" class="settings-section-summary">{{ description }}</span>
      </span>
      <span v-if="$slots.actions" class="settings-section-actions"><slot name="actions" /></span>
    </header>
    <InlineReveal v-if="advanced && title" :id="`${id}-content`" v-model:open="open">
      <div class="settings-section-reveal">
        <p v-if="description" class="settings-section-description">{{ description }}</p>
        <div class="settings-section-body"><slot /></div>
      </div>
    </InlineReveal>
    <div v-else class="settings-section-body"><slot /></div>
  </section>
</template>

<style scoped>
/* Refresh 2.5 (boards 06–10): a card with a 14 px radius, a header band separated by a soft line, and a
   body whose rows reach the card edges (settings.css). Every inset comes from --st-pad-x. */
.settings-section {
  min-width: 0;
  border: 1px solid var(--line);
  border-radius: var(--st-radius);
  background: var(--surface);
  box-shadow: var(--st-card-shadow);
}
.settings-section.is-flush { border: 0; background: transparent; box-shadow: none; }
.settings-section-header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
  width: 100%;
  min-height: 60px;
  padding: 12px var(--st-pad-x, 18px);
  border: 0;
  border-bottom: 1px solid var(--st-line-soft, var(--line));
  border-radius: var(--st-radius) var(--st-radius) 0 0;
  background: transparent;
  text-align: left;
  color: var(--text);
  font: inherit;
}
.settings-section-header[data-tone="warning"] { background: color-mix(in srgb, var(--warning-bg) 55%, var(--surface)); border-bottom-color: color-mix(in srgb, var(--warning) 22%, transparent); }
.settings-section-header[data-tone="warning"] .settings-section-icon { color: var(--warning); background: transparent; box-shadow: none; }
.settings-section-header[data-tone="success"] .settings-section-icon { color: var(--success); background: var(--success-bg); }
/* Only its own header and body: a card nested inside a flush section (the AI setup's guided steps) keeps its insets. */
.is-flush > .settings-section-header { min-height: 0; padding: 0 0 10px; border-bottom: 0; }
.settings-section-header.is-toggle { border-bottom: 0; border-radius: var(--st-radius); cursor: pointer; transition: background-color var(--atlas-motion-fast); }
.settings-section-header.is-toggle[aria-expanded="true"] { border-radius: var(--st-radius) var(--st-radius) 0 0; }
@media (hover: hover) and (pointer: fine) {
  .settings-section-header.is-toggle:hover { background: color-mix(in srgb, var(--hover) 45%, transparent); }
}
.settings-section-header.is-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.settings-section-icon { width: 32px; height: 32px; border-radius: 10px; }
/* With a description the tile and the actions stay beside the title and the first line of the description (a box of 40 px, which is
   what they were centred on while the description had one line) instead of floating at the middle of a long one. Without one the header is
   still centred. */
.settings-section-header[data-has-description] { align-items: flex-start; }
.settings-section-header[data-has-description] > .settings-section-icon { margin-top: 4px; }
@container settings-shell (min-width: 681px) {
  .settings-section-header[data-has-description] > .settings-section-actions { min-height: 40px; align-items: center; }
}
.settings-section-heading { display: grid; gap: 1px; flex: 1 1 min(220px, calc(100% - 56px)); min-width: 0; overflow-wrap: anywhere; }
.settings-section-title { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0; font-size: 15px; font-weight: 600; line-height: 1.35; }
.settings-section-tag { padding: 1px 7px; border-radius: 999px; font-size: 11px; font-weight: 600; color: var(--muted); background: var(--hover); }
.settings-section-summary { color: var(--muted); font-size: 12.5px; line-height: 1.5; }
.settings-section-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; flex-shrink: 0; }
.settings-section-chevron { rotate: -90deg; flex-shrink: 0; color: var(--muted); transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
/* The panel opens over the slow token and closes over the base one (InlineReveal): the chevron turns in step with it. */
[aria-expanded="true"] > .settings-section-chevron { rotate: 0deg; transition-duration: var(--atlas-motion-slow); }
@media (prefers-reduced-motion: reduce) { .settings-section-chevron { transition: opacity var(--atlas-motion-fast); } }
.settings-section-reveal { border-top: 1px solid var(--st-line-soft, var(--line)); }
.settings-section-description { margin: 0; padding: 14px var(--st-pad-x, 18px) 0; color: var(--muted); font-size: 13px; line-height: 1.6; }
.settings-section-body {
  display: flex;
  flex-direction: column;
  gap: var(--st-body-gap, 12px);
  padding: var(--st-body-pad-y, 16px) var(--st-pad-x, 18px);
  min-width: 0;
}
.settings-section-body > * { min-width: 0; }
/* A card that is only its header (a summary, a module with nothing to set yet) drew an empty 32 px band under a line. */
.settings-section-body:empty { display: none; }
.settings-section-header:has(+ .settings-section-body:empty) { border-bottom-color: transparent; border-radius: var(--st-radius); }
.is-flush > .settings-section-body { padding: 0; }
</style>
