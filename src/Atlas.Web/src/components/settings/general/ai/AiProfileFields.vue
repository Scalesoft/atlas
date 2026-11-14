<script setup lang="ts">
import { computed, inject, nextTick, onBeforeUnmount, ref, watch } from "vue";
import { aiModelDiscoveryKey } from "./aiAdminClient";
import { api } from "../../../../api";
import SearchableChoiceList from "../../SearchableChoiceList.vue";
import { disclosureHooks } from "../../disclosure";
import AtlasIcon from "../../../AtlasIcon.vue";
import { Button } from "../../../ui/button";
import { Switch } from "../../../ui/switch";
import ChoiceCards from "../../ChoiceCards.vue";
import InlineReveal from "../../InlineReveal.vue";
import OptionPills from "../../OptionPills.vue";
import SettingsRow from "../../SettingsRow.vue";
import SettingsSection from "../../SettingsSection.vue";
import StatusBadge from "../../StatusBadge.vue";
import SummaryList from "../../SummaryList.vue";
import t from "../../../../i18n/settings-ai";
import {
  modelCatalogRequest, errorCodeText, type AiModelCatalog, changeProvider, keyWillBeDropped, profileSummary, providerOptions, providerPresets,
  type AiProfileView, type ProfileDraft, type ProfileErrors, type ProfileKind, type ProviderId,
} from "./aiAdmin";

/**
 * The fields of one AI profile as progressive disclosure (AGENTS.md): the
 * provider is chosen first, its dependent fields open below, advanced options stay collapsed with a
 * summary of what is in effect, and a live summary says what the profile will do once saved. Earlier
 * choices stay editable; a provider change keeps compatible values and resets the rest, saying which.
 * The component only edits `draft`; the section (Embeddingy, Rychlé úlohy) and the index flow's
 * Profil step own saving, so both use exactly these fields and rules. The key is write-only: it is
 * never filled in, a stored one is offered as "Uložený klíč · Změnit · Odstranit".
 */
const props = withDefaults(
  defineProps<{
    kind: ProfileKind;
    draft: ProfileDraft;
    view: AiProfileView;
    errors?: ProfileErrors;
    /** Model, version and prefixes cannot change while an index generation is active (index flow = new generation). */
    locked?: boolean;
    disabled?: boolean;
    /** Where "Vyměnit model…" leads when the model is locked. */
    swapHref?: string;
    /** The model fields belong to a candidate that does not replace anything yet: no lock, no enabled switch. */
    candidate?: boolean;
    /** Hide duplicate activation and summary controls in the guided setup page. */
    guided?: boolean;
  }>(),
  { errors: () => ({}), locked: false, disabled: false, swapHref: undefined, candidate: false, guided: false },
);
const root = ref<HTMLElement>();
async function focusFirstError() {
  await nextTick();
  if (advancedErrors.value.some(Boolean)) advancedOpen.value = true;
  // The advanced-section watcher and InlineReveal each need their render before focus can enter.
  await nextTick();
  root.value?.querySelector<HTMLElement>('.settings-row[data-invalid="true"] input:not(:disabled), .settings-row[data-invalid="true"] button:not(:disabled), [data-provider-error]')?.focus();
}
defineExpose({ focusFirstError });

