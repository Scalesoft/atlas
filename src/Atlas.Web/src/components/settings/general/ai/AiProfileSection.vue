<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import AtlasIcon from "../../../AtlasIcon.vue";
import { Button } from "../../../ui/button";
import { ApiError, api, errorMessage, post, put } from "../../../../api";
import { popStatus } from "../../../../motion/aiMotion";
import ChoiceCards from "../../ChoiceCards.vue";
import { disclosureHooks } from "../../disclosure";
import ConflictNote from "../../ConflictNote.vue";
import ErrorState from "../../ErrorState.vue";
import LoadingNote from "../../LoadingNote.vue";
import InlineReveal from "../../InlineReveal.vue";
import SaveBar from "../../SaveBar.vue";
import SettingsSection from "../../SettingsSection.vue";
import StatusBadge from "../../StatusBadge.vue";
import { useDraftGuard } from "../../dirtyGuard";
import { useSettingsDocument } from "../../useSettingsDocument";
import { useSettingsRoute } from "../../useSettingsRoute";
import AiProfileFields from "./AiProfileFields.vue";
import AiSavedProfiles from "./AiSavedProfiles.vue";
import { formatNumber } from "../../../../i18n";
import t from "../../../../i18n/settings-ai";
import {
  draftDirty, draftFromView, draftToRequest, errorCodeText, isMissingEndpoint, kindIcons, kindLabels, validateDraft,
  type AiProfileView, type AiTestResult, type ProfileDraft, type ProfileKind,
} from "./aiAdmin";

/**
 * One profile form of Administrace → AI: Embeddingy (kind `embeddings`) or Rychlé úlohy (`quick`).
 * Own SaveBar, CAS (`expectedVersion` from the view) and ConflictNote like the YouTrack module. The
 * fields are `AiProfileFields`, the same component the index flow's Profil step uses. The connection
 * test (A2, MOCKED until then) runs against the saved profile only, so it is closed while there are
 * unsaved changes and costs tokens, which the text says.
 */
const props = defineProps<{ kind: ProfileKind; guided?: boolean }>();
const emit = defineEmits<{ saved: [] }>();
const router = useSettingsRoute();
const endpoint = computed(() => `/admin/ai/profiles/${props.kind}`);
const draft = reactive<ProfileDraft>(draftFromView(blank()));
/** Rychlé úlohy: Nenastaveno, an own provider, or the Chat profile. Embeddingy and Chat always edit their own. */
const mode = ref<"none" | "custom" | "chat">("custom");
const needsGeneration = ref(false);
const showErrors = ref(false);
const testing = ref(false);
const testError = ref("");
const testResult = ref<AiTestResult>();
const testMissing = ref(false);
const resultBadge = ref<HTMLElement>();
const savedProfiles = ref<InstanceType<typeof AiSavedProfiles>>();
const profileFields = ref<InstanceType<typeof AiProfileFields>>();
const libraryBusy = computed(() => !!savedProfiles.value?.editing || !!savedProfiles.value?.busy);
const { saved: view, loading, loadError, saving, saveError, conflict, dirty, load, save: saveDocument, reset, reloadAfterConflict, isDisposed } = useSettingsDocument<AiProfileView>({
  load: () => api<AiProfileView>(endpoint.value),
  save: async (current) => {
    needsGeneration.value = false;
    // The finally also covers building the request, so a typed key never outlives an attempt.
    try {
      // "Nenastaveno" on an already configured profile switches it off; the stored values stay for later.
      // "Použít profil Chat" keeps the stored values and switches the profile on; it needs no provider fields of its own.
      const request = draftToRequest(mode.value === "none" ? { ...draftFromView(current), enabled: false } : mode.value === "chat" ? { ...draftFromView(current), enabled: true } : effectiveDraft.value, current, props.kind, mode.value === "chat");
      return await put<AiProfileView>(endpoint.value, request);
    } catch (failure) {
      if (!(failure instanceof ApiError && failure.status === 409 && failure.code === "ai_generation_required")) throw failure;
      // The index needs a new generation first: not a version conflict, so no conflict note, only the text and the way to the swap.
      needsGeneration.value = true;
      throw new Error(failure.message);
    } finally {
      // The key only ever lives in the request and the field.
      draft.apiKey = "";
    }
  },
  fill: (current) => {
    Object.assign(draft, draftFromView(current));
    mode.value = initialMode.value;
    showErrors.value = false;
    needsGeneration.value = false;
  },
  dirty: (current): boolean => mode.value !== initialMode.value || (mode.value === "custom" && draftDirty(draft, current)),
  beforeSave: (): string | undefined => {
    if (mode.value !== "custom" || !hasErrors.value) return undefined;
    showErrors.value = true;
    void profileFields.value?.focusFirstError();
    return t.features.fixHighlighted;
  },
  onSaved: () => {
    testResult.value = undefined;
    emit("saved");
  },
  clearConflictOnSave: true,
});

