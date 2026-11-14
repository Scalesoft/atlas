<script setup lang="ts">
import t from "../../../i18n/settings-space";
import { computed } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsTabs from "../SettingsTabs.vue";
import type { SettingsTab } from "../types";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import VaultStatusBadge from "../vaults/VaultStatusBadge.vue";
import VaultAccessTab from "./VaultAccessTab.vue";
import VaultAuditTab from "./VaultAuditTab.vue";
import VaultSecretsTab from "./VaultSecretsTab.vue";
import VaultStatusTab from "./VaultStatusTab.vue";
import LoadingNote from "../LoadingNote.vue";
import ProtectionLevelEditor from "../security/ProtectionLevelEditor.vue";
import AccessRequestsPanel from "../requests/AccessRequestsPanel.vue";
import { useAccessRequestFeed } from "../requests/accessRequestFeed";

// Detail of one vault: Stav a soubor · Přístupy · Žádosti · Hesla · Audit · Ochrana, each a linkable tab.
const props = defineProps<{ spaceId: string; vaultId: string; tab: string }>();
const router = useSettingsRoute();
const registry = useVaultRegistry();
const requests = useAccessRequestFeed();
const vault = computed(() => registry.vault(props.vaultId));
const base = computed(() => `vaults/${props.vaultId}`);
const tabs = computed<SettingsTab[]>(() => [
  { id: "status", label: t.vault.status, icon: "database", href: router.href(`${base.value}/status`) },
  { id: "access", label: t.vault.access, icon: "users", href: router.href(`${base.value}/access`) },
  { id: "requests", label: t.vault.requests, icon: "user-check", count: requests?.inVault(props.vaultId).length || undefined, href: router.href(`${base.value}/requests`) },
  { id: "secrets", label: t.vault.secrets, icon: "key-round", count: vault.value?.entryCount, href: router.href(`${base.value}/secrets`) },
  { id: "audit", label: t.vault.audit, icon: "scroll-text", href: router.href(`${base.value}/audit`) },
  { id: "protection", label: t.vault.protection, icon: "fingerprint", href: router.href(`${base.value}/protection`) },
]);
</script>

<template>
  <LoadingNote v-if="!vault && !registry.loaded.value" :label="t.vault.loading" />
  <SettingsPage v-else-if="vault && vault.spaceId === spaceId" :title="vault.name" icon="lock" :description="`${vault.locationName} · ${vault.fileName}`" :back="{ href: router.href('vaults'), label: t.vault.all }">
    <template #actions><VaultStatusBadge :status="vault.status" size="md" /></template>
    <SettingsTabs :options="tabs" :model-value="tab" :label="t.vault.parts" />
    <!-- The part leaves fast and the next one rises in, like the steps of the other tabbed settings pages (st-step). -->
    <Transition name="st-step" mode="out-in">
      <VaultStatusTab v-if="tab === 'status'" :vault="vault" />
      <VaultAccessTab v-else-if="tab === 'access'" :key="`access-${vault.id}`" target="vault" :id="vault.id" />
      <VaultSecretsTab v-else-if="tab === 'secrets'" :vault="vault" />
      <VaultAuditTab v-else-if="tab === 'audit'" :vault="vault" />
      <AccessRequestsPanel v-else-if="tab === 'requests'" :vault-id="vault.id" />
      <ProtectionLevelEditor v-else-if="tab === 'protection'" :key="`protection-${vault.id}`" level="vault" :id="vault.id" :name="vault.name" />
      <EmptyState v-else icon="compass" :title="t.vault.missing" compact>
        <Button as-child variant="outline"><a :href="router.href(`${base}/status`)"><AtlasIcon name="back" :size="16" />{{ t.vault.status }}</a></Button>
      </EmptyState>
    </Transition>
  </SettingsPage>
  <EmptyState v-else icon="lock" :title="t.vault.notFound" :text="t.vault.notFoundText">
    <Button as-child variant="outline"><a :href="router.href('vaults')"><AtlasIcon name="back" :size="16" />{{ t.vault.all }}</a></Button>
  </EmptyState>
</template>