// Refresh 2.5 (board 08): provider cards side by side with the official marks of the cloud providers and a short
// hint; the provider's full description opens with its connection below.
const providerHints: Record<string, string> = t.fields.providerHints;
const options = computed(() => providerOptions(props.kind).map(option => ({ ...option, description: providerHints[option.value] ?? option.description, icon: option.value === "openai" ? "brand-openai" : option.value === "anthropic" ? "brand-anthropic" : option.icon })));
const preset = computed(() => (props.draft.providerId ? providerPresets[props.draft.providerId] : undefined));
const embeddings = computed(() => props.kind === "embeddings");
const rerank = computed(() => props.kind === "rerank");
const droppedNote = ref("");
const changingKey = ref(false);
const hasStoredKey = computed(() => props.view.hasApiKey && !props.draft.clearApiKey);
// A replacement key must stay visible while it is being typed; keyWillBeDropped intentionally
// becomes false once a new key is present, so compatibility must ignore the replacement value.
const reusableStoredKey = computed(() => !keyWillBeDropped({ ...props.draft, apiKey: "", clearApiKey: false }, props.view));
const dropsKey = computed(() => keyWillBeDropped(props.draft, props.view));
const advancedOpen = ref(false);
const summary = computed(() => profileSummary(props.draft, props.view, props.kind));
const advancedSummary = computed(() => {
  const draft = props.draft;
  const parts = [t.fields.summaryTimeout(draft.timeoutSeconds), t.fields.summaryConcurrency(draft.maxConcurrency)];
  if (embeddings.value) {
    if (draft.modelVersion.trim()) parts.push(t.fields.summaryVersion);
    if (draft.queryPrefix || draft.documentPrefix) parts.push(t.fields.summaryPrefixes);
    if (draft.requestDimensions) parts.push(t.fields.summaryDimensions);
  } else if (!rerank.value) {
    parts.push(t.fields.summaryThinking(draft.reasoningEffort || "none"));
    if (draft.temperature.trim()) parts.push(t.fields.summaryTemperature(draft.temperature));
    if (!draft.nativeJsonSchema) parts.push(t.fields.summarySchema);
  }
  return parts.join(" · ");
});
const reasoning = computed(() => [
  { value: "none", label: t.fields.reasoningNone, icon: "clock" },
  { value: "low", label: t.fields.reasoningLow, icon: "activity" },
  { value: "medium", label: t.fields.reasoningMedium, icon: "activity" },
  { value: "high", label: t.fields.reasoningHigh, icon: "activity" },
].map(option => ({ ...option, disabled: props.disabled })));
const discoverModels = inject(aiModelDiscoveryKey, (kind, body, signal) => api<AiModelCatalog>(`/admin/ai/profiles/${kind}/models`, { method: "POST", body: JSON.stringify(body), signal }));
const hosted = computed(() => preset.value?.location === "cloud");
const advancedErrors = computed(() => [props.errors.timeout, props.errors.concurrency, props.errors.temperature, props.errors.prices, props.errors.prefix, hosted.value ? props.errors.baseUrl : undefined]);
watch(advancedErrors, (errors) => {
  if (errors.some(Boolean)) advancedOpen.value = true;
}, { immediate: true });
const modelCatalog = ref<AiModelCatalog["models"]>([]);
const modelsLoading = ref(false), modelsNote = ref("");
let catalogRequest: AbortController | undefined;
const catalogAttempted = ref(false);
function clearCatalog() { catalogRequest?.abort(); catalogRequest = undefined; catalogAttempted.value = false; modelsLoading.value = false; modelCatalog.value = []; modelsNote.value = ""; }
watch(() => [props.kind, props.draft.providerId, props.draft.baseUrl, props.draft.apiKey, props.draft.clearApiKey, props.view.version], clearCatalog, { flush: "sync" });
onBeforeUnmount(clearCatalog);
const modelKeyMissing = computed(() => !!preset.value?.keyRequired && !props.draft.apiKey.trim() && (!hasStoredKey.value || dropsKey.value));
function focusModel() {
  if (!catalogAttempted.value) void loadModels();
}
async function loadModels() {
  if (modelKeyMissing.value) {
    modelsNote.value = t.fields.keyFirstNote;
    return;
  }
  clearCatalog();
  catalogAttempted.value = true;
  const current = catalogRequest = new AbortController();
  modelsLoading.value = true;
  try {
    const body = modelCatalogRequest(props.draft);
    const result = await discoverModels(props.kind, body, current.signal);
    if (current.signal.aborted || catalogRequest !== current) return;
    modelCatalog.value = result.ok ? result.models : [];
    modelsNote.value = result.ok ? (result.models.length ? t.fields.listNote : t.fields.listEmpty) : `${errorCodeText(result.code)} ${t.fields.enterManually}`;
  } catch {
    if (!current.signal.aborted && catalogRequest === current) modelsNote.value = t.fields.listFailed;
  } finally { if (catalogRequest === current) modelsLoading.value = false; }
}
/** „Vybrat model“: the button is disabled while the list loads, so the keyboard goes on in the list's search (or stays on the button). */
async function pickModel() {
  await loadModels();
  await nextTick();
  (root.value?.querySelector<HTMLElement>(".ai-model-picker input[type=search]") ?? root.value?.querySelector<HTMLElement>("[data-ai-models-load]"))?.focus();
}
const modelOptions = computed(() => modelCatalog.value.map(model => ({ value: model.id, icon: "box", disabled: props.disabled, label: model.name || model.id, description: model.name !== model.id ? model.id : undefined })));
const modelLocked = computed(() => props.locked && embeddings.value && !props.candidate);
const providerDisplayIcon = computed(() => options.value.find(option => option.value === props.draft.providerId)?.icon || "server");

