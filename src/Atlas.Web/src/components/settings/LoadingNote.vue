<script setup lang="ts">
import AtlasIcon from "../AtlasIcon.vue";

/**
 * One line saying that something is loading or working ("Načítání trezorů…"), with a spinner that
 * turns static under reduced motion. Use it where a short status fits the flow; LoadingSkeleton is for
 * the placeholder of a whole list or page region while it loads.
 *
 * With `active` the note is always rendered and only shows or hides, so its space is reserved up front
 * and the page never jumps when work starts or ends. Use that for anything that repeats (search, filter,
 * refresh, save); omit it for a one-off placeholder rendered with v-if. `inline` fits the note into an
 * existing row, such as a count or a heading.
 */
withDefaults(defineProps<{ label: string; active?: boolean; inline?: boolean }>(), { active: undefined, inline: false });
</script>

<template>
  <component :is="inline ? 'span' : 'p'" class="loading-note" :class="{ 'is-inline': inline }" :data-reserved="active === undefined ? undefined : active ? 'active' : 'idle'" role="status">
    <AtlasIcon name="loader-circle" :size="15" class="atlas-spinner" aria-hidden="true" />
    <span v-if="active === undefined">{{ label }}</span>
    <template v-else><span aria-hidden="true">{{ label }}</span><span class="visually-hidden">{{ active ? label : "" }}</span></template>
  </component>
</template>

<style scoped>
.loading-note { display: flex; align-items: center; gap: 7px; min-height: 20px; margin: 0; color: var(--muted); font-size: 13px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both; }
.loading-note.is-inline { display: inline-flex; min-height: 0; vertical-align: middle; font-size: inherit; font-weight: 400; }
.loading-note > svg { flex-shrink: 0; }
/* Reserved: the note keeps its size while idle and fades in only after a short delay. */
.loading-note[data-reserved] { animation: none; visibility: hidden; opacity: 0; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), visibility var(--atlas-motion-base); }
/* A loop only while something works: an idle note keeps its room but its spinner stands (and costs no frames). */
.loading-note[data-reserved="idle"] > .atlas-spinner { animation: none; }
.loading-note[data-reserved="active"] { visibility: visible; opacity: 1; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer), visibility var(--atlas-motion-defer); }
</style>
