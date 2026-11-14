<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from "vue";
import AtlasIcon from "../../../AtlasIcon.vue";
import { Button } from "../../../ui/button";
import { ApiError, errorMessage } from "../../../../api";
import { meterFill, popStatus } from "../../../../motion/aiMotion";
import ConfirmDialog from "../../ConfirmDialog.vue";
import ConflictNote from "../../ConflictNote.vue";
import LiveUpdateNote from "../../LiveUpdateNote.vue";
import { jobsTopic, useLiveFeed, type JobRunChange } from "../../../../live/liveFeed";
import ImpactPreview from "../../ImpactPreview.vue";
import StatusBadge from "../../StatusBadge.vue";
import StepFlow from "../../StepFlow.vue";
import SummaryList from "../../SummaryList.vue";
import type { FlowStep } from "../../types";
import { httpIndexClient, type AiIndexClient } from "./aiAdminClient";
import { disclosureHooks } from "../../disclosure";
import AiProfileFields from "./AiProfileFields.vue";
import type { SavedAiProfile } from "./aiSavedProfiles";
import { formatNumber } from "../../../../i18n";
import t from "../../../../i18n/settings-ai";
import {
  draftFromView, draftToRequest, isIndexBuildComplete, pagesText, profileSummary, validateDraft,
  type AiProfileView, type GenerationMode, type GenerationVerification, type GenerationView, type ProfileDraft,
} from "./aiAdmin";

/**
 * Výměna generace indexu as the settings UI's one wizard (StepFlow): Profil → Stavba → Ověření →
 * Aktivace. StepFlow, because these are separate phases with long asynchronous work between them;
 * inside the Profil step the same `AiProfileFields` as in Embeddingy apply (progressive disclosure,
 * one set of rules). The flow can be left and returned to: an existing `Building` generation opens it
 * at Stavba while work remains, or Ověření when complete. Activation is the only step that changes
 * what search uses, and it needs a passing verification. The index endpoints are V2 (MOCKED until served), reached through `client`.
 */
const props = withDefaults(
  defineProps<{
    mode: GenerationMode;
    savedProfile?: SavedAiProfile;
    savedActiveVersion?: string | null;
    /** The embeddings profile as saved; the new model of `replace` starts from it. */
    profile: AiProfileView;
    /** A generation that is already Building: the flow resumes at Stavba. */
    building?: GenerationView | null;
    client?: AiIndexClient;
  }>(),
  { building: null, client: () => httpIndexClient },
);
const emit = defineEmits<{ done: [message: string]; leave: [] }>();

const current = ref(props.building ? (isIndexBuildComplete(props.building) ? "verify" : "build") : "profile");
const generation = ref<GenerationView | null>(props.building);
const selectedProfile = computed(() => props.savedProfile?.profile ?? props.profile);
const draft = reactive<ProfileDraft>({ ...draftFromView(selectedProfile.value), enabled: true });
const showErrors = ref(false);
const busy = ref(false);
const error = ref("");
const conflict = ref(false);
const verification = ref<GenerationVerification>();
const confirmCancel = ref(false);
const readError = ref("");
const meter = ref<HTMLElement>();
const verdict = ref<HTMLElement>();
let disposed = false;

const replacing = computed(() => props.mode === "replace");
const errors = computed(() => (replacing.value && !props.savedProfile ? validateDraft(draft, props.profile) : {}));
const shownErrors = computed(() => (showErrors.value ? errors.value : {}));
const hasGeneration = computed(() => !!generation.value);
const complete = computed(() => isIndexBuildComplete(generation.value));
const percent = computed(() => (generation.value && generation.value.eligiblePages > 0 ? Math.round((generation.value.indexedPages / generation.value.eligiblePages) * 100) : 0));
const modeLabel = computed(() => t.flow.modes[props.mode]);