function blank(): AiProfileView {
  return { kind: props.kind, configured: false, enabled: false, provider: null, location: null, baseUrl: null, model: null, modelVersion: null, dimensions: null, queryPrefix: "", documentPrefix: "", timeoutSeconds: props.kind === "rerank" ? 10 : 30, maxConcurrency: 4, reasoningEffort: null, temperature: null, nativeJsonSchema: true, requestDimensions: false, useChatProfile: false, inputPricePerMillion: null, outputPricePerMillion: null, hasApiKey: false, modelLockedByIndex: false, version: null };
}
const quick = computed(() => props.kind === "quick");
const chatKind = computed(() => props.kind === "chat");
const initialMode = computed<"none" | "custom" | "chat">(() => (!quick.value ? "custom" : view.value?.useChatProfile ? "chat" : view.value?.configured ? "custom" : "none"));
const effectiveDraft = computed(() => props.guided ? { ...draft, enabled: true } : draft);
const errors = computed(() => (view.value && mode.value === "custom" ? validateDraft(effectiveDraft.value, view.value) : {}));
const hasErrors = computed(() => Object.keys(errors.value).length > 0);
const shownErrors = computed(() => (showErrors.value ? errors.value : {}));
const canTest = computed(() => !!view.value?.configured && !dirty.value && !testing.value && !libraryBusy.value);
const chatHref = computed(() => router.href("general/modules/ai/chat"));
const sectionDescription = computed(() => t.profile.descriptions[props.kind]);
const swapHref = computed(() => router.href("general/modules/ai/index", { action: "replace" }));
const sourceOptions = [
  { value: "none", label: t.states.notSet, description: t.profile.sourceNone, icon: "ban" },
  { value: "custom", label: t.profile.sourceCustom, description: t.profile.sourceCustomHint, icon: "server" },
  { value: "chat", label: t.profile.sourceChat, description: t.profile.sourceChatHint, icon: "quote" },
];
const stateBadge = computed(() => {
  const current = view.value;
  if (!current?.configured) return { tone: "neutral" as const, icon: "clock", label: t.states.notSet };
  return current.enabled ? { tone: "success" as const, icon: "circle-check", label: t.states.on } : { tone: "neutral" as const, icon: "ban", label: t.states.off };
});

function save() { if (!libraryBusy.value) return saveDocument(); }
async function test() {
  if (!canTest.value) return;
  testing.value = true;
  testError.value = "";
  testResult.value = undefined;
  testMissing.value = false;
  try {
    const result = await post<AiTestResult>(`${endpoint.value}/test`);
    if (isDisposed()) return;
    testResult.value = result;
    await nextTick();
    if (resultBadge.value) popStatus(resultBadge.value);
  } catch (failure) {
    if (isDisposed()) return;
    if (isMissingEndpoint(failure)) testMissing.value = true;
    else testError.value = errorMessage(failure);
  } finally {
    testing.value = false;
  }
}
// A changed draft makes an earlier test result stale.
watch(draft, () => {
  testResult.value = undefined;
  testError.value = "";
}, { deep: true });

function activated(profile: AiProfileView) {
  view.value = profile;
  reset();
  testResult.value = undefined;
  emit("saved");
}
const pageDirty = computed(() => dirty.value || !!savedProfiles.value?.dirty);
useDraftGuard(pageDirty, {
  save: () => savedProfiles.value?.editing ? savedProfiles.value.save() : save(),
  discard: () => { savedProfiles.value?.discard(); reset(); },
});
onMounted(load);
onBeforeUnmount(() => {
  draft.apiKey = "";
});
</script>

