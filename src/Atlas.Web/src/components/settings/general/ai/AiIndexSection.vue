<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../../AtlasIcon.vue";
import { Button } from "../../../ui/button";
import { api, errorMessage } from "../../../../api";
import { formatDateTime } from "../../../../formatDate";
import EmptyState from "../../EmptyState.vue";
import ErrorState from "../../ErrorState.vue";
import FlowProgress from "../../FlowProgress.vue";
import ImpactPreview from "../../ImpactPreview.vue";
import LoadingNote from "../../LoadingNote.vue";
import ResultBanner from "../../ResultBanner.vue";
import SettingsList from "../../SettingsList.vue";
import SettingsListRow from "../../SettingsListRow.vue";
import SettingsSection from "../../SettingsSection.vue";
import StatusBadge from "../../StatusBadge.vue";
import { useSettingsRoute } from "../../useSettingsRoute";
import { httpIndexClient, type AiIndexClient } from "./aiAdminClient";
import AiIndexFlow from "./AiIndexFlow.vue";
import type { SavedAiProfile } from "./aiSavedProfiles";
import { formatNumber } from "../../../../i18n";
import t from "../../../../i18n/settings-ai";
import { isIndexBuildComplete, isMissingEndpoint, pagesText, type AiProfileView, type GenerationMode, type GenerationState, type GenerationView, type IndexStatus } from "./aiAdmin";

/**
 * Index: the generations of the knowledge index and the actions that start one. Postavit index (the
 * first one), Přestavět (same profile, new generation) and Vyměnit model… (a new profile) all open
 * `AiIndexFlow`, the StepFlow of the generation swap; an existing Building generation reopens it at
 * Stavba or, once complete, Ověření. `?action=replace` (the link from a locked Embeddingy profile)
 * opens the model swap at once.
 * The index endpoints are V2, MOCKED until served: without them the section says so.
 */
const props = withDefaults(defineProps<{ guided?: boolean; client?: AiIndexClient; profileLoader?: () => Promise<AiProfileView> }>(), {
  client: () => httpIndexClient,
  profileLoader: () => api<AiProfileView>("/admin/ai/profiles/embeddings"),
});
const emit = defineEmits<{ changed: [] }>();
const router = useSettingsRoute();
const status = ref<IndexStatus>();
const profile = ref<AiProfileView>();
const savedProfile = ref<SavedAiProfile>();
const savedActiveVersion = ref<string | null>();
const missing = ref(false);
const loading = ref(true);
const loadError = ref("");
const selectionError = ref("");
const flow = ref<GenerationMode>();
const result = ref("");
let disposed = false;
/** The flow opens by itself only when the page is first entered; leaving it must not pull the user back. */
let entered = false;

const stateTones: Record<GenerationState, { tone: "success" | "info" | "neutral" | "danger"; icon: string; label: string }> = {
  Active: { tone: "success", icon: "circle-check", label: t.overview.active },
  Building: { tone: "info", icon: "hourglass", label: t.overview.building },
  Retired: { tone: "neutral", icon: "clock", label: t.index.retired },
  Failed: { tone: "danger", icon: "circle-x", label: t.index.failed },
};
const active = computed(() => status.value?.active ?? null);
const building = computed(() => status.value?.building ?? null);
const buildingComplete = computed(() => isIndexBuildComplete(building.value));
const buildingTone = computed(() => buildingComplete.value
  ? { tone: "success" as const, icon: "list-checks", label: t.index.readyToVerify }
  : stateTones.Building);
const profileReady = computed(() => !!profile.value?.configured && !!profile.value.enabled);
const phases = computed(() => {
  const target = building.value;
  const done = (value: boolean) => (value ? "done" : "todo") as "done" | "todo";
  const complete = buildingComplete.value;
  return [
    { id: "profile", label: t.index.phaseProfile, detail: target ? t.index.chosen : t.index.waiting, state: done(!!target), available: false },
    { id: "build", label: t.index.phaseBuild, detail: target ? t.index.ofTotal(formatNumber(target.indexedPages), formatNumber(target.eligiblePages)) : "—", state: target ? (complete ? "done" : "current") : "todo", available: false },
    { id: "verify", label: t.index.phaseVerify, detail: complete ? t.index.waitingForVerify : "—", state: complete ? "current" : "todo", available: false },
    { id: "activate", label: t.index.phaseActivate, detail: "—", state: "todo" as const, available: false },
  ] as const;
});
const percentOf = (generation: GenerationView) => (generation.eligiblePages ? Math.round((generation.indexedPages / generation.eligiblePages) * 100) : 0);

