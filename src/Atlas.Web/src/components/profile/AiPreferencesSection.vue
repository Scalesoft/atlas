<script setup lang="ts">
import ct from "../../i18n/components-b";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { flashRow } from "../../motion/aiMotion";
import { ApiError, errorMessage } from "../../api";
import { aiPreferencesApi, suggestionTypes, type AiPreferences, type AiPreferencesApi, type AssistantScopeMode, type EditorSuggestionsMode, type SearchAnswerMode } from "../../aiPreferences";
import { useAiCapabilities } from "../../composables/useAiCapabilities";
import { useAssistant } from "../../assistant/useAssistant";
import AiQuotaStatus from "../ai/AiQuotaStatus.vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import ChoiceCards from "../settings/ChoiceCards.vue";
import ConflictNote from "../settings/ConflictNote.vue";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import LoadingNote from "../settings/LoadingNote.vue";
import OptionPills from "../settings/OptionPills.vue";
import SaveBar from "../settings/SaveBar.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import SummaryList from "../settings/SummaryList.vue";
import type { ChoiceOption, SummaryItem } from "../settings/types";

/**
 * Profil -> AI (G4): the user's own preferences for the rule-based editor suggestions. Progressive disclosure: the
 * list of suggestion types opens only while suggestions are on, the choice made earlier stays editable and a live
 * summary says what the editor will do. Saved explicitly (SaveBar, CAS); the page then reloads `auth/me`, which
 * carries the result as `features.editorAssist` and `features.disabledSuggestionTypes`. These are the user's data,
 * not a model call, so they save even when the AI is unavailable, and the rule-based setting carries no AI glyph.
 * Phase 3 adds two groups, each only while its capability exists: ct.aiPrefs.searchAnswer (capability `answer`) and
 * ct.aiPrefs.assistant (capability `assistant`: default scope, a way to the user's conversations and deleting all of them,
 * which asks first and is not part of the draft: it is an action, not a preference). `capabilities` overrides the
 * app's live ones for the gallery and tests.
 */
const props = withDefaults(defineProps<{ api?: AiPreferencesApi; capabilities?: { answer?: boolean; assistant?: boolean } }>(), { api: () => aiPreferencesApi, capabilities: undefined });
const emit = defineEmits<{ dirty: [value: boolean]; busy: [value: boolean]; saved: []; openConversations: [] }>();
const live = useAiCapabilities().capabilities;
const hasAnswer = computed(() => props.capabilities?.answer ?? live.value.answer);
const hasAssistant = computed(() => props.capabilities?.assistant ?? live.value.assistant);
const searchAnswer = ref<SearchAnswerMode>("auto");
const assistantScope = ref<AssistantScopeMode>("context");
const deleting = ref(false);
const deleteBusy = ref(false);
const deleteError = ref("");
const deleted = ref(false);

const saved = ref<AiPreferences>();
const mode = ref<EditorSuggestionsMode>("on");
/** The types the editor may offer; the preference itself stores the opposite (what is switched off). */
const offered = ref<string[]>([]);
const loading = ref(true);
const loadError = ref("");
const saving = ref(false);
const saveError = ref("");
const conflict = ref(false);
const summaryHost = ref<HTMLElement>();
let disposed = false;
let request: AbortController | undefined;

const known = suggestionTypes.map((type) => type.value);
// A type this version does not know stays switched off if the server says so; it is never dropped silently.
const unknownDisabled = computed(() => (saved.value?.disabledSuggestionTypes ?? []).filter((type) => !known.includes(type)));
const draftDisabled = computed(() => [...known.filter((type) => !offered.value.includes(type)), ...unknownDisabled.value]);
const sameSet = (left: string[], right: string[]) => left.length === right.length && left.every((item) => right.includes(item));
const savedAnswer = computed(() => saved.value?.searchAnswer ?? "auto");
const savedScope = computed(() => saved.value?.assistantScope ?? "context");
const dirty = computed(() => !!saved.value && (mode.value !== saved.value.editorSuggestions || !sameSet(draftDisabled.value, saved.value.disabledSuggestionTypes)
  || (hasAnswer.value && searchAnswer.value !== savedAnswer.value) || (hasAssistant.value && assistantScope.value !== savedScope.value)));
