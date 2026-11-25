<script setup lang="ts">
import { computed, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { useSettingsContext } from "../context";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-global";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import VaultStatusBadge from "../vaults/VaultStatusBadge.vue";
import { foldText } from "../../../lib/foldText";
import { spaceSettingsHash } from "../../../settingsRoutes";
import type { Vault } from "../../../vaults";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import FilterMenu from "../FilterMenu.vue";
import SearchField from "../SearchField.vue";
import { locale } from "../../../i18n";
import type { ChoiceOption } from "../types";

// Trezory napříč prostory: every vault of the wiki in one list (content first: no card per space),
// each naming its space, with its state and a link into the space's vault detail. A filter line
// appears once there are enough vaults to need it.
const context = useSettingsContext();
const router = useSettingsRoute();
const registry = useVaultRegistry();
const flash = useSettingsFlash();

const groups = computed(() => {
  const bySpace = new Map<string, { spaceId: string; name: string; icon: string; vaults: Vault[] }>();
  for (const vault of registry.vaults.value) {
    const space = context.spaces.find((item) => item.id === vault.spaceId);
    const group = bySpace.get(vault.spaceId) ?? { spaceId: vault.spaceId, name: space?.name ?? vault.spaceName, icon: space?.icon ?? "book-open", vaults: [] };
    group.vaults.push(vault);
    bySpace.set(vault.spaceId, group);
  }
  return [...bySpace.values()].sort((a, b) => a.name.localeCompare(b.name, locale));
});
const query = ref("");
const space = ref("");
const spaceOptions = computed<ChoiceOption[]>(() => [{ value: "", label: t.vaults.allSpaces, icon: "book-open" }, ...groups.value.map((group) => ({ value: group.spaceId, label: group.name, icon: group.icon, badge: String(group.vaults.length) }))]);
const spaceName = (vault: Vault) => groups.value.find((group) => group.spaceId === vault.spaceId)?.name ?? vault.spaceName;
const shown = computed(() => {
  const text = foldText(query.value.trim());
  return groups.value
    .flatMap((group) => group.vaults)
    .filter((vault) => (!space.value || vault.spaceId === space.value) && (!text || foldText(`${vault.name} ${spaceName(vault)} ${vault.fileName}`).includes(text)));
});
const vaultHref = (vault: Vault) => spaceSettingsHash(vault.spaceId, `vaults/${vault.id}/status`);
const openVault = (vault: Vault) => { location.hash = vaultHref(vault); };
</script>

<template>
  <SettingsPage :title="t.vaults.title" :description="t.vaults.description">
    <template #actions>
      <Button as-child><a :href="router.href('security/vaults/new')" data-add-vault><AtlasIcon name="plus" :size="16" />{{ t.vaults.add }}</a></Button>
    </template>
    <ErrorState v-if="registry.error.value" :title="t.vaults.loadFailed" :text="registry.error.value" :busy="registry.loading.value" @retry="registry.load()" />
    <LoadingSkeleton v-else-if="!registry.loaded.value" :label="t.vaults.loading" />
    <template v-else-if="groups.length">
      <div v-if="registry.vaults.value.length > 6" class="all-vaults-filters" role="search" :aria-label="t.vaults.filter">
        <SearchField v-model="query" :label="t.vaults.search" :placeholder="t.vaults.searchPlaceholder" />
        <FilterMenu v-model="space" :options="spaceOptions" :label="t.vaults.space" icon="book-open" data-vault-space />
      </div>
      <SettingsSection :label="t.vaults.section(shown.length)">
        <SettingsList v-if="shown.length" :label="t.vaults.listLabel">
          <SettingsListRow
            v-for="vault in shown"
            :key="vault.id"
            :title="vault.name"
            :description="t.vaults.rowDescription(spaceName(vault), vault.locationName, vault.fileName, t.counts.secrets(vault.entryCount))"
            icon="lock"
            :icon-tone="vault.status === 'unlocked' ? undefined : vault.status === 'needsPassword' ? 'warning' : 'danger'"
            :highlighted="flash.highlight.value === vault.id"
            :data-vault="vault.id"
            :data-space-vaults="vault.spaceId"
            :menu="[{ id: 'open', label: t.vaults.open, icon: 'arrow-right' }]"
            @select="openVault(vault)"
          >
            <template #meta>
              <VaultStatusBadge :status="vault.status" />
              <StatusBadge v-if="vault.lastFailure" tone="danger" icon="triangle-alert" :label="vault.lastFailure" size="sm" />
            </template>
            <template #actions>
              <Button variant="ghost" size="sm" as-child><a :href="vaultHref(vault)" :data-open-vault="vault.id"><AtlasIcon name="arrow-right" :size="15" />{{ t.vaults.open }}</a></Button>
            </template>
          </SettingsListRow>
        </SettingsList>
        <p v-else class="st-note" role="status"><AtlasIcon name="search" :size="15" />{{ t.vaults.noMatch }}</p>
      </SettingsSection>
      <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.vaults.summary(t.counts.vaults(registry.vaults.value.length), t.counts.inSpaces(groups.length)) }}</p>
    </template>
    <EmptyState v-else icon="lock" :title="t.vaults.emptyTitle" :text="t.vaults.emptyText">
      <Button as-child><a :href="router.href('security/vaults/new')"><AtlasIcon name="plus" :size="16" />{{ t.vaults.add }}</a></Button>
    </EmptyState>
  </SettingsPage>
</template>

<style scoped>
.all-vaults-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
</style>