const steps = computed<FlowStep[]>(() => [
  { id: "profile", label: t.index.phaseProfile, title: replacing.value && !hasGeneration.value ? t.flow.profileTitleReplace : t.flow.profileTitle, hint: replacing.value ? t.flow.profileHintReplace : t.flow.profileHint, icon: "sliders-horizontal", summary: (replacing.value ? draft.model.trim() : props.profile.model) || undefined, manual: !hasGeneration.value, nextLabel: hasGeneration.value ? t.flow.next : t.flow.startBuild, nextIcon: "arrow-right", validate: validateProfile },
  { id: "build", label: t.index.phaseBuild, title: complete.value ? t.flow.buildDoneTitle : t.flow.buildTitle, hint: complete.value ? t.flow.buildDoneHint : t.flow.buildHint, icon: "database", summary: complete.value ? t.flow.buildSummary(generation.value?.indexedPages ?? 0) : undefined, ready: complete.value },
  { id: "verify", label: t.index.phaseVerify, title: t.flow.verifyTitle, hint: t.flow.verifyHint, icon: "list-checks", summary: verification.value?.canActivate ? t.flow.verified : undefined, ready: !!verification.value?.canActivate },
  { id: "activate", label: t.index.phaseActivate, title: t.flow.activateTitle, icon: "circle-check", manual: true, nextLabel: t.flow.activate, nextIcon: "check" },
]);

function validateProfile() {
  if (!replacing.value || hasGeneration.value) return true;
  if (Object.keys(errors.value).length) {
    showErrors.value = true;
    return false;
  }
  return true;
}
function say(failure: unknown) {
  error.value = errorMessage(failure);
}
async function start() {
  busy.value = true;
  error.value = "";
  try {
    const saved = props.savedProfile && replacing.value ? {
      savedProfileId: props.savedProfile.id, expectedSavedVersion: props.savedProfile.version,
      expectedProfileVersion: props.savedActiveVersion === undefined ? props.profile.version : props.savedActiveVersion,
    } : undefined;
    const result = await props.client.start(props.mode, !saved && replacing.value ? draftToRequest({ ...draft, enabled: true }, { ...props.profile, modelLockedByIndex: false }, "embeddings") : undefined, saved);
    if (disposed) return;
    generation.value = result;
    current.value = "build";
  } catch (failure) {
    if (!disposed) say(failure);
  } finally {
    draft.apiKey = "";
    busy.value = false;
  }
}
async function runVerify() {
  const id = generation.value?.id;
  if (!id) return;
  busy.value = true;
  error.value = "";
  verification.value = undefined;
  try {
    const result = await props.client.verify(id);
    if (disposed) return;
    verification.value = result;
    await nextTick();
    if (verdict.value) popStatus(verdict.value);
  } catch (failure) {
    if (!disposed) say(failure);
  } finally {
    busy.value = false;
  }
}
async function activate() {
  const target = generation.value;
  if (!target) return;
  busy.value = true;
  error.value = "";
  conflict.value = false;
  try {
    await props.client.activate(target.id, target.version);
    if (disposed) return;
    emit("done", replacing.value ? t.flow.activatedReplace : t.flow.activated);
  } catch (failure) {
    if (disposed) return;
    if (failure instanceof ApiError && failure.status === 409) {
      // Someone changed the generation (or it is no longer complete): back to the verification, with the current version.
      conflict.value = true;
      verification.value = undefined;
      current.value = "verify";
      await refresh();
    } else say(failure);
  } finally {
    busy.value = false;
  }
}
function onNext(step: FlowStep) {
  if (step.id === "profile" && !hasGeneration.value) void start();
  else if (step.id === "activate") void activate();
}
async function refresh() {
  try {
    const status = await props.client.status();
    if (disposed) return;
    readError.value = "";
    if (status.building) generation.value = status.building;
    else if (generation.value) {
      // It ended while we looked away: activated or cancelled elsewhere.
      const activated = status.active?.id === generation.value.id;
      emit("done", activated ? t.flow.alreadyActive : t.flow.endedWithoutActivation);
    }
  } catch (failure) {
    if (!disposed) readError.value = errorMessage(failure);
  }
}
async function cancelBuild() {
  const id = generation.value?.id;
  if (!id) return;
  busy.value = true;
  try {
    await props.client.cancel(id);
    if (!disposed) emit("done", t.flow.canceled);
  } catch (failure) {
    if (!disposed) {
      confirmCancel.value = false;
      say(failure);
    }
  } finally {
    busy.value = false;
  }
}
function onCancel() {
  if (hasGeneration.value) confirmCancel.value = true;
  else emit("leave");
}
// The build is followed by the index job's runs on the jobs feed (each round is a step the server tells): nothing polls.
const { unavailable: liveOff, refresh: refreshLive } = useLiveFeed<JobRunChange>(() => (current.value === "build" && generation.value ? jobsTopic : undefined), refresh,
  (change) => change.jobId === "ai-index.index");