const answerOptions: ChoiceOption[] = [
  { value: "ask", label: ct.aiPrefs.askOnDemand, icon: "search" },
  { value: "auto", label: ct.aiPrefs.askAuto, icon: "activity" },
  { value: "never", label: ct.aiPrefs.askNever, icon: "ban" },
];
const answerHelp = computed(() => ({
  ask: ct.aiPrefs.helpAsk,
  auto: ct.aiPrefs.helpAuto,
  never: ct.aiPrefs.helpNever,
})[searchAnswer.value]);
const scopeOptions: ChoiceOption[] = [
  { value: "context", label: ct.aiPrefs.scopeContext, icon: "file-text" },
  { value: "wiki", label: ct.aiPrefs.scopeWiki, icon: "book-open" },
];
const locked = computed(() => saving.value || loading.value);

const modeOptions: ChoiceOption[] = [
  { value: "on", label: ct.aiPrefs.on, icon: "check" },
  { value: "rules", label: ct.aiPrefs.rulesOnly, icon: "list" },
  { value: "off", label: ct.aiPrefs.off, icon: "ban" },
];
const typeOptions: ChoiceOption[] = suggestionTypes.map(({ value, label, description, icon }) => ({ value, label, description, icon }));
const showTypes = computed(() => mode.value !== "off");
const nothingOffered = computed(() => showTypes.value && offered.value.length === 0);

const summary = computed<SummaryItem[]>(() => {
  const labelOf = (value: string) => suggestionTypes.find((type) => type.value === value)?.label ?? value;
  const list = (values: string[]) => values.map(labelOf).join(", ");
  const modeText = mode.value === "off" ? ct.aiPrefs.off : mode.value === "rules" ? ct.aiPrefs.rulesNoModel : ct.aiPrefs.on;
  const items: SummaryItem[] = [{ key: "mode", icon: "lightbulb", label: ct.aiPrefs.suggestions, value: modeText }];
  if (mode.value === "off") items.push({ key: "result", icon: "ban", label: ct.aiPrefs.editor, value: ct.aiPrefs.editorOffers });
  else {
    const offeredKnown = known.filter((type) => offered.value.includes(type));
    items.push({ key: "offered", icon: "circle-check", label: ct.aiPrefs.offered, value: offeredKnown.length ? list(offeredKnown) : ct.aiPrefs.nothing });
    if (draftDisabled.value.length) items.push({ key: "hidden", icon: "ban", label: ct.aiPrefs.notOffered, value: list(draftDisabled.value) });
  }
  if (hasAnswer.value) items.push({ key: "answer", icon: "search", label: ct.aiPrefs.searchAnswer, value: ({ ask: ct.aiPrefs.answerOnRequest, auto: ct.aiPrefs.answerAuto, never: ct.aiPrefs.answerNever } as Record<string, string>)[searchAnswer.value] ?? "" });
  if (hasAssistant.value) items.push({ key: "scope", icon: "book-open", label: ct.aiPrefs.assistantScope, value: assistantScope.value === "wiki" ? ct.aiPrefs.scopeWiki : ct.aiPrefs.scopeSpace });
  return items;
});

