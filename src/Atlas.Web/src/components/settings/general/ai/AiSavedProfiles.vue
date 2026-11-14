<script setup lang="ts">
import { computed, onBeforeUnmount, provide, ref, useId, watch } from "vue";
import AtlasIcon from "../../../AtlasIcon.vue";
import { Button } from "../../../ui/button";
import { Input } from "../../../ui/input";
import { ApiError, api, errorMessage, post, put } from "../../../../api";
import ConfirmDialog from "../../ConfirmDialog.vue";
import InlineReveal from "../../InlineReveal.vue";
import LoadingNote from "../../LoadingNote.vue";
import OptionPills from "../../OptionPills.vue";
import SaveBar from "../../SaveBar.vue";
import SettingsList from "../../SettingsList.vue";
import SettingsListRow from "../../SettingsListRow.vue";
import SettingsRow from "../../SettingsRow.vue";
import SettingsSection from "../../SettingsSection.vue";
import StatusBadge from "../../StatusBadge.vue";
import { useSettingsRoute } from "../../useSettingsRoute";
import AiProfileFields from "./AiProfileFields.vue";
import { aiModelDiscoveryKey } from "./aiAdminClient";
import { draftDirty, draftFromView, draftToRequest, errorCodeText, validateDraft, type AiModelCatalog, type AiProfileView, type AiTestResult, type ProfileDraft, type ProfileKind } from "./aiAdmin";
import { disclosureHooks } from "../../disclosure";
import type { SavedAiProfile, SavedAiProfiles } from "./aiSavedProfiles";
import t from "../../../../i18n/settings-ai";