async function load() {
  loading.value = true;
  loadError.value = "";
  selectionError.value = "";
  savedProfile.value = undefined;
  try {
    const [index, view] = await Promise.all([props.client.status(), props.profileLoader()]);
    if (disposed) return;
    status.value = index;
    profile.value = view;
    missing.value = false;
    const query = router.route.value.query;
    if (!index.building && query.action === "replace" && query.savedProfileId) {
      try {
        const selected = await api<SavedAiProfile>(`/admin/ai/profiles/embeddings/saved/${encodeURIComponent(query.savedProfileId)}`);
        if (disposed) return;
        if (!query.savedVersion || selected.version !== query.savedVersion) throw new Error(t.index.savedChanged);
        savedProfile.value = selected;
        savedActiveVersion.value = query.activeVersion || null;
      } catch (failure) {
        if (!disposed) selectionError.value = isMissingEndpoint(failure)
          ? t.index.savedGone
          : errorMessage(failure);
        return;
      }
    }
    openRequested();
  } catch (failure) {
    if (disposed) return;
    if (isMissingEndpoint(failure)) missing.value = true;
    else loadError.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
/** An existing build always reopens the flow; `?action=replace` opens the model swap. */
function openRequested() {
  if (flow.value || entered) return;
  entered = true;
  if (building.value) flow.value = "build";
  else if (router.route.value.query.action === "replace" && active.value && profileReady.value) flow.value = "replace";
}
function open(mode: GenerationMode) {
  result.value = "";
  flow.value = mode;
}
async function finished(message: string) {
  flow.value = undefined;
  result.value = message;
  if (router.route.value.query.action) router.setQuery({});
  await load();
  emit("changed");
}
function left() {
  flow.value = undefined;
  if (router.route.value.query.action) router.setQuery({});
  void load();
}

onMounted(load);
onBeforeUnmount(() => (disposed = true));
</script>

<template>
  <div class="ai-index" role="region" :aria-label="t.module.tabs.index" data-ai-index>
    <ErrorState v-if="selectionError" :title="t.index.selectionFailed" :text="selectionError" :busy="loading" data-ai-saved-selection-error @retry="load">
      <a class="st-link" :href="router.href('general/modules/ai/embeddings')">{{ t.index.selectProfile }}</a>
    </ErrorState>
    <ResultBanner v-if="result" :message="result" data-ai-index-result @dismiss="result = ''" />
    <ErrorState v-if="loadError" :title="t.index.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !status && !missing" :label="t.index.loading" />
    <EmptyState v-else-if="missing" icon="database" :title="t.index.missingTitle" :text="t.index.missingText" data-ai-index-missing />

    <template v-else-if="status && profile">
      <!-- The build flow and the overview swap like the tab parts of the module: the old one leaves, the new one rises in. -->
      <Transition name="st-step" mode="out-in">
      <SettingsSection v-if="flow" key="flow" :title="flow === 'build' ? t.index.flowBuild : flow === 'rebuild' ? t.index.flowRebuild : t.index.flowReplace" icon="database" :description="t.index.flowDescription">
        <AiIndexFlow :mode="flow" :profile="profile" :saved-profile="savedProfile" :saved-active-version="savedActiveVersion" :building="building" :client="client" @done="finished" @leave="left" />
      </SettingsSection>

      <div v-else key="overview" class="ai-index-overview">
        <SettingsSection :title="t.index.activeTitle" icon="database" :description="t.index.activeDescription">
          <template #actions>
            <StatusBadge v-if="active" v-bind="stateTones.Active" size="sm" data-ai-generation-state="Active" />
            <StatusBadge v-else tone="neutral" icon="clock" :label="t.index.noActive" size="sm" data-ai-generation-state="none" />
          </template>
          <template v-if="active">
            <dl class="ai-facts">
              <div><dt>{{ t.summary.model }}</dt><dd>{{ active.model }}<template v-if="active.modelVersion"> · {{ active.modelVersion }}</template></dd></div>
              <div><dt>{{ t.index.pages }}</dt><dd>{{ t.index.pagesOf(formatNumber(active.indexedPages), pagesText(active.eligiblePages), percentOf(active)) }}</dd></div>
              <div><dt>{{ t.index.passages }}</dt><dd>{{ formatNumber(active.chunks) }}</dd></div>
              <div v-if="active.activatedAt"><dt>{{ t.index.activeSince }}</dt><dd>{{ formatDateTime(active.activatedAt) }}</dd></div>
              <div><dt>{{ t.index.chunking }}</dt><dd class="st-mono">{{ active.chunkingVersion }}</dd></div>
            </dl>
            <div v-if="active.missing.length" class="ai-missing" data-ai-missing>
              <p class="st-note"><AtlasIcon name="triangle-alert" :size="15" />{{ t.index.notIndexed(pagesText(active.missing.length)) }}<template v-if="active.eligiblePages - active.indexedPages > active.missing.length">{{ " " + t.index.shownFirst(active.missing.length) }}</template>.</p>
              <ul><li v-for="page in active.missing.slice(0, 20)" :key="page.pageId"><a class="st-link" :href="`#/pages/${page.pageId}`">{{ page.title }}</a></li></ul>
            </div>
            <p v-if="status.queue.pending || status.queue.failing" class="st-note" data-ai-queue><AtlasIcon name="clock" :size="15" />{{ t.overview.queue(status.queue.pending, status.queue.failing) }}<template v-if="status.queue.oldestPendingAt"> · {{ t.index.oldestSince(formatDateTime(status.queue.oldestPendingAt)) }}</template>. {{ t.index.catchUp }}</p>
          </template>
          <p v-else class="st-note"><AtlasIcon name="info" :size="15" />{{ t.index.noIndexNote }}</p>

          <div class="st-actions">
            <template v-if="guided && active">
              <Button as-child variant="outline"><a :href="router.href('general/modules/ai/index')" data-ai-manage-index>{{ t.index.manage }}</a></Button>
            </template>
            <template v-else-if="building">
              <Button type="button" data-ai-resume @click="open('build')"><AtlasIcon name="arrow-right" :size="16" />{{ buildingComplete ? t.index.continueVerify : t.index.continueBuild }}</Button>
            </template>
            <template v-else-if="active">
              <Button type="button" variant="outline" data-ai-rebuild-action :disabled="!profileReady" @click="open('rebuild')"><AtlasIcon name="refresh" :size="16" />{{ t.index.rebuild }}</Button>
              <Button type="button" variant="outline" data-ai-replace-action :disabled="!profileReady" @click="open('replace')"><AtlasIcon name="arrow-right-left" :size="16" />{{ t.profile.swapModel }}</Button>
            </template>
            <Button v-else type="button" data-ai-build-action :disabled="!profileReady" @click="open('build')"><AtlasIcon name="database" :size="16" />{{ t.index.build }}</Button>
            <span v-if="!profileReady" class="st-note"><AtlasIcon name="info" :size="15" /><span>{{ t.index.firstBefore }}<a class="st-link" :href="router.href('general/modules/ai/embeddings')">{{ t.kinds.embeddings }}</a>{{ t.index.firstAfter }}</span></span>
          </div>
        </SettingsSection>

        <SettingsSection v-if="building" :title="buildingComplete ? t.index.completedGeneration : t.index.buildingGeneration" :icon="buildingComplete ? 'list-checks' : 'hourglass'" :description="buildingComplete ? t.index.completedDescription(building.model) : t.index.buildingDescription(building.model)" data-ai-building>
          <template #actions><StatusBadge v-bind="buildingTone" size="sm" data-ai-generation-state="Building" /></template>
          <FlowProgress :items="[...phases]" :label="t.index.swapProgress" />
          <p class="ai-figures"><strong>{{ formatNumber(building.indexedPages) }}</strong> {{ t.index.ofPagesAfter(pagesText(building.eligiblePages), percentOf(building)) }}</p>
        </SettingsSection>

        <SettingsSection v-if="status.retired.length" :title="t.index.retiredTitle" icon="clock" :description="t.index.retiredDescription">
          <SettingsList :label="t.index.retiredTitle">
            <SettingsListRow v-for="item in status.retired" :key="item.id" :title="item.model" :description="t.index.retiredItem(formatNumber(item.indexedPages), item.chunkingVersion)" icon="database">
              <template #meta><StatusBadge v-bind="stateTones[item.state]" size="sm" /></template>
            </SettingsListRow>
          </SettingsList>
        </SettingsSection>

        <ImpactPreview v-if="!guided && !building" :title="t.index.howTitle" :items="[{ icon: 'sliders-horizontal', label: t.index.howProfile }, { icon: 'database', label: t.index.howBuild }, { icon: 'list-checks', label: t.index.howVerify }, { icon: 'circle-check', label: t.index.howActivate, tone: 'success' }]" />
      </div>
      </Transition>
    </template>
  </div>
</template>

<style scoped>
/* The index page is a column of flex items, not a grid: the result banner unfolds and gives the gap above it back (settings/disclosure.ts). */
.ai-index { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
.ai-index-overview { display: grid; gap: 16px; min-width: 0; }
.ai-facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 12px 20px; margin: 0; }
.ai-facts > div { display: grid; gap: 2px; min-width: 0; }
.ai-facts dt { color: var(--muted); font-size: 12px; }
.ai-facts dd { margin: 0; font-size: 14px; overflow-wrap: anywhere; font-variant-numeric: tabular-nums; }
.ai-missing { display: grid; gap: 4px; }
.ai-missing ul { display: flex; flex-wrap: wrap; gap: 4px 14px; margin: 0; padding-left: 22px; font-size: 13px; }
.ai-figures { margin: 0; font-size: 14px; font-variant-numeric: tabular-nums; }
</style>