<template>
  <div class="ai-profile" role="region" :aria-label="t.profile.region(kindLabels[kind])" :data-ai-profile="kind">
    <ConflictNote v-if="conflict" :text="t.profile.conflict" :reload-label="t.features.reload" :busy="loading || saving" data-ai-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="t.profile.loadFailed(kindLabels[kind])" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !view" :label="t.profile.loading" />
    <template v-else-if="view">
      <AiSavedProfiles ref="savedProfiles" :kind="kind" :current="view" :blocked="dirty || saving || testing" @activated="activated" />
      <SettingsSection :title="guided ? undefined : kindLabels[kind]" :label="guided ? kindLabels[kind] : undefined" :flush="guided" :icon="kindIcons[kind]" :description="guided ? undefined : sectionDescription">
        <template v-if="!guided" #actions><StatusBadge :tone="stateBadge.tone" :icon="stateBadge.icon" :label="stateBadge.label" size="sm" data-ai-profile-state /></template>

        <ChoiceCards v-if="quick" v-model="mode" :options="sourceOptions" :label="t.profile.sourceLabel" :columns="3" :disabled="saving || libraryBusy" data-ai-source />
        <component :is="quick ? InlineReveal : 'div'" :open="quick ? mode === 'custom' : undefined">
          <AiProfileFields ref="profileFields" :kind="kind" :guided="guided" :draft="draft" :view="view" :errors="shownErrors" :locked="view.modelLockedByIndex" :disabled="saving || libraryBusy || mode !== 'custom'" :swap-href="swapHref" data-ai-current-fields />
        </component>
        <p v-if="mode === 'chat'" class="st-note" data-ai-quick-chat><AtlasIcon name="info" :size="15" /><span>{{ t.profile.quickChatBefore }}<a class="st-link" :href="chatHref">{{ t.profile.quickChatLink }}</a>{{ t.profile.quickChatAfter }}</span></p>
        <p v-else-if="mode === 'none' && view.configured" class="st-note" data-ai-quick-off><AtlasIcon name="info" :size="15" />{{ t.profile.quickOff }}</p>

        <template v-if="chatKind">
          <p class="st-note" data-ai-chat-note><AtlasIcon name="info" :size="15" />{{ t.profile.chatNote }}</p>
        </template>

        <!-- The connection test row unfolds with the dependent fields instead of appearing in one frame (it took 54 px at once). -->
        <Transition :css="false" v-bind="disclosureHooks">
        <div v-if="mode === 'custom' && (draft.providerId || view.configured)" class="ai-test">
          <div class="st-actions">
            <Button variant="outline" size="sm" type="button" :disabled="!canTest" data-ai-test @click="test">
              <AtlasIcon :name="testing ? 'loader-circle' : 'plug'" :size="15" :class="{ 'atlas-spinner': testing }" />{{ t.profile.test }}
            </Button>
            <span class="st-note"><template v-if="dirty">{{ t.profile.testSaveFirst }}</template><template v-else-if="!view.configured">{{ t.profile.testAfterSave }}</template><template v-else>{{ t.profile.testCost }}</template></span>
          </div>
          <Transition :css="false" v-bind="disclosureHooks">
            <p v-if="testError" class="ai-test-error" role="alert" data-ai-test-error><AtlasIcon name="triangle-alert" :size="14" />{{ testError }}</p>
          </Transition>
          <Transition :css="false" v-bind="disclosureHooks">
            <p v-if="testMissing" class="st-note" data-ai-test-missing><AtlasIcon name="info" :size="15" />{{ t.profile.testMissing }}</p>
          </Transition>
          <!-- A new test clears the previous result: it folds away and the new one unfolds, instead of the row jumping twice. -->
          <Transition :css="false" v-bind="disclosureHooks">
          <div v-if="testResult" class="ai-test-result" data-ai-test-result>
            <span ref="resultBadge" class="ai-test-badge"><StatusBadge :tone="!testResult.ok ? 'danger' : testResult.warning ? 'warning' : 'success'" :label="testResult.ok ? t.profile.testOk : t.profile.testFailed" /></span>
            <p class="st-note"><AtlasIcon :name="testResult.ok ? 'activity' : 'circle-x'" :size="15" />
              <template v-if="testResult.ok">{{ t.profile.answerIn(formatNumber(testResult.latencyMs)) }}<template v-if="testResult.dimensions"> · {{ t.profile.dimensionsCount(testResult.dimensions) }}</template><template v-if="testResult.model"> · {{ testResult.model }}</template></template>
              <template v-else>{{ errorCodeText(testResult.code) }} <code class="st-mono">{{ testResult.code }}</code></template>
            </p>
            <p v-if="testResult.warning" class="st-note" role="alert" data-ai-test-warning><AtlasIcon name="triangle-alert" :size="15" />{{ testResult.warning }}</p>
          </div>
          </Transition>
        </div>
        </Transition>
      </SettingsSection>

      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="needsGeneration" class="ai-generation" role="alert" data-ai-generation-required>
          <AtlasIcon name="triangle-alert" :size="16" />
          <span>{{ t.profile.generationRequired }}</span>
          <Button as-child variant="outline" size="sm"><a :href="swapHref" data-ai-swap-error><AtlasIcon name="arrow-right-left" :size="15" />{{ t.profile.swapModel }}</a></Button>
        </p>
      </Transition>

      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="guided && view.configured && !view.enabled && !dirty" class="st-actions">
          <Button :disabled="saving || libraryBusy" data-ai-enable-saved @click="save">{{ t.profile.enableSaved }}</Button>
          <span class="st-note">{{ t.profile.enableSavedNote }}</span>
        </p>
      </Transition>
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="guided && saveError && !dirty" class="ai-test-error" role="alert">{{ saveError }}</p>
      </Transition>
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.profile.save(kindLabels[kind])" @save="save" @cancel="reset" />
    </template>
  </div>
</template>

<style scoped>
/* A column of flex items, not a grid: the failure notes unfold and give the gap above them back (settings/disclosure.ts). */
.ai-profile { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
.ai-test { display: flex; flex-direction: column; gap: 8px; padding-top: 4px; }
.ai-test-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
.ai-test-result { display: grid; gap: 6px; justify-items: start; }
.ai-test-badge { display: inline-flex; }
.ai-generation { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin: 0; padding: 12px 14px; border: 1px solid var(--warning); border-radius: var(--radius, 10px); background: var(--warning-bg); font-size: 13px; }
.ai-generation > span { flex: 1 1 240px; }
</style>
