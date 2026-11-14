<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useAiAdminHealth } from "./useAiAdminHealth";
import AiReportsSection from "./AiReportsSection.vue";
import AtlasIcon from "../../../AtlasIcon.vue";
import { Switch } from "../../../ui/switch";
import { ApiError, api, errorMessage, put } from "../../../../api";
import { formatAgo, formatDateTime } from "../../../../formatDate";
import { formatNumber } from "../../../../i18n";
import t from "../../../../i18n/settings-ai";
import { meterFill } from "../../../../motion/aiMotion";
import ConfirmDialog from "../../ConfirmDialog.vue";
import { disclosureHooks } from "../../disclosure";
import ConflictNote from "../../ConflictNote.vue";
import ErrorState from "../../ErrorState.vue";
import ImpactPreview from "../../ImpactPreview.vue";
import LoadingNote from "../../LoadingNote.vue";
import SaveBar from "../../SaveBar.vue";
import SettingsRow from "../../SettingsRow.vue";
import SettingsSection from "../../SettingsSection.vue";
import StatTile from "../../StatTile.vue";
import StatusBadge from "../../StatusBadge.vue";
import { useDraftGuard } from "../../dirtyGuard";
import { useSettingsRoute } from "../../useSettingsRoute";
import { kindIcons, kindLabels, pagesText, profileStateView, type AiSwitchView, type ProfileKind } from "./aiAdmin";

/**
 * Přehled: the master switch (own SaveBar, CAS, asks before switching AI off) and the state of the
 * profiles, the index and seven days of use. The state comes from the A3 overview (the index tile
 * from V2): against a server without it the switch still works and the tiles say the overview is not
 * available yet. Errors are shown as Czech text by code; the server never sends upstream text and
 * this view would not show it. Refreshes every 15 s while the tab is visible.
 */
const props = defineProps<{ guided?: boolean }>();
const emit = defineEmits<{ saved: [] }>();
const router = useSettingsRoute();
const sw = ref<AiSwitchView>();
const enabled = ref(false);
const { overview, missing: overviewMissing, error: overviewError, refresh: loadOverview } = useAiAdminHealth();
const loading = ref(true);
const loadError = ref("");
const saving = ref(false);
const saveError = ref("");
const conflict = ref(false);
const confirmOff = ref(false);
const meter = ref<HTMLElement>();
let disposed = false;

const dirty = computed(() => !!sw.value && enabled.value !== sw.value.enabled);
const profile = (kind: ProfileKind) => overview.value?.profiles.find((item) => item.kind === kind);
const states = computed(() => (["embeddings", "quick", "chat", "rerank"] as ProfileKind[]).map((kind) => ({ kind, view: profileStateView(profile(kind)), model: profile(kind)?.model ?? null, location: profile(kind)?.location })));
const index = computed(() => overview.value?.index ?? undefined);
const completeness = computed(() => (index.value && index.value.eligiblePages > 0 ? Math.round((index.value.indexedPages / index.value.eligiblePages) * 100) : 0));
const tabHref = (part: string) => router.href(`general/modules/ai/${part}`);