watch(percent, async (value, before) => {
  if (value === before || !meter.value) return;
  await nextTick();
  if (meter.value) meterFill([meter.value]);
});
watch(draft, () => (error.value = ""), { deep: true });

onBeforeUnmount(() => {
  disposed = true;
  draft.apiKey = "";
});

const currentSummary = computed(() => profileSummary({ ...draftFromView(selectedProfile.value), enabled: true }, selectedProfile.value, "embeddings"));
const verifyItems = computed(() => {
  const v = verification.value;
  if (!v) return [];
  return [
    { key: "complete", icon: v.complete ? "circle-check" : "triangle-alert", label: t.flow.completeness, value: v.complete ? t.flow.allIndexed : t.flow.missing(pagesText(v.missingPages)) },
    { key: "stale", icon: v.stalePages ? "triangle-alert" : "circle-check", label: t.flow.freshness, value: v.stalePages ? t.flow.stale(pagesText(v.stalePages)) : t.flow.revisionsMatch },
    { key: "dimensions", icon: v.dimensionsOk ? "circle-check" : "triangle-alert", label: t.flow.vectorSize, value: v.dimensionsOk ? t.flow.vectorsOk : t.flow.vectorsBad },
    { key: "sanity", icon: "search", label: t.flow.sanity, value: t.flow.sanityValue(v.sanity.hitsAt5, v.sanity.checked) },
  ];
});
</script>