function pick(value: string | string[] | null) {
  if (typeof value !== "string" || !(value in providerPresets)) return;
  const result = changeProvider(props.draft, value as ProviderId, props.kind);
  if (result.draft === props.draft) return;
  Object.assign(props.draft, result.draft);
  changingKey.value = false;
  droppedNote.value = result.dropped.length ? t.fields.dropped(result.dropped.join(", ")) : "";
}
function removeKey() {
  props.draft.clearApiKey = true;
  props.draft.apiKey = "";
  changingKey.value = false;
}
function stopChangingKey() {
  changingKey.value = false;
  props.draft.apiKey = "";
}
watch(() => props.view.version, () => {
  changingKey.value = false;
  droppedNote.value = "";
});
</script>

<template>
  <div ref="root" class="ai-fields" :data-ai-fields="kind">
    <div v-if="modelLocked" class="ai-static-provider" data-ai-provider-static>
      <span class="st-note">{{ t.fields.currentProvider }}</span>
      <strong><AtlasIcon :name="providerDisplayIcon" :size="18" />{{ preset?.label }}</strong>
      <p class="st-note">{{ t.fields.providerWithModel }}</p>
    </div>
    <ChoiceCards v-else :model-value="draft.providerId || null" :options="options" :label="t.summary.provider" :description="embeddings ? t.fields.providerEmbeddings : rerank ? t.fields.providerRerank : kind === 'chat' ? t.fields.providerChat : t.fields.providerQuick" :disabled="disabled" :columns="2" class="ai-providers" @update:model-value="pick" />
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="errors.provider" class="st-note" role="alert" tabindex="-1" data-provider-error>{{ errors.provider }}</p>
    </Transition>
    <p v-if="droppedNote" class="st-note" role="status" data-provider-dropped><AtlasIcon name="info" :size="15" />{{ droppedNote }}</p>

    <InlineReveal :open="!!draft.providerId">
      <div v-if="draft.providerId" class="ai-fields-body">
        <SettingsSection :title="t.fields.connection" icon="plug" :description="preset?.description">
          <SettingsRow v-if="!hosted" v-slot="{ id, describedBy, invalid }" :label="t.summary.url" :help="preset?.urlHint" :error="errors.baseUrl">
            <input :id="id" v-model="draft.baseUrl" class="st-input st-mono" inputmode="url" maxlength="2048" autocomplete="off" spellcheck="false" :placeholder="preset?.defaultUrl || t.fields.defaultUrl" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" data-ai-url />
          </SettingsRow>

          <SettingsRow :label="t.fields.apiKey" :label-for="false" :error="errors.apiKey" :help="t.fields.apiKeyHelp">
            <template #default="{ describedBy }">
              <div v-if="hasStoredKey && reusableStoredKey && !changingKey" class="ai-key" data-ai-key-saved>
                <StatusBadge tone="success" icon="key-round" :label="t.fields.keyStored" size="sm" />
                <Button type="button" variant="outline" size="sm" :disabled="disabled" data-ai-key-change @click="changingKey = true">{{ t.fields.change }}</Button>
                <Button type="button" variant="ghost" size="sm" :disabled="disabled" data-ai-key-remove @click="removeKey">{{ t.fields.remove }}</Button>
              </div>
              <div v-else-if="draft.clearApiKey" class="ai-key" data-ai-key-removed>
                <StatusBadge tone="warning" icon="trash" :label="t.fields.keyWillBeRemoved" size="sm" />
                <Button type="button" variant="ghost" size="sm" :disabled="disabled" data-ai-key-keep @click="draft.clearApiKey = false">{{ t.fields.keepStored }}</Button>
              </div>
              <div v-else class="ai-key">
                <input v-model="draft.apiKey" class="st-input" type="password" autocomplete="new-password" maxlength="8192" :disabled="disabled" :placeholder="preset?.keyRequired ? t.fields.keyRequiredHint : t.fields.keyOptionalHint" :aria-label="hasStoredKey ? t.fields.newApiKey : t.fields.apiKey" :aria-describedby="describedBy" :aria-invalid="errors.apiKey ? true : undefined" data-ai-key-input />
                <Button v-if="hasStoredKey && reusableStoredKey" type="button" variant="ghost" size="sm" :disabled="disabled" data-ai-key-cancel @click="stopChangingKey">{{ t.fields.leaveStored }}</Button>
              </div>
              <p v-if="dropsKey" class="st-note" data-ai-key-drop><AtlasIcon name="triangle-alert" :size="15" />{{ t.fields.keyDrop }}</p>
            </template>
          </SettingsRow>

          <div v-if="modelLocked" class="ai-current-model" data-ai-current-model>
            <span class="st-note">{{ t.fields.currentModel }}</span>
            <strong class="st-mono">{{ draft.model }}</strong>
            <p class="st-note" data-ai-model-locked>{{ t.fields.modelLocked }}</p>
            <Button v-if="swapHref" as-child variant="outline" size="sm"><a :href="swapHref" data-ai-swap><AtlasIcon name="arrow-right-left" :size="15" />{{ t.fields.changeModel }}</a></Button>
          </div>
          <template v-else>
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.summary.model" :error="errors.model" :help="t.fields.modelHelp">
              <div class="ai-model-entry">
                <input :id="id" v-model="draft.model" class="st-input" maxlength="200" autocomplete="off" spellcheck="false" :placeholder="embeddings ? t.fields.modelExampleEmbeddings : rerank ? t.fields.modelExampleRerank : t.fields.modelExampleChat" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" data-ai-model @focus="focusModel" />
                <Button type="button" variant="outline" size="sm" :disabled="disabled || modelsLoading || modelKeyMissing" data-ai-models-load @click="pickModel">{{ modelsLoading ? t.fields.loadingModels : catalogAttempted ? t.fields.refreshList : t.fields.pickModel }}</Button>
              </div>
            </SettingsRow>
            <div class="ai-model-picker">
              <p class="st-note">{{ t.fields.pickerNote }}</p>
              <SearchableChoiceList v-if="modelOptions.length" :model-value="draft.model" :options="modelOptions" :label="t.fields.availableModels" :placeholder="t.fields.searchModels" @pick="draft.model = $event" />
              <p v-if="modelKeyMissing || modelsNote" class="st-note" role="status" data-ai-models-note>{{ modelKeyMissing ? t.fields.keyFirst : modelsNote }}</p>
            </div>
          </template>
          <p v-if="!embeddings" class="st-note">{{ t.fields.noRebuild }}</p>
          <p v-if="hosted" class="st-note" data-ai-default-url>{{ t.fields.defaultUrlNote(draft.baseUrl.trim() || preset?.defaultUrl || "") }}</p>


          <SettingsRow v-if="!candidate && !guided" v-slot="{ id, describedBy }" :label="embeddings ? t.fields.useEmbeddings : rerank ? t.fields.useRerank : kind === 'chat' ? t.fields.useChat : t.fields.useQuick">
            <Switch :id="id" v-model="draft.enabled" :aria-describedby="describedBy" :disabled="disabled" data-ai-enabled />
          </SettingsRow>
        </SettingsSection>

        <SettingsSection v-model:open="advancedOpen" :title="t.fields.advanced" icon="sliders-horizontal" advanced :summary="advancedSummary">
          <SettingsRow v-if="embeddings" :label="t.fields.modelVersion" :label-for="false" :help="t.fields.modelVersionHelp">
            <span v-if="modelLocked" class="st-mono" data-ai-current-version>{{ draft.modelVersion || t.fields.notStated }}</span>
            <input v-else v-model="draft.modelVersion" class="st-input" maxlength="200" autocomplete="off" spellcheck="false" :placeholder="t.fields.modelVersionExample" :aria-label="t.fields.modelVersion" :disabled="disabled" data-ai-model-version />
          </SettingsRow>
          <SettingsRow v-if="hosted" v-slot="{ id, describedBy, invalid }" :label="t.fields.customUrl" :help="t.fields.customUrlHelp(preset?.defaultUrl ?? '')" :error="errors.baseUrl">
            <input :id="id" v-model="draft.baseUrl" class="st-input st-mono" inputmode="url" maxlength="2048" autocomplete="off" spellcheck="false" :placeholder="preset?.defaultUrl" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" data-ai-url />
          </SettingsRow>
          <template v-if="embeddings">
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.fields.queryPrefix" :help="t.fields.queryPrefixHelp" :error="errors.prefix">
              <input :id="id" v-model="draft.queryPrefix" class="st-input" maxlength="200" autocomplete="off" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" :readonly="modelLocked" data-ai-query-prefix />
            </SettingsRow>
            <SettingsRow v-slot="{ id }" :label="t.fields.documentPrefix" :help="t.fields.documentPrefixHelp">
              <input :id="id" v-model="draft.documentPrefix" class="st-input" maxlength="200" autocomplete="off" :disabled="disabled" :readonly="modelLocked" data-ai-document-prefix />
            </SettingsRow>
            <SettingsRow v-if="draft.providerId === 'openai'" v-slot="{ id }" :label="t.fields.sendDimensions" :help="t.fields.sendDimensionsHelp">
              <Switch :id="id" v-model="draft.requestDimensions" :disabled="disabled" />
            </SettingsRow>
            <SettingsRow :label="t.summary.dimensions" :label-for="false" :help="t.fields.dimensionsHelp">
              <span class="st-mono">{{ t.fields.dimensionsFixed }}</span>
            </SettingsRow>
          </template>
          <template v-else-if="!rerank">
            <SettingsRow :label="t.summary.thinking" :label-for="false" :help="kind === 'chat' ? t.fields.thinkingChatHelp : t.fields.thinkingQuickHelp">
              <OptionPills v-model="draft.reasoningEffort" :options="reasoning" :label="t.fields.thinkingLevel" hide-label />
            </SettingsRow>
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.fields.temperature" :help="t.fields.temperatureHelp" :error="errors.temperature">
              <input :id="id" v-model="draft.temperature" class="st-input" inputmode="decimal" autocomplete="off" :placeholder="t.fields.defaultValue" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" data-ai-temperature />
            </SettingsRow>
            <SettingsRow v-slot="{ id }" :label="t.fields.nativeSchema" :help="draft.providerId === 'gateway' ? t.fields.nativeSchemaGateway : t.fields.nativeSchemaOther">
              <Switch :id="id" v-model="draft.nativeJsonSchema" :disabled="disabled" data-ai-native-schema />
            </SettingsRow>
          </template>
          <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.fields.timeout" :error="errors.timeout">
            <input :id="id" v-model.number="draft.timeoutSeconds" class="st-input" type="number" min="1" max="300" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" data-ai-timeout />
          </SettingsRow>
          <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.fields.concurrency" :help="t.fields.concurrencyHelp" :error="errors.concurrency">
            <input :id="id" v-model.number="draft.maxConcurrency" class="st-input" type="number" min="1" max="16" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" data-ai-concurrency />
          </SettingsRow>
          <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.fields.inputPrice" :help="t.fields.inputPriceHelp" :error="errors.prices">
            <input :id="id" v-model="draft.inputPrice" class="st-input" inputmode="decimal" autocomplete="off" :placeholder="t.fields.notStatedShort" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled" />
          </SettingsRow>
          <SettingsRow v-if="!embeddings && !rerank" v-slot="{ id }" :label="t.fields.outputPrice">
            <input :id="id" v-model="draft.outputPrice" class="st-input" inputmode="decimal" autocomplete="off" :placeholder="t.fields.notStatedShort" :disabled="disabled" />
          </SettingsRow>
        </SettingsSection>

        <SettingsSection v-if="!guided" :title="t.fields.summaryTitle" icon="list-checks" :description="t.fields.summaryDescription">
          <SummaryList :items="summary" :label="t.fields.summaryLabel" data-ai-summary />
        </SettingsSection>
      </div>
    </InlineReveal>
  </div>