const props = defineProps<{ kind: ProfileKind; current: AiProfileView; blocked?: boolean }>();
const emit = defineEmits<{ activated: [profile: AiProfileView] }>();
const router = useSettingsRoute();
const revealId = `saved-ai-${useId()}`;
const opened = ref(false);
const items = ref<SavedAiProfile[]>([]);
const activeVersion = ref<string | null>(null);
const busy = ref(false), loading = ref(false), error = ref(""), notice = ref("");
const generationHref = ref("");
const testResult = ref<{ id: string; result: AiTestResult }>();
const endpoint = computed(() => `/admin/ai/profiles/${props.kind}/saved`);
interface Editor { mode: "new" | "edit" | "snapshot"; entry?: SavedAiProfile; view: AiProfileView; draft: ProfileDraft; name: string; useChat: boolean; activeVersion: string | null; keyNeedsReentry: boolean }
const editor = ref<Editor>();
const confirmation = ref<{ action: "use" | "delete"; entry: SavedAiProfile; activeVersion: string | null }>();
const showErrors = ref(false);
let disposed = false, requestVersion = 0;
let request: AbortController | undefined;
const editing = computed(() => !!editor.value || !!confirmation.value);
const dirty = computed(() => {
  const current = editor.value;
  return !!current && (current.keyNeedsReentry || current.mode !== "edit" || current.name !== current.entry?.name
    || current.useChat !== current.view.useChatProfile || draftDirty(current.draft, current.view));
});
const nameError = computed(() => editor.value && (!editor.value.name.trim() || editor.value.name.trim().length > 200) ? t.saved.nameError : "");
const fieldErrors = computed(() => {
  const current = editor.value;
  if (!current || current.mode === "snapshot" || current.useChat) return {};
  const errors = validateDraft(current.draft, current.view);
  if (current.keyNeedsReentry && !current.draft.apiKey.trim()) errors.apiKey = t.saved.keyAgain;
  return errors;
});
const actionsBlocked = computed(() => !!props.blocked || busy.value || loading.value || editing.value);
function emptyProfile(): AiProfileView {
  return { kind: props.kind, configured: false, enabled: false, provider: null, location: null, baseUrl: null, model: null, modelVersion: null, dimensions: null, queryPrefix: "", documentPrefix: "", timeoutSeconds: 30, maxConcurrency: 4, reasoningEffort: null, temperature: null, nativeJsonSchema: true, requestDimensions: false, useChatProfile: false, inputPricePerMillion: null, outputPricePerMillion: null, hasApiKey: false, modelLockedByIndex: false, version: null };
}
function invalidate() { requestVersion++; request?.abort(); request = undefined; }
function discard() {
  invalidate();
  if (editor.value) editor.value.draft.apiKey = "";
  editor.value = undefined;
  confirmation.value = undefined;
  busy.value = false; loading.value = false; error.value = ""; generationHref.value = "";
}
async function load() {
  invalidate(); const token = requestVersion;
  request = new AbortController(); loading.value = true; error.value = "";
  try {
    const result = await api<SavedAiProfiles>(endpoint.value, { signal: request.signal });
    if (disposed || token !== requestVersion) return;
    items.value = result.items; activeVersion.value = result.activeVersion;
  } catch (failure) { if (!disposed && token === requestVersion) error.value = errorMessage(failure); }
  finally { if (token === requestVersion) loading.value = false; }
}
async function toggle() {
  if (props.blocked || busy.value || dirty.value || editing.value) return;
  opened.value = !opened.value;
  if (opened.value) await load(); else invalidate();
}
function edit(mode: Editor["mode"], entry?: SavedAiProfile) {
  if (actionsBlocked.value) return;
  const view = { ...(entry?.profile ?? emptyProfile()), modelLockedByIndex: false };
  const draft = draftFromView(view);
  if (mode === "new") draft.enabled = true;
  editor.value = { mode, entry, view, draft, name: entry?.name ?? "", useChat: view.useChatProfile, activeVersion: activeVersion.value, keyNeedsReentry: false };
  error.value = ""; notice.value = ""; showErrors.value = false; testResult.value = undefined;
}
async function save() {
  const current = editor.value;
  if (!current || busy.value) return;
  showErrors.value = true;
  if (nameError.value || Object.keys(fieldErrors.value).length) { error.value = t.features.fixHighlighted; return; }
  invalidate(); const token = requestVersion; busy.value = true; error.value = "";
  const enteredKey = !!current.draft.apiKey.trim();
  try {
    // Reusing Chat ignores the hidden custom branch, while the editor retains it for switching back.
    const requestDraft = current.useChat ? { ...draftFromView(emptyProfile()), enabled: current.draft.enabled } : current.draft;
    const payload = { name: current.name.trim(), profile: draftToRequest(requestDraft, current.view, props.kind, current.useChat), expectedVersion: current.entry?.version ?? null };
    const saved = current.mode === "snapshot"
      ? await post<SavedAiProfile>(`${endpoint.value}/from-current`, { name: current.name.trim(), expectedVersion: current.activeVersion })
      : current.entry ? await put<SavedAiProfile>(`${endpoint.value}/${current.entry.id}`, payload) : await post<SavedAiProfile>(endpoint.value, payload);
    if (disposed || token !== requestVersion || editor.value !== current) return;
    const index = items.value.findIndex(item => item.id === saved.id);
    if (index < 0) items.value.push(saved); else items.value[index] = saved;
    editor.value = undefined; notice.value = t.saved.ready;
  } catch (failure) { if (!disposed && token === requestVersion) error.value = errorMessage(failure); }
  finally { current.draft.apiKey = ""; if (token === requestVersion) { if (editor.value === current && enteredKey) current.keyNeedsReentry = true; busy.value = false; } }
}
// The provider applies only below this library, so the active role editor keeps its own discovery context.
provide(aiModelDiscoveryKey, async (kind, body, signal) => {
  const current = editor.value;
  if (!current || kind !== props.kind) throw new Error(t.saved.editorClosed);
  const path = current.entry ? `${endpoint.value}/${current.entry.id}/models` : `${endpoint.value}/models`;
  return api<AiModelCatalog>(path, { method: "POST", body: JSON.stringify(current.entry ? { ...body, expectedSavedVersion: current.entry.version } : body), signal });
});
function confirm(action: "use" | "delete", entry: SavedAiProfile) {
  if (actionsBlocked.value) return;
  confirmation.value = { action, entry, activeVersion: activeVersion.value };
  error.value = ""; generationHref.value = "";
}
async function applyConfirmed() {
  const selected = confirmation.value;
  if (!selected || busy.value) return;
  invalidate(); const token = requestVersion; busy.value = true; error.value = "";
  try {
    if (selected.action === "use") {
      const result = await post<AiProfileView>(`${endpoint.value}/${selected.entry.id}/activate`, { expectedVersion: selected.activeVersion, expectedSavedVersion: selected.entry.version });
      if (disposed || token !== requestVersion) return;
      activeVersion.value = result.version;
      items.value = items.value.map(item => ({ ...item, isActive: item.id === selected.entry.id }));
      emit("activated", result);
      notice.value = t.saved.inUse(selected.entry.name);
    } else {
      await api(`${endpoint.value}/${selected.entry.id}?expectedVersion=${encodeURIComponent(selected.entry.version)}`, { method: "DELETE" });
      if (disposed || token !== requestVersion) return;
      items.value = items.value.filter(item => item.id !== selected.entry.id);
      notice.value = t.saved.removed;
    }
    confirmation.value = undefined;
  } catch (failure) {
    if (disposed || token !== requestVersion) return;
    error.value = errorMessage(failure);
    if (failure instanceof ApiError && failure.code === "ai_generation_required") generationHref.value = router.href("general/modules/ai/index", { action: "replace", savedProfileId: selected.entry.id, savedVersion: selected.entry.version, activeVersion: selected.activeVersion ?? "" });
  } finally { if (token === requestVersion) busy.value = false; }
}
async function test(entry: SavedAiProfile) {
  if (actionsBlocked.value) return;
  invalidate(); const token = requestVersion; busy.value = true; error.value = ""; testResult.value = undefined;
  try {
    const result = await post<AiTestResult>(`${endpoint.value}/${entry.id}/test`, { expectedVersion: entry.version });
    if (!disposed && token === requestVersion) testResult.value = { id: entry.id, result };
  } catch (failure) { if (!disposed && token === requestVersion) error.value = errorMessage(failure); }
  finally { if (token === requestVersion) busy.value = false; }
}
watch(() => props.current.version, (version) => {
  if (opened.value && !editing.value && !busy.value && version !== activeVersion.value) void load();
});
watch(() => props.kind, () => { discard(); opened.value = false; items.value = []; });
onBeforeUnmount(() => { disposed = true; discard(); });
defineExpose({ dirty, editing, busy, save, discard });
</script>

