<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import { foldText } from "../../../lib/foldText";
import { vaultApi, vaultFieldLabels, type Vault, type VaultSecret } from "../../../vaults";
import AccessLevelBadge from "../AccessLevelBadge.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { levelOptions } from "../vaults/accessLevels";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-space";
import VaultAccessTab from "./VaultAccessTab.vue";
import ProtectionLevelEditor from "../security/ProtectionLevelEditor.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import FilterMenu from "../FilterMenu.vue";
import SearchField from "../SearchField.vue";
import type { ChoiceOption } from "../types";

// Hesla: the registered KeePass entries (title, path and field names, never a value) with the
// caller's level, and per secret its own access and protection on the same components as the vault.
const props = defineProps<{ vault: Vault }>();
const secrets = ref<VaultSecret[]>([]);
const loading = ref(true);
const error = ref("");
const open = ref<{ id: string; part: "access" | "protection" } | null>(null);
const toggle = (id: string, part: "access" | "protection") => (open.value = open.value?.id === id && open.value.part === part ? null : { id, part });
const isOpen = (id: string, part: "access" | "protection") => open.value?.id === id && open.value.part === part;
let disposed = false;

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const list = await vaultApi.secrets(props.vault.id);
    if (!disposed) secrets.value = list;
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
const current = computed(() => secrets.value.filter((secret) => !secret.removedAt));
// A trezor holds tens of entries: one filter line (text, folder) keeps the list itself on screen.
const query = ref("");
const folder = ref("");
const folderOf = (secret: VaultSecret) => secret.groupPath || t.secrets.root;
const folderOptions = computed<ChoiceOption[]>(() => {
  const counts = new Map<string, number>();
  for (const secret of current.value) counts.set(folderOf(secret), (counts.get(folderOf(secret)) ?? 0) + 1);
  return [
    { value: "", label: t.secrets.allFolders, icon: "folder" },
    ...[...counts].sort(([a], [b]) => a.localeCompare(b, locale)).map(([path, count]) => ({ value: path, label: path, icon: "folder", badge: String(count) })),
  ];
});
const shown = computed(() => {
  const text = foldText(query.value.trim());
  return current.value.filter((secret) => (!folder.value || folderOf(secret) === folder.value) && (!text || foldText(`${secret.title} ${folderOf(secret)}`).includes(text)));
});
// The caller's level is marked only where a secret differs from the whole vault.
const ownLevel = (secret: VaultSecret) => !!secret.level && secret.level !== props.vault.level;
const removed = computed(() => secrets.value.filter((secret) => secret.removedAt));
const fields = (secret: VaultSecret) => secret.availableFields.map((field) => vaultFieldLabels[field] ?? field).join(", ") || t.secrets.noFields;
onMounted(load);
onBeforeUnmount(() => (disposed = true));
</script>

<template>
  <div class="vault-secrets">
    <ErrorState v-if="error" :title="t.secrets.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingSkeleton v-else-if="loading && !secrets.length" :rows="4" row-height="60px" :label="t.secrets.loading" />
    <SettingsSection v-else :title="t.secrets.title" icon="key-round" :count="current.length" :description="t.secrets.description">
      <div v-if="current.length > 8" class="vault-secrets-filters" role="search" :aria-label="t.secrets.filter">
        <SearchField v-model="query" :label="t.secrets.search" :placeholder="t.secrets.searchPlaceholder" />
        <FilterMenu v-if="folderOptions.length > 2" v-model="folder" :options="folderOptions" :label="t.secrets.folder" icon="folder" data-secret-folder />
        <span v-if="query || folder" class="vault-secrets-count" aria-live="polite">{{ t.secrets.shown(shown.length, current.length) }}</span>
      </div>
      <SettingsList v-if="shown.length" :label="t.secrets.list">
        <SettingsListRow v-for="secret in shown" :key="secret.secretId" :title="secret.title || t.secrets.untitled" :description="`${folderOf(secret)} · ${fields(secret)}`" icon="key-round" icon-tone="neutral" :data-secret="secret.secretId">
          <template v-if="ownLevel(secret)" #meta>
            <AccessLevelBadge :options="levelOptions()" :value="secret.level!" tone="neutral" />
          </template>
          <template #actions>
            <Button variant="ghost" size="sm" type="button" :aria-expanded="isOpen(secret.secretId, 'access')" :data-secret-access="secret.secretId" @click="toggle(secret.secretId, 'access')">
              <AtlasIcon name="users" :size="15" />{{ t.secrets.access }}
            </Button>
            <Button variant="ghost" size="sm" type="button" :aria-expanded="isOpen(secret.secretId, 'protection')" :data-secret-protection="secret.secretId" @click="toggle(secret.secretId, 'protection')">
              <AtlasIcon name="fingerprint" :size="15" />{{ t.secrets.protection }}
            </Button>
          </template>
          <InlineReveal :open="isOpen(secret.secretId, 'access')">
            <div class="vault-secret-access">
              <VaultAccessTab target="secret" :id="secret.secretId" />
            </div>
          </InlineReveal>
          <InlineReveal :open="isOpen(secret.secretId, 'protection')">
            <div class="vault-secret-access">
              <ProtectionLevelEditor level="secret" :id="secret.secretId" :name="secret.title || t.secrets.untitled" />
            </div>
          </InlineReveal>
        </SettingsListRow>
      </SettingsList>
      <p v-else-if="current.length" class="st-note" role="status"><AtlasIcon name="search" :size="15" />{{ t.secrets.noMatch }}</p>
      <EmptyState v-else icon="key-round" :title="t.secrets.emptyTitle" :text="t.secrets.emptyText" compact />
    </SettingsSection>
    <SettingsSection v-if="removed.length" :title="t.secrets.removed" icon="trash" :count="removed.length" advanced :summary="t.secrets.removedSummary">
      <SettingsList :label="t.secrets.removedList">
        <SettingsListRow v-for="secret in removed" :key="secret.secretId" :title="secret.title || t.secrets.untitled" :description="secret.groupPath || t.secrets.root" icon="key-round" icon-tone="neutral">
          <template #meta><StatusBadge tone="warning" icon="triangle-alert" :label="t.secrets.notInFile" size="sm" /></template>
        </SettingsListRow>
      </SettingsList>
    </SettingsSection>
  </div>
</template>

<style scoped>
.vault-secrets { display: grid; gap: 18px; min-width: 0; }
.vault-secret-access { padding: 2px 0 16px; }
.vault-secrets-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.vault-secrets-count { margin-left: auto; color: var(--muted); font-size: 12.5px; font-variant-numeric: tabular-nums; }
</style>