</template>

<style scoped>
/* .ai-fields is a column of flex items, not a grid: the provider error unfolds and gives the gap above it back (settings/disclosure.ts). */
.ai-fields { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.ai-fields-body { display: grid; gap: 14px; min-width: 0; }
/* A closed reveal is an empty box, yet the column's gap stood above it: 14 px of blank under the provider cards until one
   was picked. The space comes from inside the reveal instead, so it opens and closes with it (as ProfileActivity). */
.ai-fields > .inline-reveal-root { margin-top: -14px; }
.ai-fields > .inline-reveal-root > :deep(.inline-reveal > .inline-reveal-inner) { padding-top: 14px; }
.ai-static-provider, .ai-current-model { display: grid; gap: 6px; justify-items: start; padding: 14px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); }
.ai-static-provider strong { display: flex; align-items: center; gap: 8px; }
.ai-static-provider p, .ai-current-model p { margin: 0; }
.ai-current-model strong { overflow-wrap: anywhere; }
/* Both locked cards open with the same small caps label (the provider's read as body text beside the model's eyebrow),
   and the card's action keeps a little room from the explanation above it. */
.ai-static-provider > .st-note:first-child, .ai-current-model > .st-note:first-child { font-size: 11px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.ai-current-model > :last-child:not(p) { margin-top: 4px; }
.ai-model-entry { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; width: 100%; }
.ai-model-entry .st-input { flex: 1 1 220px; }
.ai-model-picker { display: grid; gap: 8px; justify-items: start; }
/* Provider marks keep their own colours in a quiet white box, selected or not. */
.ai-providers :deep(.choice-tile-icon),
.ai-providers :deep(.choice-tile[data-selected] .choice-tile-icon) { width: 36px; height: 36px; color: var(--muted); background: var(--surface); box-shadow: inset 0 0 0 1px var(--st-line-soft, var(--line)); animation: none; }
.ai-providers :deep(.choice-tile [data-slot="field"]) { align-items: center; min-height: 64px; padding: 10px 12px; }
.ai-providers :deep(.choice-tile-control) { margin-top: 0; }
.ai-model-picker :deep(.searchable-list) { width: 100%; }
.ai-key { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.ai-key .st-input { flex: 1 1 220px; }
/* A field focused by a failed save (focusFirstError) must not end up under the sticky SaveBar at the bottom of the page. */
.ai-fields :deep(:is(input, textarea, select, button)) { scroll-margin-block-end: 96px; }
</style>
