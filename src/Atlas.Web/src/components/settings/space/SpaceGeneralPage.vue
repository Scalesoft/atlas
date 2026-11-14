<script setup lang="ts">
import t from "../../../i18n/settings-space";
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import { ApiError, errorMessage, type SpacePinPolicy } from "../../../api";
import { spaceSettingsApi, spaceTrashApi, type SpaceSettings } from "../../../spaceSettings";
import { Button } from "../../ui/button";
import { Input } from "../../ui/input";
import { Switch } from "../../ui/switch";
import AtlasIcon from "../../AtlasIcon.vue";
import OptionPills from "../OptionPills.vue";
import { publicationLabels, type PublicationMode } from "../../../publishing/pagePublication";
import ConfirmDialog from "../ConfirmDialog.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import type { SpaceCover } from "../../../spaceCover";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SaveBar from "../SaveBar.vue";
import SettingsPage from "../SettingsPage.vue";
import { useSettingsContext } from "../context";
import { useDraftGuard } from "../dirtyGuard";
import SpaceProfileFields from "./SpaceProfileFields.vue";

// Obecné of a space: name, description, icon, colour and pin policy with the loaded version as the CAS token.
// A conflict keeps the draft and offers "Načíst znovu", which discards it for the current values (the
// API cannot force a save, so there is no "Přepsat"); the sidebar learns the saved value.
// The fields are SpaceProfileFields, shared with Nový prostor; the image of the space is saved on its own, at once
// (SpaceCoverField), outside the draft and its save bar.
const props = defineProps<{ spaceId: string }>();
const context = useSettingsContext();

const baseline = ref<SpaceSettings>();
const loading = ref(true);
const saving = ref(false);
const loadError = ref("");
const saveError = ref("");
const nameError = ref("");
const conflict = ref("");
const draft = reactive({ name: "", icon: "book-open", color: "blue", description: "", pinPolicy: "None" as SpacePinPolicy, commentsEnabled: true, publicationMode: "Manual" as PublicationMode });
const fields = ["name", "icon", "color", "description", "pinPolicy", "commentsEnabled", "publicationMode"] as const;
const publicationOptions = (Object.keys(publicationLabels) as PublicationMode[]).map((value) => ({ value, label: publicationLabels[value] }));
/** An older server leaves the comments switch out: comments are on. */
const baselineOf = (value: SpaceSettings) => ({ ...value, commentsEnabled: value.commentsEnabled ?? true, publicationMode: value.publicationMode ?? "Manual" });
const dirty = computed(() => !!baseline.value && fields.some((key) => draft[key] !== baselineOf(baseline.value!)[key]));
/** The image is no part of the draft: it is saved on its own and only reported to the shell. */
const cover = ref<SpaceCover>();
const pageCount = computed(() => context.spaces?.find((space) => space.id === props.spaceId)?.pageCount ?? 0);
function coverChanged(value: SpaceCover) {
  cover.value = value;
  if (baseline.value) context.spaceChanged({ ...baseline.value, cover: value });
}

let attempt = 0;
let abort: AbortController | undefined;
function apply(value: SpaceSettings) {
  baseline.value = value;
  cover.value = value.cover ?? undefined;
  Object.assign(draft, { name: value.name, icon: value.icon, color: value.color, description: value.description, pinPolicy: value.pinPolicy, commentsEnabled: value.commentsEnabled ?? true, publicationMode: value.publicationMode ?? "Manual" });
  nameError.value = "";
  saveError.value = "";
}
async function load() {
  const current = ++attempt;
  abort?.abort();
  abort = new AbortController();
  loading.value = true;
  loadError.value = "";
  try {
    const value = await spaceSettingsApi.get(props.spaceId, abort.signal);
    if (current === attempt) apply(value);
  } catch (failure) {
    if (current !== attempt) return;
    loadError.value = errorMessage(failure);
    baseline.value = undefined;
  } finally {
    if (current === attempt) loading.value = false;
  }
}
watch(() => props.spaceId, () => {
  baseline.value = undefined;
  saving.value = false;
  conflict.value = "";
  void load();
}, { immediate: true });

