<script setup lang="ts">
import { computed } from "vue";
import EmptyState from "../EmptyState.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import type { ChoiceOption } from "../types";
import { managedSpaces, useSettingsContext } from "../context";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-space";
import tv from "../../../i18n/settings-vaults";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import VaultWizard from "../vaults/VaultWizard.vue";
import type { VaultFileLister, VaultWizardDraft, VaultWizardLocation, VaultWizardResult } from "../vaults/types";
import { spaceSettingsHash } from "../../../settingsRoutes";
import { vaultApi, type VaultLocationDraft } from "../../../vaults";
import LoadingSkeleton from "../../LoadingSkeleton.vue";

// "Přidat trezor" on the vault API: inside a space the space step is skipped; from global settings
// the wizard asks for the space first. Only the wiki administrator may add a new location on the way.
const props = defineProps<{ spaceId?: string; backPath: string }>();
const context = useSettingsContext();
const router = useSettingsRoute();
const registry = useVaultRegistry();
const flash = useSettingsFlash();

const spaces = computed<ChoiceOption[]>(() =>
  managedSpaces(context).map((space) => ({ value: space.id, label: space.name, description: tv.count(registry.inSpace(space.id).length), icon: space.icon || "book-open" })),
);
const locations = computed<VaultWizardLocation[]>(() =>
  registry.locations.value.map((location) => ({ id: location.id, name: location.name, kind: location.kind, root: location.root, vaultCount: location.vaultCount })),
);

async function submit(draft: VaultWizardDraft): Promise<VaultWizardResult> {
  let locationId = draft.vault.locationId;
  if (draft.newLocation) {
    const location = await vaultApi.saveLocation(null, draft.newLocation);
    registry.saveLocation(location);
    locationId = location.id;
  }
  const vault = { ...draft.vault, locationId };
  if (draft.mode === "attach") {
    const attached = await vaultApi.attach(vault, draft.password ?? "");
    registry.replace(attached);
    return { id: attached.id, name: attached.name, entryCount: attached.entryCount, generatedPassword: null };
  }
  const created = await vaultApi.create(vault, draft.password);
  registry.replace(created.vault);
  return { id: created.vault.id, name: created.vault.name, entryCount: created.vault.entryCount, generatedPassword: created.generatedPassword };
}
// Inside a space its administrator may list; from global settings only the wiki administrator does.
const listFiles: VaultFileLister = (locationId, query, signal) => vaultApi.locationFiles(locationId, props.spaceId ?? null, query, signal);
const testLocation = async (draft: VaultLocationDraft) => (await vaultApi.testLocation(draft, null)).message;

function done(result: VaultWizardResult) {
  const vault = registry.vault(result.id);
  router.navigate(props.backPath);
  flash.notify(t.addVault.added(result.name, vault?.spaceName ?? ""), { highlight: result.id });
}
function open(result: VaultWizardResult) {
  const vault = registry.vault(result.id);
  if (props.spaceId) router.navigate(`vaults/${result.id}/status`);
  else if (vault) window.location.hash = spaceSettingsHash(vault.spaceId, `vaults/${vault.id}/status`);
}
</script>

<template>
  <SettingsPage :title="t.addVault.title" icon="lock" :description="t.addVault.description" :back="{ href: router.href(backPath), label: t.addVault.back }">
    <LoadingSkeleton v-if="!registry.loaded.value" :label="t.addVault.loading" />
    <EmptyState v-else-if="!spaceId && !spaces.length" icon="book-open" :title="t.addVault.noSpaceTitle" :text="t.addVault.noSpaceText" />
    <SettingsSection v-else :label="t.addVault.title">
      <VaultWizard
        :spaces="spaces"
        :locations="locations"
        :space-id="spaceId"
        :can-add-location="context.isWikiAdmin"
        :submit="submit"
        :test-location="testLocation"
        :list-files="listFiles"
        @done="done"
        @open="open"
        @cancel="router.navigate(backPath)"
      />
    </SettingsSection>
  </SettingsPage>
</template>