<template>
  <div class="ai-saved" data-ai-saved>
    <div class="st-actions">
      <Button type="button" variant="outline" :aria-expanded="opened" :aria-controls="revealId" :disabled="blocked || busy || editing" data-ai-saved-toggle @click="toggle"><AtlasIcon name="layers" :size="16" />{{ t.saved.title }}<AtlasIcon :name="opened ? 'chevron-up' : 'chevron-down'" :size="14" /></Button>
      <span v-if="blocked && !editing" class="st-note">{{ t.saved.blockedNote }}</span>
    </div>
    <InlineReveal :id="revealId" :open="opened">
      <SettingsSection :title="t.saved.title" :description="t.saved.description">
        <LoadingNote v-if="loading" :label="t.saved.loading" />
        <template v-else>
          <SettingsList v-if="items.length" :label="t.saved.title">
            <SettingsListRow v-for="item in items" :key="item.id" class="ai-saved-profile" :title="item.name" :description="item.profile.useChatProfile ? t.models.usesChat : t.saved.itemDescription(item.profile.model, item.profile.location === 'cloud', item.profile.enabled)" :data-ai-saved-profile="item.id">
              <template v-if="item.isActive" #meta><StatusBadge tone="success" :label="t.saved.active" /></template>
              <template #actions>
                <Button type="button" variant="outline" size="sm" :disabled="actionsBlocked || item.isActive" :aria-label="t.saved.useLabel(item.name)" data-ai-saved-use @click="confirm('use', item)">{{ t.saved.use }}</Button>
                <Button type="button" variant="ghost" size="sm" :disabled="actionsBlocked" :aria-label="t.saved.editLabel(item.name)" data-ai-saved-edit @click="edit('edit', item)">{{ t.setup.edit }}</Button>
                <Button type="button" variant="ghost" size="sm" :disabled="actionsBlocked" :aria-label="t.saved.testLabel(item.name)" data-ai-saved-test @click="test(item)">{{ t.saved.test }}</Button>
                <Button type="button" variant="ghost" size="sm" :disabled="actionsBlocked || item.isActive" :aria-label="t.saved.removeLabel(item.name)" data-ai-saved-delete data-ai-saved-remove @click="confirm('delete', item)"><AtlasIcon name="trash" :size="15" /></Button>
              </template>
              <Transition :css="false" v-bind="disclosureHooks"><p v-if="testResult?.id === item.id" class="st-note" role="status" data-ai-saved-test-result>{{ testResult.result.ok ? t.saved.connectionWorks : errorCodeText(testResult.result.code) }}</p></Transition>
            </SettingsListRow>
          </SettingsList>
          <p v-else class="st-note">{{ t.saved.none }}</p>
          <div class="st-actions">
            <Button type="button" variant="outline" :disabled="actionsBlocked" data-ai-saved-new @click="edit('new')"><AtlasIcon name="plus" :size="16" />{{ t.saved.newProfile }}</Button>
            <Button type="button" variant="ghost" :disabled="actionsBlocked || !current.configured" data-ai-saved-snapshot @click="edit('snapshot')">{{ t.saved.snapshot }}</Button>
          </div>
          <p class="st-note">{{ t.saved.testNote }}</p>
        </template>
        <InlineReveal :open="!!editor">
          <div v-if="editor" class="ai-saved-editor" data-ai-saved-editor>
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.saved.nameLabel" :error="showErrors ? nameError : undefined"><Input :id="id" v-model="editor.name" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="busy" maxlength="200" data-ai-saved-name /></SettingsRow>
            <template v-if="editor.mode !== 'snapshot'">
              <OptionPills v-if="kind === 'quick'" :model-value="editor.useChat ? 'chat' : 'custom'" :options="[{ value: 'custom', label: t.saved.ownModel, disabled: busy }, { value: 'chat', label: t.saved.chatProfile, disabled: busy }]" :label="t.profile.sourceLabel" @update:model-value="editor.useChat = $event === 'chat'" />
              <AiProfileFields v-if="!editor.useChat" :key="editor.entry?.id ?? 'new'" :kind="kind" :draft="editor.draft" :view="editor.view" :errors="showErrors ? fieldErrors : {}" :disabled="busy" />
              <p v-else class="st-note">{{ t.saved.quickUsesChat }}</p>
            </template>
            <p v-else class="st-note">{{ t.saved.snapshotNote }}</p>
            <SaveBar :dirty="dirty" :saving="busy" :error="error" :save-label="t.saved.save" @save="save" @cancel="discard" />
            <Button v-if="!dirty" type="button" variant="ghost" :disabled="busy" data-ai-saved-close @click="discard">{{ t.saved.closeEditor }}</Button>
          </div>
        </InlineReveal>
        <Transition :css="false" v-bind="disclosureHooks"><p v-if="error && !confirmation && !editor" class="ai-saved-error" role="alert">{{ error }} <Button type="button" variant="ghost" size="sm" :disabled="busy" @click="load">{{ t.features.reload }}</Button></p></Transition>
        <Transition :css="false" v-bind="disclosureHooks"><p v-if="notice" class="st-note" role="status">{{ notice }}</p></Transition>
      </SettingsSection>
    </InlineReveal>
    <ConfirmDialog :open="!!confirmation" :title="confirmation?.action === 'use' ? t.saved.useTitle : t.saved.removeTitle" :confirm-label="confirmation?.action === 'use' ? t.saved.useProfile : t.saved.removeProfile" :destructive="confirmation?.action === 'delete'" :busy="busy" :error="error" @confirm="applyConfirmed" @cancel="discard">
      <template v-if="confirmation">
        <p><strong>{{ confirmation.entry.name }}</strong> · {{ confirmation.entry.profile.model }}</p>
        <template v-if="confirmation.action === 'use'">
          <p v-if="confirmation.entry.profile.location === 'cloud'">{{ t.saved.confirmCloud }}</p>
          <p v-else-if="confirmation.entry.profile.useChatProfile">{{ t.saved.confirmChat }}</p>
          <p v-else>{{ t.saved.confirmCompany }}</p>
          <p>{{ confirmation.entry.profile.enabled ? t.saved.confirmSwitch : t.saved.confirmOff }}</p>
        </template>
        <p v-else>{{ t.saved.confirmRemove }}</p>
        <Button v-if="generationHref" as-child variant="outline"><a :href="generationHref" data-ai-saved-index>{{ t.saved.buildIndex }}</a></Button>
      </template>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
