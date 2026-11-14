<script setup lang="ts">
import { computed, nextTick, ref } from "vue";
import t from "../../../i18n/settings-space";
import { flashRow } from "../../../motion/aiMotion";
import { ApiError } from "../../../api";
import { spaceAiApi, type SpaceAiApi, type SpaceAiSettings } from "../../../aiPreferences";
import { Switch } from "../../ui/switch";
import ConflictNote from "../ConflictNote.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import ImpactPreview from "../ImpactPreview.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SaveBar from "../SaveBar.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsDocument } from "../useSettingsDocument";
import type { ImpactItem } from "../types";

/**
 * AI a znalosti (G5): whether the pages of this space take part in the AI features: related pages, search by meaning
 * and suggestions, and the answers of the assistant (phase 3). Ordinary search is not affected. The server answers 403 or 404 to
 * everyone who is not an administrator of the space, and then the section does not exist. A switch with an impact
 * preview, saved explicitly (SaveBar, CAS). The Nenalezené dotazy section is separate: it works without AI.
 */
const props = withDefaults(defineProps<{ spaceId: string; api?: SpaceAiApi; ai?: boolean; mcp?: boolean }>(), { api: () => spaceAiApi, ai: true, mcp: false });

const included = ref(true);
const row = ref<HTMLElement>();
const { saved, loading, loadError, loadFailure, saving, saveError, conflict, dirty, load, save: saveDocument, reset: resetDocument, reloadAfterConflict } = useSettingsDocument<SpaceAiSettings>({
  load: (signal) => props.api.load(props.spaceId, signal),
  save: (current) => props.api.save(props.spaceId, included.value, current.version),
  fill: (next) => { included.value = next.included; },
  dirty: (next) => included.value !== next.included,
  onSaved: () => { void nextTick(() => { if (row.value) flashRow(row.value); }); },
  clearConflictOnSave: true,
  quietConflict: true,
});
// A 403 or 404 means that the section does not exist, so the page says that instead of the load error.
const unavailable = computed(() => loadFailure.value instanceof ApiError && [403, 404].includes(loadFailure.value.status));

// Only what is actually on is named: with AI off and MCP on there are no AI features to talk about.
const target = computed(() => (props.ai && props.mcp ? t.ai.both : props.mcp ? t.ai.mcpOnly : t.ai.aiOnly));
const excludeImpact = computed<ImpactItem[]>(() => [
  ...(props.ai
    ? [
        { icon: "link", label: t.ai.relatedPages },
        { icon: "search", label: t.ai.semantic, detail: t.ai.semanticDetail },
        { icon: "lightbulb", label: t.ai.suggestions },
        { icon: "quote", label: t.ai.answers, detail: t.ai.answersDetail },
      ]
    : []),
  ...(props.mcp ? [{ icon: "plug", label: t.ai.mcp, detail: t.ai.mcpDetail }] : []),
]);
const excludeUnchanged = t.ai.unchanged;

function reset() {
  if (!saving.value) resetDocument();
}
function save() {
  if (dirty.value) return saveDocument();
}
useDraftGuard(dirty, { save, discard: reset });
void load();
</script>

<template>
  <EmptyState v-if="unavailable" icon="compass" :title="t.missing.title" :text="t.missing.text" />
  <SettingsPage v-else :title="t.ai.title" icon="ai" :description="t.ai.description(ai && mcp ? 'both' : mcp ? 'mcp' : 'ai')">
    <div class="space-ai" role="region" :aria-label="t.ai.region">
      <ConflictNote v-if="conflict" :text="t.ai.conflict" :reload-label="t.ai.reload" :busy="loading || saving" data-space-ai-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
      <ErrorState v-if="loadError && !saved" :title="t.ai.loadFailed" :text="loadError" :busy="loading" @retry="load" />
      <LoadingSkeleton v-else-if="loading && !saved" :label="t.ai.loading" />
      <template v-else-if="saved">
        <SettingsSection :title="mcp && !ai ? t.ai.sectionMcp : t.ai.sectionAi" icon="book-open">
          <div ref="row">
          <SettingsRow v-slot="{ id, describedBy }" :label="t.ai.include(target)" :help="ai ? t.ai.helpAi : t.ai.helpMcp">
            <Switch :id="id" v-model="included" :aria-describedby="describedBy" :disabled="saving" data-space-ai-included />
          </SettingsRow>
          </div>
          <InlineReveal :open="!included">
            <ImpactPreview :title="t.ai.impactTitle" :items="excludeImpact" :unchanged="excludeUnchanged" data-space-ai-impact />
          </InlineReveal>
        </SettingsSection>
        <slot v-if="saved.included && ai" />
        <SaveBar :dirty="dirty" :saving="saving" :error="saveError || undefined" :save-label="t.ai.save" @save="save" @cancel="reset" />
      </template>
    </div>
  </SettingsPage>
</template>

<style scoped>
.space-ai { display: grid; gap: 16px; min-width: 0; }
</style>