<template>
  <div class="ai-index-flow" :data-ai-index-flow="mode">
    <StepFlow v-model="current" :steps="steps" :label="modeLabel" :context="t.flow.context(modeLabel)" :busy="busy" :cancel-label="hasGeneration ? t.flow.cancelBuild : t.evaluation.cancel" @next="onNext" @cancel="onCancel">
      <template #profile>
        <template v-if="hasGeneration">
          <p class="st-note" data-ai-flow-started><AtlasIcon name="info" :size="15" />{{ t.flow.started }}</p>
          <SummaryList :items="replacing ? profileSummary(draft, profile, 'embeddings') : currentSummary" :label="t.flow.generationProfile" />
        </template>
        <template v-else-if="savedProfile && replacing">
          <p class="st-note" data-ai-saved-index-profile><span>{{ t.flow.savedProfile }} <strong>{{ savedProfile.name }}</strong>. {{ t.flow.savedProfileNote }}</span></p>
          <SummaryList :items="currentSummary" :label="t.flow.selectedProfile" />
          <p class="st-note">{{ t.flow.costNote }}</p>
        </template>
        <AiProfileFields v-else-if="replacing" kind="embeddings" :draft="draft" :view="profile" :errors="shownErrors" candidate :disabled="busy" />
        <template v-else>
          <SummaryList :items="currentSummary" :label="t.flow.indexProfile" data-ai-flow-summary />
          <p class="st-note"><AtlasIcon name="info" :size="15" />{{ mode === "rebuild" ? t.flow.rebuildNote : t.flow.buildNote }} {{ t.flow.tokensByPages }}</p>
        </template>
      </template>

      <template #build>
        <div v-if="generation" class="ai-build" data-ai-build>
          <div class="ai-build-head">
            <StatusBadge :tone="complete ? 'success' : 'info'" :icon="complete ? 'circle-check' : 'hourglass'" :label="complete ? t.flow.done : t.overview.building" />
            <span class="st-note">{{ generation.model }}<template v-if="generation.modelVersion"> · {{ generation.modelVersion }}</template></span>
          </div>
          <div class="ai-build-meter" role="progressbar" :aria-valuenow="percent" aria-valuemin="0" aria-valuemax="100" :aria-label="t.flow.progressLabel(percent)"><span ref="meter" :style="{ width: `${percent}%` }" /></div>
          <p class="ai-build-figures" data-ai-build-figures><strong>{{ formatNumber(generation.indexedPages) }}</strong> {{ t.flow.figures(pagesText(generation.eligiblePages), percent, formatNumber(generation.chunks)) }}</p>
          <p class="st-note"><AtlasIcon name="clock" :size="15" /><span>{{ t.flow.pending(formatNumber(generation.pendingTasks)) }}<template v-if="generation.failingTasks"> · <span class="ai-failing" data-ai-failing>{{ t.flow.failing(generation.failingTasks) }}</span></template>. {{ t.flow.publishedDuring }}</span></p>
          <p v-if="readError" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ t.flow.refreshFailed(readError) }}</p>
          <LiveUpdateNote v-if="liveOff" @refresh="refreshLive" />
        </div>
      </template>

      <template #verify>
        <ConflictNote v-if="conflict" :text="t.flow.conflict" :dismissible="true" data-ai-flow-conflict @dismiss="conflict = false" />
        <div class="st-actions">
          <Button variant="outline" type="button" :disabled="busy || !complete" data-ai-verify @click="runVerify">
            <AtlasIcon :name="busy && !verification ? 'loader-circle' : 'list-checks'" :size="16" :class="{ 'atlas-spinner': busy && !verification }" />{{ verification ? t.health.verifyAgain : t.flow.runVerify }}
          </Button>
          <span class="st-note">{{ t.flow.verifyCost }}</span>
        </div>
        <div v-if="verification" ref="verdict" class="ai-verdict" data-ai-verification>
          <StatusBadge :tone="verification.canActivate ? 'success' : 'danger'" :label="verification.canActivate ? t.flow.canActivate : t.flow.cannotActivate" />
          <SummaryList :items="verifyItems" :label="t.flow.verifyResult" />
          <p v-if="!verification.canActivate" class="st-note"><AtlasIcon name="triangle-alert" :size="15" />{{ t.flow.waitOrCancel }}</p>
        </div>
      </template>

      <template #activate>
        <ImpactPreview :title="t.flow.impactTitle" :items="[{ icon: 'search', label: t.flow.impactSearch, detail: t.flow.impactSearchDetail }, ...(replacing ? [{ icon: 'database', label: t.flow.impactModel(draft.model.trim() || generation?.model || ''), detail: t.flow.impactModelDetail }] : []), { icon: 'trash', label: t.flow.impactOld, detail: t.flow.impactOldDetail, tone: 'warning' }]" :unchanged="t.flow.impactUnchanged" data-ai-activate-impact />
      </template>

      <template #actions>
        <Button v-if="hasGeneration" variant="ghost" type="button" :disabled="busy" data-ai-flow-leave @click="emit('leave')"><AtlasIcon name="back" :size="16" />{{ complete ? t.flow.leaveComplete : t.flow.leaveBuilding }}</Button>
      </template>
    </StepFlow>

    <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="ai-flow-error" role="alert" data-ai-flow-error><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p></Transition>

    <ConfirmDialog :open="confirmCancel" :title="t.flow.confirmTitle" :confirm-label="t.flow.cancelBuild" confirm-icon="trash" :busy="busy" data-ai-confirm-cancel @confirm="cancelBuild" @cancel="confirmCancel = false">
      <p>{{ t.flow.confirmText }}</p>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
/* A column of flex items, not a grid: the error unfolds and gives the gap above it back (settings/disclosure.ts). */
.ai-index-flow { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
.ai-build { display: grid; gap: 10px; }
.ai-build-head { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.ai-build-meter { height: 8px; overflow: hidden; border-radius: 999px; background: var(--hover); }
.ai-build-meter > span { display: block; height: 100%; border-radius: inherit; background: var(--accent); transition: width var(--atlas-motion-slow) var(--atlas-ease-out); }
.ai-build-figures { margin: 0; font-size: 14px; font-variant-numeric: tabular-nums; }
.ai-failing { color: var(--error); font-weight: 600; }
.ai-verdict { display: grid; gap: 10px; justify-items: start; }
.ai-verdict > :deep(*) { max-width: 100%; }
/* The summary is a size container (inline-size): left to shrink to its content it has none, collapsed to its
   border and wrapped every word into an invisible 540 px column. It takes the full row; only the badge hugs. */
.ai-verdict > :deep(.summary-list) { justify-self: stretch; }
.ai-flow-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
</style>