function adopt(next: AiPreferences) {
  const normalized = { ...next, disabledSuggestionTypes: next.disabledSuggestionTypes ?? [] };
  saved.value = normalized;
  mode.value = normalized.editorSuggestions;
  offered.value = known.filter((type) => !normalized.disabledSuggestionTypes.includes(type));
  searchAnswer.value = normalized.searchAnswer ?? "auto";
  assistantScope.value = normalized.assistantScope ?? "context";
  saveError.value = "";
  loadError.value = "";
}
async function load() {
  request?.abort();
  const current = (request = new AbortController());
  loading.value = true;
  loadError.value = "";
  try {
    const result = await props.api.load(current.signal);
    if (!current.signal.aborted && !disposed) adopt(result);
  } catch (failure) {
    if (!current.signal.aborted && !disposed) loadError.value = errorMessage(failure);
  } finally {
    if (!current.signal.aborted && !disposed) loading.value = false;
  }
}
function discard() {
  if (saved.value && !saving.value) adopt(saved.value);
}
/** Saves the draft; resolves `true` when it is saved (or there was nothing to save). */
async function save(): Promise<boolean> {
  const current = saved.value;
  if (!current || saving.value) return false;
  if (!dirty.value) return true;
  saving.value = true;
  saveError.value = "";
  conflict.value = false;
  try {
    const result = await props.api.save({
      editorSuggestions: mode.value,
      disabledSuggestionTypes: draftDisabled.value,
      // A field whose capability is off is left out: the server keeps the stored value.
      ...(hasAnswer.value ? { searchAnswer: searchAnswer.value } : {}),
      ...(hasAssistant.value ? { assistantScope: assistantScope.value } : {}),
    }, current.version);
    if (disposed) return true;
    adopt(result);
    emit("saved");
    // The saved summary flashes once (A0 flashRow; a fade under reduced motion).
    void nextTick(() => { if (summaryHost.value) flashRow(summaryHost.value); });
    return true;
  } catch (failure) {
    if (disposed) return false;
    conflict.value = failure instanceof ApiError && failure.status === 409;
    saveError.value = conflict.value ? "" : errorMessage(failure);
    return false;
  } finally {
    saving.value = false;
  }
}
async function deleteAll() {
  if (deleteBusy.value) return;
  deleteBusy.value = true;
  deleteError.value = "";
  try {
    await props.api.deleteAllConversations();
    useAssistant().clearHistory();
    if (disposed) return;
    deleting.value = false;
    deleted.value = true;
  } catch (failure) {
    if (!disposed) deleteError.value = errorMessage(failure);
  } finally {
    deleteBusy.value = false;
  }
}
function reloadAfterConflict() {
  conflict.value = false;
  void load();
}
defineExpose({ save, discard });

watch(dirty, (value) => emit("dirty", value), { flush: "sync" });
watch(() => saving.value || deleteBusy.value, (value) => emit("busy", value), { flush: "sync" });
onMounted(load);
onBeforeUnmount(() => {
  disposed = true;
  request?.abort();
  emit("dirty", false);
  emit("busy", false);
});

const answerModel = computed({ get: () => searchAnswer.value, set: (value: string) => (searchAnswer.value = value as SearchAnswerMode) });
const scopeModel = computed({ get: () => assistantScope.value, set: (value: string) => (assistantScope.value = value as AssistantScopeMode) });
const modeModel = computed({ get: () => mode.value, set: (value: string) => (mode.value = value as EditorSuggestionsMode) });
const offeredModel = computed({ get: () => offered.value, set: (value: string | string[] | null) => (offered.value = Array.isArray(value) ? value : []) });
</script>

