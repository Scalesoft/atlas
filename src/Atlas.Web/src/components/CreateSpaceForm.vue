<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import { Button } from "./ui/button";
import { adminApi, type Group, type SpaceRole } from "../administration";
import { errorMessage, post, type Space, type SpacePinPolicy } from "../api";
import { spaceCoverApi, type SpaceCover } from "../spaceCover";
import AtlasIcon from "./AtlasIcon.vue";
import ErrorState from "./settings/ErrorState.vue";
import InlineReveal from "./settings/InlineReveal.vue";
import LoadingNote from "./settings/LoadingNote.vue";
import OptionPills from "./settings/OptionPills.vue";
import t from "../i18n/components-creation";
import SearchableChoiceList from "./settings/SearchableChoiceList.vue";
import SettingsPage from "./settings/SettingsPage.vue";
import SettingsSection from "./settings/SettingsSection.vue";
import SubjectAvatar from "./settings/SubjectAvatar.vue";
import SpaceProfileFields from "./settings/space/SpaceProfileFields.vue";
import { spaceRoleOptions } from "./settings/space/spaceAccess";
import type { ChoiceOption } from "./settings/types";
// A workspace view outside the settings shell: it brings the design system's styles itself.
import "./settings/settings.css";
import "./settings/settings-tailwind.css";

/**
 * Nový prostor: the fields of Nastavení prostoru → Obecné (SpaceProfileFields, the image included), then optional
 * group roles granted in the same request (docs/space-creation-design.md), then an explicit Vytvořit prostor.
 * The image is chosen before the space exists, so it is kept here and saved right after the space is created.
 */
const emit = defineEmits<{ created: [space: Space, warning?: string]; cancel: []; busy: [value: boolean]; dirty: [value: boolean] }>();
const props = defineProps<{ disabled?: boolean }>();
const fields = ref<InstanceType<typeof SpaceProfileFields>>();
const initial = { name: "", description: "", icon: "book-open", color: "blue", pinPolicy: "None" as SpacePinPolicy };
const draft = reactive({ ...initial });

const automatic: SpaceCover = { source: "Auto", catalogKey: null, version: null, tinted: true };
const cover = ref<SpaceCover>(automatic);
const coverFile = ref<File>();
/** The draft's image onto the new space. Auto is what the server starts with, so it needs no request. */
async function saveCover(spaceId: string): Promise<SpaceCover | undefined> {
  const value = cover.value;
  if (value.source === "Upload" && coverFile.value) {
    const uploaded = await spaceCoverApi.upload(spaceId, coverFile.value);
    return value.tinted ? uploaded : spaceCoverApi.setTinted(spaceId, false);
  }
  if (value.source === "Catalog" || value.source === "None") return spaceCoverApi.choose(spaceId, { source: value.source, catalogKey: value.catalogKey ?? undefined });
  return undefined;
}

const groups = ref<Group[]>([]);
const selected = ref<{ groupId: string; role: SpaceRole }[]>([]);
const picking = ref(false);
const groupQuery = ref("");
const loading = ref(false);
const busy = ref(false);
const error = ref("");
const nameError = ref("");
const groupsError = ref("");
let alive = true;

const groupName = (id: string) => groups.value.find((group) => group.id === id)?.name ?? t.space.group;
const available = computed<ChoiceOption[]>(() =>
  groups.value.filter((group) => !selected.value.some((row) => row.groupId === group.id)).map((group) => ({ value: group.id, label: group.name, icon: "users" })),
);
const accessSummary = computed(() => {
  const count = selected.value.length;
  return count === 0 ? t.space.onlyYou : t.space.groups(count);
});
const dirty = computed(() =>
  (Object.keys(initial) as (keyof typeof initial)[]).some((key) => draft[key].trim() !== initial[key]) || cover.value.source !== "Auto" || selected.value.length > 0);
watch(dirty, (value) => emit("dirty", value));

async function loadGroups() {
  if (loading.value) return;
  loading.value = true;
  groupsError.value = "";
  try { const result = await adminApi.groups(); if (alive) groups.value = result; }
  catch (cause) { if (alive) groupsError.value = errorMessage(cause); }
  finally { if (alive) loading.value = false; }
}
function addGroup(groupId: string) {
  if (selected.value.length >= 100 || selected.value.some((row) => row.groupId === groupId)) return;
  selected.value.push({ groupId, role: "Reader" });
  picking.value = false;
  groupQuery.value = "";
}
function cancel() {
  if (busy.value) return;
  emit("cancel");
}
/** Esc is Zrušit (keyboard pass), unless a field, list or picker inside used it first (they stop it or prevent it). */
function escape(event: KeyboardEvent) {
  if (event.defaultPrevented || event.isComposing) return;
  event.preventDefault();
  cancel();
}
async function submit() {
  if (busy.value || props.disabled) return;
  nameError.value = draft.name.trim() ? "" : t.space.nameRequired;
  if (nameError.value) { fields.value?.focusName(); return; }
  error.value = "";
  busy.value = true;
  emit("busy", true);
  let space: Space | undefined;
  let warning: string | undefined;
  try {
    space = await post<Space>("/spaces", { ...draft, name: draft.name.trim(), description: draft.description.trim(), groups: selected.value.map(row => ({ ...row })) });
    // The space exists now: an image the server refuses must not undo it, it is only reported.
    try { const saved = await saveCover(space.id); if (saved) space = { ...space, cover: saved }; }
    catch (cause) { warning = t.space.coverFailed(errorMessage(cause)); }
  }
  catch (cause) { if (alive) error.value = errorMessage(cause); }
  finally { busy.value = false; if (alive) emit("busy", false); }
  if (alive && space) { emit("dirty", false); if (warning) emit("created", space, warning); else emit("created", space); }
}
onMounted(() => { void loadGroups(); void nextTick(() => fields.value?.focusName()); });
onUnmounted(() => { alive = false; if (busy.value) emit("busy", false); emit("dirty", false); });
</script>

