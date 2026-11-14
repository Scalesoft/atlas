<script setup lang="ts">
import { computed, ref } from "vue";
import { automaticPublicationHint, draftWording } from "../../publishing/pagePublication";
import AtlasIcon from "../AtlasIcon.vue";
import { useSegmentedThumb } from "../library/segmentedThumb";
import StatusBadge from "../settings/StatusBadge.vue";
import { Button } from "../ui/button";
import t from "../../i18n/shell";

/**
 * What readers see, said to a writer in the page's top bar (owner 2026-11-07, board „Koncept v horní liště“, variant A). A draft
 * (koncept: saved changes nobody published yet) names the version readers read meanwhile, and the bar tints itself through
 * `data-publication="draft"` (refresh-theme.css); a live page says Živě. A page in sync shows nothing here and readers never see it.
 * A published page's draft also switches „Koncept | Publikovaná“ (`viewingPublished`): the host shows the published revision
 * read-only meanwhile, and „Zpět ke konceptu“ (or Escape, the host's) returns to the draft.
 * Choose it over PageStatusBadge, the state as a chip in lists of pages, when the state belongs to the open page's own bar.
 */
const props = defineProps<{ state: "draft" | "live"; published: boolean; publishedAt?: string | null; automatic?: boolean; canViewPublished?: boolean }>();
const viewingPublished = defineModel<boolean>("viewingPublished", { default: false });
const wording = computed(() => draftWording(props.published, props.publishedAt));
const label = computed(() => (viewingPublished.value ? wording.value.publishedLabel : wording.value.label));
const automatic = computed(() => props.automatic && !viewingPublished.value);
const full = computed(() => (automatic.value ? `${label.value}; ${automaticPublicationHint()}` : label.value));
const track = ref<HTMLElement>();
const thumb = useSegmentedThumb(track, '[aria-pressed="true"]', viewingPublished);
</script>

<template>
  <StatusBadge v-if="state === 'live'" tone="success" dot size="sm" :label="t.pubState.live" :title="t.pubState.liveTitle" data-publication-state="live" />
  <span v-else class="page-publication-state" :data-publication-state="viewingPublished ? 'published-view' : 'draft'" :title="full">
    <AtlasIcon :name="viewingPublished ? 'eye' : 'pencil'" :size="14" />
    <span class="page-publication-full">{{ label }}<span v-if="automatic" class="page-publication-hint"> · {{ automaticPublicationHint() }}</span></span>
    <span v-if="canViewPublished" ref="track" v-bind="thumb" class="page-publication-switch" role="group" :aria-label="t.pubState.viewLabel">
      <button type="button" :aria-pressed="!viewingPublished" @click="viewingPublished = false">{{ t.pubState.draft }}</button>
      <button type="button" :aria-pressed="viewingPublished" :title="t.pubState.publishedTitle" @click="viewingPublished = true">{{ t.pubState.published }}</button>
    </span>
    <span v-if="!canViewPublished" class="page-publication-short" aria-hidden="true">{{ t.pubState.draft }}</span>
    <Button v-if="viewingPublished" size="sm" variant="default" type="button" aria-keyshortcuts="Escape" data-publication-back @click="viewingPublished = false">{{ t.pubState.backToDraft }}</Button>
  </span>
</template>

<style scoped>
.page-publication-state {
  display: inline-flex; align-items: center; gap: 8px; min-width: 0;
  color: var(--accent); font-size: 12.5px; font-weight: 500; white-space: nowrap;
  animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both;
}
.page-publication-state[data-publication-state="published-view"] { color: var(--text); }
.page-publication-state > svg { flex: none; }
.page-publication-hint { color: var(--muted); font-weight: 400; }
.page-publication-short { display: none; }
/* The segmented switch of the history views (HistoryFormatSwitch), on the bar's own surface. */
.page-publication-switch { --thumb-inset: 2px; --thumb-radius: calc(6px - 1px - 2px); --thumb-shadow: 0 1px 3px color-mix(in srgb, var(--text) 8%, transparent); display: inline-flex; gap: 2px; padding: 2px; border: 1px solid var(--line); border-radius: 6px; background: var(--sidebar); }
.page-publication-switch button { padding: 2px 8px; border: 0; border-radius: calc(6px - 1px - 2px); background: transparent; color: var(--muted); font: inherit; font-size: 12px; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
.page-publication-switch button[aria-pressed="true"] { background: var(--surface); color: var(--text); box-shadow: var(--thumb-shadow); }
.page-publication-switch[data-thumb] button[aria-pressed="true"] { background: transparent; box-shadow: none; }
@media (hover: hover) and (pointer: fine) { .page-publication-switch button:hover:not([aria-pressed="true"]) { color: var(--text); } }
.page-publication-switch button:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
/* Where room runs out the words leave the sight only: a screen reader still reads them, the tooltip still shows them. */
@container workspace (max-width: 1100px) {
  .page-publication-hint { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
}
/* Beside the switch the sentence repeats what the switch and the tooltip say: it goes first, before the page's name in the trail does
   (4.0: at 1280 px of window the bar's actions took 875 px and left the trail 66). */
@container workspace (max-width: 1280px) {
  .page-publication-state:has(.page-publication-switch) .page-publication-full { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
}
@media (max-width: 680px) {
  .page-publication-full { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  .page-publication-short { display: inline; }
}
</style>
