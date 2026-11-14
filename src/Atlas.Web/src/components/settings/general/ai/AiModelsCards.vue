<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../../../AtlasIcon.vue";
import StatusBadge from "../../StatusBadge.vue";
import { useSettingsRoute } from "../../useSettingsRoute";
import t from "../../../../i18n/settings-ai";
import { kindIcons, kindLabels, profileStateView, type AiProfileStatus, type ProfileKind } from "./aiAdmin";

/**
 * Modely (refresh 2.5, board 08): beside the opened model, all three in one card with what is saved for each and a link
 * to the others, and what a missing embedding model means. Read from the AI overview; it never probes a model.
 */
const props = defineProps<{ current: ProfileKind; profiles?: readonly AiProfileStatus[] }>();
const router = useSettingsRoute();
const uses: Record<ProfileKind, string> = t.models.uses;
const order: ProfileKind[] = ["chat", "quick", "embeddings", "rerank"];
const rows = computed(() => order.map((kind) => {
  const status = props.profiles?.find((profile) => profile.kind === kind);
  const view = profileStateView(status);
  return {
    kind,
    view,
    stateLabel: status?.state === "Unavailable" ? t.states.unavailable : view.label,
    unavailableSince: status?.state === "Unavailable" ? view.since : undefined,
    useChatProfile: status?.useChatProfile,
    model: status?.model ?? null,
    location: status?.location === "cloud" ? "cloud" : status?.location ? "company" : null,
    href: router.href(`general/modules/ai/${kind}`),
  };
}));
const embeddingsMissing = computed(() => {
  const status = props.profiles?.find((profile) => profile.kind === "embeddings");
  return !!status && status.state === "NotConfigured";
});
</script>

<template>
  <aside class="ai-models-aside" :aria-label="t.models.region" data-ai-models-summary>
    <div class="ai-models-card">
      <p class="ai-models-title">{{ t.models.title }}</p>
      <a v-for="row in rows" :key="row.kind" class="ai-models-row" :href="row.href" :aria-current="row.kind === props.current ? 'page' : undefined" :data-ai-model-summary="row.kind">
        <span class="st-icon-tile ai-models-icon" :data-tone="row.kind === props.current ? undefined : 'neutral'"><AtlasIcon :name="kindIcons[row.kind]" :size="15" /></span>
        <span class="ai-models-text">
          <span class="ai-models-name">{{ kindLabels[row.kind] }}</span>
          <span class="ai-models-value">{{ row.model || uses[row.kind] }}</span>
          <span v-if="row.useChatProfile" class="ai-models-detail">{{ t.models.usesChat }}</span>
          <span v-else-if="row.location" class="ai-models-detail">{{ row.location === 'cloud' ? t.models.cloudModel : t.models.companyModel }}</span>
          <span class="ai-models-status"><StatusBadge :tone="row.view.tone" :icon="row.view.icon" :label="row.stateLabel" size="sm" /></span>
          <span v-if="row.unavailableSince" class="ai-models-detail">{{ t.models.since(row.unavailableSince) }}</span>
        </span>
      </a>
    </div>
    <!-- The overview arrives after the card: the note rises in (and leaves after a save) instead of appearing in one frame. -->
    <Transition name="st-step">
      <p v-if="embeddingsMissing" class="ai-models-warning" role="note"><AtlasIcon name="triangle-alert" :size="16" />{{ t.models.embeddingsMissing }}</p>
    </Transition>
  </aside>
</template>

<style scoped>
/* Sticks below the workspace bar (sticky, 52 px), not under it: at top: 12px the card's title scrolled out of sight. */
.ai-models-aside { display: grid; gap: 12px; min-width: 0; position: sticky; top: calc(var(--atlas-topbar-height, 52px) + 12px); }
/* The same 12 px on the sides and the bottom, so the outlined open model keeps an even margin when it is the last row. */
.ai-models-card { display: grid; gap: 2px; padding: 14px 12px 12px; border: 1px solid var(--st-line-soft, var(--line)); border-radius: var(--st-radius); background: var(--st-subtle, var(--hover)); }
.ai-models-title { margin: 0 4px 6px; font-weight: 600; }
.ai-models-row { position: relative; display: flex; align-items: center; gap: 10px; min-width: 0; padding: 8px 6px; border-radius: 8px; color: var(--text); text-decoration: none; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
/* The line between two rows is a hairline in the gap above the row, not a border-top: a border on a rounded box curls down at both ends. None next to the open model, which has its own outline. */
.ai-models-row + .ai-models-row::before { content: ""; position: absolute; top: -2px; right: 6px; left: 6px; height: 1px; background: var(--st-line-soft, var(--line)); }
.ai-models-row[aria-current="page"]::before, .ai-models-row[aria-current="page"] + .ai-models-row::before { display: none; }
@media (hover: hover) and (pointer: fine) {
  .ai-models-row:hover { background: color-mix(in srgb, var(--surface) 70%, transparent); }
}
.ai-models-row[aria-current="page"] { background: var(--surface); box-shadow: 0 0 0 1px var(--st-line-soft, var(--line)); }
.ai-models-row:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
.ai-models-icon { width: 30px; height: 30px; border-radius: 9px; flex-shrink: 0; align-self: start; }
.ai-models-text { display: grid; gap: 3px; flex: 1; min-width: 0; }
.ai-models-name { font-size: 13.5px; font-weight: 600; }
.ai-models-value { color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }
.ai-models-detail { color: var(--muted); font-size: 12px; }
.ai-models-status { margin-top: 3px; }
.ai-models-warning { display: flex; gap: 10px; margin: 0; padding: 12px 14px; border: 1px solid color-mix(in srgb, var(--warning) 28%, var(--line)); border-radius: 12px; background: color-mix(in srgb, var(--warning-bg) 55%, var(--surface)); font-size: 13px; line-height: 1.5; }
.ai-models-warning > svg { flex-shrink: 0; margin-top: 2px; color: var(--warning); }
</style>