.ai-saved-editor { display: grid; gap: 12px; min-width: 0; }
/* The closed library still took the gap under its button (28 px to the profile card instead of 16). The space lives
   inside the reveal and unfolds with it; a column of flex items, because a grid track cannot give a gap back. */
.ai-saved { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
.ai-saved > .inline-reveal-root { margin-top: -12px; }
.ai-saved > .inline-reveal-root > :deep(.inline-reveal > .inline-reveal-inner) { padding-top: 12px; }
/* "Zavřít editor" is a quiet text action under the form, not a full-width bar. */
.ai-saved-editor > [data-ai-saved-close] { justify-self: start; }
.ai-saved-error { color: var(--error); margin: 0; font-size: 13px; }
/* Four actions need their own row beside a narrow profile description and status. These rows have no leading icon. */
@container (max-width: 620px) {
  .ai-saved-profile :deep(.settings-list-row-main) { grid-column: 1 / -1; grid-row: 1; }
  .ai-saved-profile :deep(.settings-list-row-meta) { grid-column: 1 / -1; grid-row: 2; padding-left: 0; }
  .ai-saved-profile :deep(.settings-list-row-actions) { grid-column: 1 / -1; grid-row: 3; justify-content: flex-start; flex-wrap: wrap; min-width: 0; padding-left: 0; }
  .ai-saved-profile :deep(.settings-list-row-line:not(:has(> .settings-list-row-meta)) .settings-list-row-actions) { grid-row: 2; padding-left: 0; }
}
</style>