async function save() {
  if (!baseline.value || loading.value || saving.value || !dirty.value) return;
  nameError.value = draft.name.trim() ? "" : t.general.nameRequired;
  if (nameError.value) {
    saveError.value = t.general.fixField;
    return;
  }
  const current = attempt;
  saving.value = true;
  saveError.value = "";
  conflict.value = "";
  try {
    const value = await spaceSettingsApi.save(props.spaceId, { ...draft, name: draft.name.trim(), expectedVersion: baseline.value.version });
    if (current !== attempt) return;
    // The form never sends the image; keep the one saved meanwhile if the answer leaves it out.
    const saved = { ...value, cover: value.cover ?? cover.value };
    apply(saved);
    context.spaceChanged(saved);
  } catch (failure) {
    if (current !== attempt) return;
    if (failure instanceof ApiError && failure.status === 409) {
      conflict.value = t.general.conflict;
      saveError.value = t.general.notSaved;
    } else if (failure instanceof ApiError && [401, 403, 404].includes(failure.status)) {
      // Access to the space is gone: nothing left to edit here.
      baseline.value = undefined;
      loadError.value = errorMessage(failure);
    } else saveError.value = errorMessage(failure);
  } finally {
    saving.value = false;
  }
}
function discard() {
  if (baseline.value) apply(baseline.value);
}
/** The explicit way out of a conflict: the draft goes, the current values come. */
function reloadAfterConflict() {
  conflict.value = "";
  void load();
}

// Přesunout prostor do koše: the wiki admin's, confirmed by typing the name; the space and its pages wait in Koš prostorů.
const trashOpen = ref(false);
const trashName = ref("");
const trashing = ref(false);
const trashError = ref("");
const trashReady = computed(() => !!baseline.value && trashName.value.trim() === baseline.value.name);
function openTrash() {
  trashName.value = "";
  trashError.value = "";
  trashOpen.value = true;
}
async function trash() {
  if (!baseline.value || !trashReady.value || trashing.value) return;
  trashing.value = true;
  trashError.value = "";
  try {
    await spaceTrashApi.trash(props.spaceId, trashName.value);
    trashOpen.value = false;
    context.spaceTrashed(props.spaceId);
  } catch (failure) {
    trashError.value = errorMessage(failure);
  } finally {
    trashing.value = false;
  }
}

useDraftGuard(dirty, { save, discard });
onBeforeUnmount(() => {
  attempt++;
  abort?.abort();
});
</script>

<template>
  <SettingsPage :title="t.general.title" icon="settings" :description="t.general.description">
    <ConflictNote v-if="conflict" :text="conflict" :reload-label="t.general.reload" :busy="loading || saving" data-space-conflict @reload="reloadAfterConflict" @dismiss="conflict = ''" />
    <ErrorState v-if="loadError" :title="t.general.loadFailed" :text="loadError" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-if="loading && !baseline" :label="t.general.loading" />
    <form v-if="baseline" class="space-general" :aria-busy="saving" @submit.prevent="save">
      <SpaceProfileFields
        v-model:name="draft.name" v-model:description="draft.description" v-model:icon="draft.icon" v-model:color="draft.color" v-model:pin-policy="draft.pinPolicy"
        :space-id="spaceId" :page-count="pageCount" :cover="cover" :disabled="saving" :name-error="nameError" @cover-change="coverChanged"
      />

      <SettingsSection :label="t.general.comments">
        <SettingsRow v-slot="{ id }" :label="t.general.comments" :help="t.general.commentsHelp">
          <Switch :id="id" v-model="draft.commentsEnabled" data-space-comments />
        </SettingsRow>
        <SettingsRow :label="t.general.publication" :help="t.general.publicationHelp" :label-for="false">
          <OptionPills v-model="draft.publicationMode" :label="t.general.publication" hide-label :options="publicationOptions" data-space-publication />
        </SettingsRow>
      </SettingsSection>

      <SettingsSection v-if="context.isWikiAdmin" :title="t.general.trashTitle" icon="trash" data-space-trash>
        <SettingsRow :label="t.general.trashRow" :help="t.general.trashHelp" :label-for="false">
          <Button variant="outline" type="button" class="space-trash-button" :disabled="saving" data-space-trash-open @click="openTrash"><AtlasIcon name="trash" :size="15" />{{ t.general.trashButton }}</Button>
        </SettingsRow>
      </SettingsSection>

      <!-- display: contents keeps the bar sticky to the form while giving tests a stable hook. -->
      <div class="space-save" data-space-save>
        <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.general.save" @save="save" @cancel="discard" />
      </div>
    </form>
    <ConfirmDialog :open="trashOpen" :title="t.general.trashDialog" :confirm-label="t.general.trashConfirm" confirm-icon="trash" :busy="trashing" :error="trashError || undefined" :confirm-disabled="!trashReady" @confirm="trash" @cancel="trashOpen = false">
      <p>{{ t.general.trashLead }} <strong>{{ baseline?.name }}</strong> {{ t.general.trashTail }}</p>
      <SettingsRow v-slot="{ id }" :label="t.general.trashType" stacked>
        <Input :id="id" v-model="trashName" autocomplete="off" :placeholder="baseline?.name" data-space-trash-name @keydown.enter.prevent="trash" />
      </SettingsRow>
    </ConfirmDialog>
  </SettingsPage>
</template>

<style scoped>
.space-general { display: grid; gap: 18px; min-width: 0; }
.space-save { display: contents; }
.space-trash-button { justify-self: end; width: fit-content; color: var(--error); }
</style>
