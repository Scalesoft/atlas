<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { adminApi, type AiAccessGroup, type AiAccessView, type Group } from "../../../../administration";
import { Button } from "../../../ui/button";
import { Switch } from "../../../ui/switch";
import SettingsRow from "../../SettingsRow.vue";
import SettingsSection from "../../SettingsSection.vue";
import SettingsList from "../../SettingsList.vue";
import SettingsListRow from "../../SettingsListRow.vue";
import SearchableChoiceList from "../../SearchableChoiceList.vue";
import InlineReveal from "../../InlineReveal.vue";
import { disclosureHooks } from "../../disclosure";
import SaveBar from "../../SaveBar.vue";
import LoadingNote from "../../LoadingNote.vue";
import ErrorState from "../../ErrorState.vue";
import ConflictNote from "../../ConflictNote.vue";
import { useDraftGuard } from "../../dirtyGuard";
import { useSettingsDocument } from "../../useSettingsDocument";
import AiQuotaFields from "./AiQuotaFields.vue";
import { emptyQuotas, quotaSummary, validQuotas } from "./aiAccess";
import { useSettingsRoute } from "../../useSettingsRoute";
import t from "../../../../i18n/settings-ai";
const router = useSettingsRoute();
const props = withDefaults(defineProps<{ client?: Pick<typeof adminApi, "groups" | "aiAccess" | "setAiAccess"> }>(), { client: () => adminApi });
const emit = defineEmits<{ saved: [] }>();
/** What one load reads: the access together with the groups that can be added to it. */
interface Loaded { access: AiAccessView; groups: Group[] }
const draft = ref<AiAccessGroup[]>([]);
const adding = ref(false), expanded = ref<string[]>([]);
const { saved: loaded, loading, loadError: error, saving, saveError, conflict, dirty, load, save: saveDocument, reset: discard, reloadAfterConflict } = useSettingsDocument<Loaded>({
  load: async () => { const [access, candidates] = await Promise.all([props.client.aiAccess(), props.client.groups()]); return { access, groups: candidates }; },
  save: async (current): Promise<Loaded> => ({ access: await props.client.setAiAccess(draft.value.map(({ groupName: _, ...group }) => group), current.access.version), groups: current.groups }),
  fill: (current) => { draft.value = current.access.groups.map(group => ({ ...group })); },
  dirty: (current): boolean => JSON.stringify(draft.value) !== JSON.stringify(current.access.groups),
  beforeSave: () => draft.value.every(validQuotas) ? undefined : t.access.invalidQuotas,
  onSaved: () => emit("saved"),
  clearConflictOnSave: true,
  quietConflict: true,
});
const saved = computed(() => loaded.value?.access);
const groups = computed(() => loaded.value?.groups ?? []);
const options = computed(() => groups.value.filter(group => !draft.value.some(item => item.groupId === group.id)).map(group => ({ value: group.id, label: group.name, icon: "users" })));
function add(id: string) {
  const group = groups.value.find(item => item.id === id);
  if (!group || saving.value || draft.value.some(item => item.groupId === id)) return;
  // D20: a new grant has the "AI s nástroji" right; the admin can turn it off per group.
  draft.value.push({ groupId: id, groupName: group.name, ...emptyQuotas(), toolsEnabled: true }); adding.value = false;
}
function save() { if (!loading.value && dirty.value) return saveDocument(); }
useDraftGuard(dirty, { save, discard });
onMounted(load);
</script>
<template>
  <div class="ai-access">
    <ConflictNote v-if="conflict" data-ai-access-conflict :text="t.access.conflict" :busy="loading || saving" @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="error" :title="t.access.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingNote v-if="loading && !saved" :label="t.access.loading" />
    <SettingsSection v-if="saved" :title="t.access.sectionTitle" icon="users" :description="t.access.sectionDescription">
      <template #actions><Button size="sm" variant="outline" :disabled="saving || loading" data-ai-add-group @click="adding = !adding">{{ t.access.addGroup }}</Button></template>
      <InlineReveal :open="adding" class="reveal-tail">
        <!-- A fresh wiki has no group yet: the step leads to creating one instead of ending in an empty list. -->
        <p v-if="adding && !groups.length" class="st-note" data-ai-no-groups>{{ t.access.noGroups }} <Button as-child variant="link" size="sm"><a :href="router.href('people/groups/new')">{{ t.access.newGroup }}</a></Button></p>
        <SearchableChoiceList v-else-if="adding" :options="options" :label="t.access.pickLabel" :placeholder="t.access.pickPlaceholder" :empty-text="t.access.pickEmpty" @pick="add" />
      </InlineReveal>
      <Transition :css="false" v-bind="disclosureHooks"><p v-if="!draft.length" class="st-note">{{ t.access.noneYet }}</p></Transition>
      <SettingsList v-if="draft.length" :label="t.access.listLabel">
        <SettingsListRow v-for="group in draft" :key="group.groupId" :title="group.groupName" :description="(group.toolsEnabled ? '' : t.access.withoutTools) + quotaSummary(group)" icon="users" :data-ai-access-group="group.groupId">
          <template #actions>
            <Button variant="ghost" size="sm" :aria-expanded="expanded.includes(group.groupId)" data-ai-group-limits @click="expanded = expanded.includes(group.groupId) ? expanded.filter(id => id !== group.groupId) : [...expanded, group.groupId]">{{ t.access.edit }}</Button>
            <Button variant="ghost" size="sm" :disabled="saving || loading" :aria-label="t.access.removeLabel(group.groupName)" @click="draft = draft.filter(item => item.groupId !== group.groupId)">{{ t.access.remove }}</Button>
          </template>
          <InlineReveal :open="expanded.includes(group.groupId)">
            <SettingsRow v-slot="{ id, describedBy }" :label="t.access.toolsLabel" :help="t.access.toolsHelp" class="ai-tools-row">
              <Switch :id="id" v-model="group.toolsEnabled" :aria-describedby="describedBy" :disabled="saving || loading" data-ai-group-tools />
            </SettingsRow>
            <AiQuotaFields :draft="group" :disabled="saving || loading" />
          </InlineReveal>
        </SettingsListRow>
      </SettingsList>
      <p class="st-note">{{ t.access.footnote }}</p>
    </SettingsSection>
    <SaveBar :dirty="dirty" :saving="saving" :error="saveError || undefined" :save-label="t.access.saveLabel" @save="save" @cancel="discard" />
  </div>
</template>
<style scoped>
.ai-access { display: grid; gap: 16px; min-width: 0; container: ai-access / inline-size; }
/* A group's limits belong to it: they start under its title (32 px tile + 12 px gap) instead of taking the list's row
   inset a second time inside the row, which put the labels 18 px right of the tile and nowhere in particular. */
.ai-access :deep(.settings-list-row :is(.ai-quota-fields, .ai-tools-row)) { --st-row-pad-x: 0px; margin-inline-start: 44px; }
/* The section header wraps when its own room is narrow (phone, or a page squeezed by the dock), not by the screen. */
@container ai-access (max-width: 560px) {
  .ai-access :deep(.settings-section-header) { flex-wrap: wrap; align-items: flex-start; }
  .ai-access :deep(.settings-section-heading) { flex-basis: calc(100% - 44px); }
  .ai-access :deep(.settings-section-actions) { margin-left: 44px; }
  .ai-access :deep(.settings-list-row :is(.ai-quota-fields, .ai-tools-row)) { margin-inline-start: 0; }
}
</style>