function reset() {
  if (sw.value) enabled.value = sw.value.enabled;
  saveError.value = "";
}
async function loadSwitch() {
  const result = await api<AiSwitchView>("/admin/ai/switch");
  if (disposed) return;
  sw.value = result;
  reset();
}
async function load() {
  loading.value = true;
  loadError.value = "";
  try {
    await Promise.all([loadSwitch(), loadOverview()]);
  } catch (failure) {
    if (!disposed) loadError.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
function reloadAfterConflict() {
  conflict.value = false;
  void load();
}
/** Switching off asks first; switching on saves at once from the bar. */
function requestSave() {
  if (!sw.value || saving.value) return;
  if (!enabled.value && sw.value.enabled) confirmOff.value = true;
  else void save();
}
async function save() {
  const current = sw.value;
  if (!current || saving.value) return;
  saving.value = true;
  saveError.value = "";
  try {
    const result = await put<AiSwitchView>("/admin/ai/switch", { enabled: enabled.value, expectedVersion: current.version });
    if (disposed) return;
    sw.value = result;
    reset();
    confirmOff.value = false;
    void loadOverview();
    emit("saved");
  } catch (failure) {
    if (disposed) return;
    confirmOff.value = false;
    saveError.value = errorMessage(failure);
    conflict.value = failure instanceof ApiError && failure.status === 409;
  } finally {
    saving.value = false;
  }
}
watch(index, async (value, before) => {
  if (!value || before) return;
  await nextTick();
  if (meter.value) meterFill([meter.value]);
});

useDraftGuard(dirty, { save: requestSave, discard: reset });
onMounted(() => {
  void load();
});
onBeforeUnmount(() => {
  disposed = true;
});
</script>

<template>
  <div class="ai-overview" role="region" :aria-label="t.overview.region" data-ai-overview>
    <ConflictNote v-if="conflict" :text="t.overview.conflict" :reload-label="t.overview.reload" :busy="loading || saving" data-ai-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="t.overview.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !sw" :label="t.overview.loading" />
    <template v-else-if="sw">
      <SettingsSection :title="t.overview.switchTitle" icon="sliders-horizontal" :description="t.overview.switchDescription">
        <template #actions><StatusBadge :tone="sw.enabled ? 'success' : 'neutral'" :icon="sw.enabled ? 'circle-check' : 'ban'" :label="sw.enabled ? t.states.on : t.states.off" size="sm" data-ai-switch-state /></template>
        <SettingsRow v-slot="{ id, describedBy }" :label="t.overview.useLabel" :help="t.overview.useHelp">
          <Switch :id="id" v-model="enabled" :aria-describedby="describedBy" :disabled="saving" data-ai-switch />
        </SettingsRow>
        <!-- Flipping the switch folds one preview away while the other unfolds, so the card changes height smoothly. -->
        <Transition :css="false" v-bind="disclosureHooks">
        <ImpactPreview v-if="!guided && !enabled" :title="t.overview.offTitle" :items="[{ icon: 'ban', label: t.overview.offLabel, detail: t.overview.offDetail }]" :unchanged="t.overview.offUnchanged" data-ai-switch-impact />
        <ImpactPreview v-else-if="!guided" :title="t.overview.onTitle" :items="[{ icon: 'circle-check', label: t.overview.onLabel, detail: t.overview.onDetail, tone: 'success' }]" />
        </Transition>
      </SettingsSection>

      <SettingsSection v-if="!guided" :title="t.overview.stateTitle" icon="activity" :description="t.overview.stateDescription">
        <p v-if="overviewMissing" class="st-note" role="status" data-ai-overview-missing><AtlasIcon name="info" :size="15" />{{ t.overview.missing }}</p>
        <p v-else-if="overviewError" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ t.overview.refreshFailed(overviewError) }}</p>
        <div v-if="overview" class="ai-tiles">
          <StatTile v-for="item in states" :key="item.kind" :label="kindLabels[item.kind]" :value="item.view.label" :value-icon="item.view.icon" :tone="item.view.tone" :icon="kindIcons[item.kind]" :href="tabHref(item.kind)" :data-ai-tile="item.kind">
            <span v-if="item.model" class="ai-tile-line">{{ item.model }}<template v-if="item.location"> · {{ item.location === "cloud" ? t.summary.cloud : t.summary.company }}</template></span>
            <span v-if="item.view.error" class="ai-tile-line is-error" data-ai-tile-error>{{ t.overview.lastError }} {{ item.view.error.text }} <code class="st-mono">{{ item.view.error.code }}</code></span>
            <span v-if="item.view.since && item.view.tone === 'success'" class="ai-tile-line">{{ t.models.since(item.view.since) }}</span>
            <span v-if="item.view.lastSuccess" class="ai-tile-line">{{ t.overview.lastSuccess(item.view.lastSuccess) }}</span>
          </StatTile>

          <StatTile :label="t.overview.index" :value="index?.state === 'Building' ? t.overview.building : index?.state === 'Active' ? t.overview.active : t.overview.notBuilt" :value-icon="index?.state === 'Active' ? 'circle-check' : index?.state === 'Building' ? 'hourglass' : 'clock'" :tone="index?.state === 'Active' ? 'success' : index?.state === 'Building' ? 'info' : 'neutral'" icon="database" :href="tabHref('index')" data-ai-tile="index">
            <template v-if="index && index.state !== 'None'">
              <span class="ai-tile-line">{{ index.model }}</span>
              <span class="ai-tile-line">{{ t.overview.ofPages(formatNumber(index.indexedPages), pagesText(index.eligiblePages), completeness) }}</span>
              <span class="ai-meter" aria-hidden="true"><span ref="meter" class="ai-meter-bar" :style="{ width: `${completeness}%` }" /></span>
              <span class="ai-tile-line" data-ai-queue>{{ t.overview.queue(index.queue.pending, index.queue.failing) }}<template v-if="index.queue.oldestPendingAt"> · {{ t.overview.oldest(formatAgo(index.queue.oldestPendingAt)) }}</template></span>
              <StatusBadge v-if="index.rebuildRecommended" tone="warning" icon="refresh" :label="t.overview.rebuildRecommended" size="sm" data-ai-rebuild />
            </template>
            <span v-else class="ai-tile-line">{{ t.overview.noIndex }}</span>
          </StatTile>
        </div>

        <p v-if="overview" class="st-note"><AtlasIcon name="clock" :size="15" />{{ t.overview.asOf(formatDateTime(new Date())) }}</p>
      </SettingsSection>

      <AiReportsSection v-if="!guided" kind="usage" compact />
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.overview.save" @save="requestSave" @cancel="reset" />
      <ConfirmDialog :open="confirmOff" :title="t.overview.confirmTitle" :confirm-label="t.overview.confirmLabel" confirm-icon="ban" :busy="saving" data-ai-confirm-off @confirm="save" @cancel="confirmOff = false">
        <p>{{ t.overview.confirmText }}</p>
      </ConfirmDialog>
    </template>
  </div>
</template>

<style scoped>
.ai-overview { display: grid; gap: 16px; min-width: 0; }
.ai-tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 230px), 1fr)); gap: 12px; }
/* The tile's context is a wrapping flex row: every line takes a row of its own (on a wide tile two short lines sat side
   by side), and the meter, which has no content of its own, would otherwise shrink to nothing. */
.ai-tile-line { display: block; flex: 1 0 100%; min-width: 0; overflow-wrap: anywhere; }
.ai-tile-line.is-error { color: var(--error); }
.ai-meter { display: block; flex: 1 0 100%; height: 6px; margin: 2px 0; overflow: hidden; border-radius: 999px; background: var(--hover); }
.ai-meter-bar { display: block; height: 100%; border-radius: inherit; background: var(--accent); transition: width var(--atlas-motion-slow) var(--atlas-ease-out); }
</style>