<template>
  <div class="ai-preferences" role="region" :aria-label="ct.aiPrefs.region">
    <SettingsSection :title="ct.aiPrefs.quotaTitle" icon="activity"><AiQuotaStatus /></SettingsSection>
    <ConflictNote v-if="conflict" :text="ct.aiPrefs.conflict" :reload-label="ct.aiPrefs.reload" :busy="loading || saving" data-ai-preferences-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="ct.aiPrefs.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-if="loading && !saved" :label="ct.aiPrefs.loading" />
    <template v-else-if="saved">
      <SettingsSection :title="ct.aiPrefs.suggestions" icon="lightbulb" :description="ct.aiPrefs.suggestionsDescription">
        <OptionPills v-model="modeModel" :options="modeOptions" :label="ct.aiPrefs.suggestions" hide-label :disabled="locked" data-ai-suggestions-mode />
        <InlineReveal :open="showTypes" class="ai-types-reveal">
          <div class="ai-types">
            <p class="ai-types-lead">{{ ct.aiPrefs.whichSuggestions }}</p>
            <ChoiceCards v-model="offeredModel" multiple :options="typeOptions" :label="ct.aiPrefs.suggestionTypes" hide-label :columns="2" :disabled="locked" data-ai-suggestion-types />
            <p v-if="nothingOffered" class="st-note" data-ai-nothing-offered>{{ ct.aiPrefs.nothingOffered }}</p>
          </div>
        </InlineReveal>
      </SettingsSection>
      <SettingsSection v-if="hasAnswer" :title="ct.aiPrefs.searchAnswer" icon="search" :description="ct.aiPrefs.answerDescription" data-ai-answer-preferences>
        <OptionPills v-model="answerModel" :options="answerOptions" :label="ct.aiPrefs.searchAnswer" hide-label :disabled="locked" data-ai-search-answer />
        <Transition name="st-fade" mode="out-in"><p :key="searchAnswer" class="st-note" data-ai-answer-help>{{ answerHelp }}</p></Transition>
      </SettingsSection>
      <SettingsSection v-if="hasAssistant" :title="ct.aiPrefs.assistant" icon="book-open" :description="ct.aiPrefs.assistantDescription" data-ai-assistant-preferences>
        <SettingsRow :label="ct.aiPrefs.defaultScope" :label-for="false" :help="ct.aiPrefs.defaultScopeHelp">
          <OptionPills v-model="scopeModel" :options="scopeOptions" :label="ct.aiPrefs.defaultScope" hide-label :disabled="locked" data-ai-assistant-scope />
        </SettingsRow>
        <SettingsRow :label="ct.aiPrefs.myConversations" :label-for="false" :help="ct.aiPrefs.myConversationsHelp">
          <Button variant="outline" size="sm" type="button" data-ai-open-conversations @click="emit('openConversations')"><AtlasIcon name="quote" :size="15" />{{ ct.aiPrefs.openConversations }}</Button>
        </SettingsRow>
      </SettingsSection>
      <SettingsSection v-if="hasAssistant" :title="ct.aiPrefs.historyTitle" icon="history" advanced :summary="ct.aiPrefs.historySummary" data-ai-history-settings>
        <SettingsRow :label="ct.aiPrefs.deleteAll" :label-for="false" :help="ct.aiPrefs.deleteAllHelp">
          <div class="ai-delete">
            <Button variant="outline" size="sm" type="button" :disabled="locked || deleteBusy" data-ai-delete-conversations @click="deleted = false; deleting = true"><AtlasIcon name="trash" :size="15" />{{ ct.aiPrefs.deleteAll }}</Button>
            <p v-if="deleted" class="st-note" role="status" data-ai-conversations-deleted><AtlasIcon name="circle-check" :size="15" />{{ ct.aiPrefs.allDeleted }}</p>
          </div>
        </SettingsRow>
      </SettingsSection>
      <SettingsSection :title="ct.aiPrefs.summary" icon="info" :description="ct.aiPrefs.summaryDescription">
        <div ref="summaryHost" class="ai-summary"><SummaryList :items="summary" :label="ct.aiPrefs.summaryLabel" /></div>
      </SettingsSection>
      <SettingsSection :title="ct.aiPrefs.guardTitle" icon="shield-check">
        <SettingsRow :label="ct.aiPrefs.guardRow" :help="ct.aiPrefs.guardHelp">
          <StatusBadge tone="success" icon="check" :label="ct.aiPrefs.alwaysOn" size="sm" data-password-guard />
        </SettingsRow>
      </SettingsSection>
      <ConfirmDialog :open="deleting" :title="ct.aiPrefs.deleteAll" :confirm-label="ct.aiPrefs.deleteAll" confirm-icon="trash" :busy="deleteBusy" :error="deleteError || undefined" @confirm="deleteAll" @cancel="deleting = false; deleteError = ''">
        <p>{{ ct.aiPrefs.deleteConfirmText }}</p>
        <p>{{ ct.aiPrefs.unchanged }}</p>
      </ConfirmDialog>
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError || undefined" :save-label="ct.aiPrefs.savePrefs" @save="save" @cancel="discard" />
    </template>
  </div>
</template>

<style scoped>
.ai-preferences { display: grid; gap: 16px; min-width: 0; }
/* Folded (Vypnuto), the reveal still took its gap in the card's column, a 12 px band under the pills; the panel's own lead
   takes the gap's place instead, so opening grows the same 24 px with the height. */
.ai-types-reveal { margin-top: calc(var(--st-body-gap, 12px) * -1); }
.ai-types { display: grid; gap: 10px; padding-top: calc(var(--st-body-gap, 12px) + 12px); }
.ai-summary { border-radius: var(--atlas-radius-sm); }
.ai-delete { display: grid; gap: 6px; justify-items: start; }
.ai-types-lead { margin: 0; font-weight: 600; }
</style>
