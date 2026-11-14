<script setup lang="ts">
import { computed, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import ActionDisclosure from "../../ActionDisclosure.vue";
import ActionDisclosureItem from "../../ActionDisclosureItem.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import { vaultApi, vaultLocationKinds, type VaultLocation, type VaultLocationDraft } from "../../../vaults";
import ConfirmDialog from "../ConfirmDialog.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { useDirtyGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-global";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import VaultLocationWizard from "../vaults/VaultLocationWizard.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";

// Umístění souborů: the folders where vault files live (wiki administrator only). A location is
// added or edited in the location wizard at `general/files/new` or `general/files/<id>`.
const props = defineProps<{ editing?: string }>();
const router = useSettingsRoute();
const registry = useVaultRegistry();
const flash = useSettingsFlash();
const guard = useDirtyGuard();

const editedLocation = computed(() => (props.editing && props.editing !== "new" ? registry.locations.value.find((item) => item.id === props.editing) : undefined));
const wizardOpen = computed(() => props.editing === "new" || !!editedLocation.value);

const test = async (draft: VaultLocationDraft) => (await vaultApi.testLocation(draft, editedLocation.value?.id ?? null)).message;
const submit = (draft: VaultLocationDraft) => vaultApi.saveLocation(editedLocation.value?.id ?? null, draft);
function done(location: VaultLocation) {
  const added = !editedLocation.value;
  registry.saveLocation(location);
  if (guard) guard.dirty.value = false;
  router.navigate("general/files");
  flash.notify(added ? t.locations.added(location.name) : t.locations.saved(location.name), { highlight: location.id });
}
function cancel() {
  if (guard) guard.dirty.value = false;
  router.navigate("general/files");
}

const removing = ref<VaultLocation>();
const removeBusy = ref(false);
const removeError = ref("");
async function remove() {
  const location = removing.value;
  if (!location) return;
  removeBusy.value = true;
  removeError.value = "";
  try {
    await vaultApi.archiveLocation(location.id);
    registry.removeLocation(location.id);
    removing.value = undefined;
    flash.notify(t.locations.removed(location.name), { icon: "trash" });
  } catch (failure) {
    removeError.value = errorMessage(failure);
  } finally {
    removeBusy.value = false;
  }
}
</script>

<template>
  <SettingsPage :title="t.locations.title" :description="t.locations.description" :back="wizardOpen ? { href: router.href('general/files'), label: t.locations.back } : undefined">
    <template v-if="!wizardOpen" #actions>
      <Button as-child><a :href="router.href('general/files/new')" data-add-location><AtlasIcon name="folder-plus" :size="16" />{{ t.locations.add }}</a></Button>
    </template>

    <!-- The wizard is a card of its own; a frame around it made it a band of an empty outer card, with a stray line on top. -->
    <VaultLocationWizard v-if="wizardOpen" :location="editedLocation" :test="test" :submit="submit" @done="done" @cancel="cancel" @dirty="guard && (guard.dirty.value = $event)" />
    <EmptyState v-else-if="editing && registry.loaded.value" icon="folder" :title="t.locations.notFoundTitle" :text="t.locations.notFoundText">
      <Button as-child variant="outline"><a :href="router.href('general/files')"><AtlasIcon name="back" :size="16" />{{ t.locations.back }}</a></Button>
    </EmptyState>
    <template v-else>
      <ErrorState v-if="registry.error.value" :title="t.locations.loadFailed" :text="registry.error.value" :busy="registry.loading.value" @retry="registry.load()" />
      <LoadingSkeleton v-else-if="!registry.loaded.value" :label="t.locations.loading" />
      <SettingsSection v-else-if="registry.locations.value.length" :label="t.locations.section(registry.locations.value.length)">
        <SettingsList :label="t.locations.listLabel">
          <SettingsListRow
            v-for="location in registry.locations.value"
            :key="location.id"
            :title="location.name"
            :icon="vaultLocationKinds[location.kind].icon"
            :highlighted="flash.highlight.value === location.id"
            :data-location="location.id"
          >
            <template #description><code class="st-mono location-root">{{ location.root }}</code></template>
            <template #meta>
              <StatusBadge :label="vaultLocationKinds[location.kind].badge" :icon="vaultLocationKinds[location.kind].icon" size="sm" />
              <StatusBadge tone="neutral" icon="lock" :label="t.counts.vaults(location.vaultCount)" size="sm" />
              <StatusBadge v-if="location.kind === 'smb'" :tone="location.hasPassword ? 'success' : 'neutral'" icon="key-round" :label="location.hasPassword ? t.locations.passwordSaved : t.locations.noPassword" size="sm" />
            </template>
            <template #actions>
              <Button variant="ghost" size="sm" as-child><a :href="router.href(`general/files/${location.id}`)" :data-edit-location="location.id"><AtlasIcon name="pencil" :size="15" />{{ t.locations.edit }}</a></Button>
              <ActionDisclosure :label="t.locations.moreActions(location.name)">
                <ActionDisclosureItem :data-remove-location="location.id" @click="removing = location; removeError = ''"><AtlasIcon name="trash" :size="15" />{{ t.locations.remove }}</ActionDisclosureItem>
              </ActionDisclosure>
            </template>
          </SettingsListRow>
        </SettingsList>
      </SettingsSection>
      <EmptyState v-else icon="folder" :title="t.locations.emptyTitle" :text="t.locations.emptyText">
        <Button as-child><a :href="router.href('general/files/new')"><AtlasIcon name="folder-plus" :size="16" />{{ t.locations.add }}</a></Button>
      </EmptyState>
    </template>

    <ConfirmDialog :open="!!removing" :title="t.locations.confirmTitle" :confirm-label="t.locations.confirmLabel" confirm-icon="trash" :busy="removeBusy" :error="removeError" @confirm="remove" @cancel="removing = undefined">
      <p>{{ t.locations.confirmBefore }} <strong>{{ removing?.name }}</strong> {{ t.locations.confirmAfter }}</p>
      <p v-if="removing?.vaultCount">{{ t.locations.inUse(t.counts.vaults(removing.vaultCount)) }}</p>
    </ConfirmDialog>
  </SettingsPage>
</template>

<style scoped>
.location-root { overflow-wrap: anywhere; }
</style>