<template>
  <!-- A form so Enter in the name field creates the space as well. -->
  <form class="space-creation settings-ui" :aria-label="t.space.title" novalidate :aria-busy="busy" @submit.prevent="submit" @keydown.esc="escape">
    <SettingsPage :title="t.space.title" icon="folder-plus" :description="t.space.description">
      <div class="space-creation-body">
        <SpaceProfileFields
          ref="fields" v-model:name="draft.name" v-model:description="draft.description" v-model:icon="draft.icon" v-model:color="draft.color" v-model:pin-policy="draft.pinPolicy"
          space-id="" deferred :disabled="busy || disabled" :name-error="nameError" @cover-change="(value, file) => { cover = value; coverFile = file; }"
        />
        <SettingsSection :title="t.space.access" icon="users" :summary="accessSummary" :description="t.space.accessDescription">
          <ErrorState v-if="groupsError" :title="t.space.groupsError" :text="groupsError" :busy="loading" data-groups-error @retry="loadGroups" />
          <LoadingNote v-else-if="loading" :label="t.space.groupsLoading" />
          <p v-else-if="!groups.length" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.space.noGroups }}</p>
          <TransitionGroup v-if="selected.length" tag="ul" name="st-list" class="space-creation-groups" :aria-label="t.space.selectedGroups">
            <li v-for="(row, index) in selected" :key="row.groupId" class="space-creation-group" :data-group-row="row.groupId">
              <SubjectAvatar kind="group" :name="groupName(row.groupId)" size="sm" />
              <strong>{{ groupName(row.groupId) }}</strong>
              <OptionPills v-model="row.role" :options="spaceRoleOptions" :label="t.space.groupRole(groupName(row.groupId))" hide-label :data-group-role="index + 1" />
              <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.space.removeGroup(groupName(row.groupId))" :disabled="busy" @click="selected.splice(index, 1)"><AtlasIcon name="close" :size="16" /></Button>
            </li>
          </TransitionGroup>
          <InlineReveal :open="picking">
            <SearchableChoiceList v-if="picking" v-model:query="groupQuery" :model-value="null" :options="available" :label="t.space.addGroup" :placeholder="t.space.searchGroup" :max-height="260" data-group-picker @pick="addGroup" />
          </InlineReveal>
          <div v-if="groups.length && available.length" class="st-actions">
            <Button v-if="!picking" variant="outline" size="sm" type="button" data-action="add-group" :disabled="busy || selected.length >= 100" @click="picking = true"><AtlasIcon name="plus" :size="15" />{{ t.space.addGroup }}</Button>
            <Button v-else variant="ghost" size="sm" type="button" @click="picking = false">{{ t.space.closePicker }}</Button>
          </div>
          <p class="st-note"><AtlasIcon name="lock" :size="15" />{{ selected.length ? t.space.withGroups : t.space.withoutGroups }}</p>
        </SettingsSection>
        <p v-if="error" class="space-creation-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
        <div class="st-actions is-end">
          <Button variant="ghost" type="button" :disabled="busy" data-space-cancel @click="cancel"><AtlasIcon name="close" :size="16" />{{ t.space.cancel }}</Button>
          <Button type="submit" :disabled="busy || disabled" data-space-create><AtlasIcon :name="busy ? 'loader-circle' : 'plus'" :size="16" :class="{ 'atlas-spinner': busy }" />{{ t.space.create }}</Button>
        </div>
      </div>
    </SettingsPage>
  </form>
</template>

<style scoped>
/* A form keeps a readable measure and sits in the middle of the workspace (the margin is repeated in refresh-library.css, which outranks the shell's generic workspace-child rule); the gutter is the page frame's. */
.space-creation { width: 100%; max-width: 1080px; min-width: 0; margin: 0 auto; padding: 20px var(--atlas-page-gutter, 24px) 40px; }
@media (max-width: 680px) { .space-creation { padding: 16px 16px 72px; } }
.space-creation-body { display: grid; gap: 18px; min-width: 0; }
.space-creation-groups { display: grid; gap: 8px; margin: 0; padding: 0; list-style: none; }
.space-creation-group { display: grid; grid-template-columns: auto minmax(120px, 1fr) auto auto; align-items: center; gap: 8px 12px; padding: 8px 10px; border: 1px solid var(--line); border-radius: var(--st-radius-sm); background: var(--surface); }
.space-creation-group > strong { font-size: 13.5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@media (max-width: 680px) {
  .space-creation-group { grid-template-columns: auto minmax(0, 1fr) auto; }
  .space-creation-group > :deep(.option-pills) { grid-column: 1 / -1; grid-row: 2; }
}
.space-creation-error { display: flex; align-items: center; gap: 8px; margin: 0; color: var(--error); font-size: 13px; }
</style>
