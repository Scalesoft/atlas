<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import AccessLevelBadge from "../AccessLevelBadge.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import SettingsPage from "../SettingsPage.vue";
import TileCard from "../TileCard.vue";
import { useSettingsFlash } from "../flash";
import t from "../../../i18n/settings-space";
import tv from "../../../i18n/settings-vaults";
import { useSettingsRoute } from "../useSettingsRoute";
import { useVaultRegistry } from "../vaultRegistry";
import { levelOptions } from "../vaults/accessLevels";
import VaultStatusBadge from "../vaults/VaultStatusBadge.vue";
import { formatDateTime } from "../../../formatDate";
import LoadingNote from "../LoadingNote.vue";

// Trezory of one space: a TileCard per vault with its state, the file and the caller's level.
const props = defineProps<{ spaceId: string }>();
const router = useSettingsRoute();
const registry = useVaultRegistry();
const flash = useSettingsFlash();
const vaults = computed(() => registry.inSpace(props.spaceId));
const tone = (status: string) => (status === "unlocked" ? undefined : status === "needsPassword" ? "warning" : "danger");
</script>

<template>
  <SettingsPage :title="t.vaults.title" icon="lock" :description="t.vaults.description">
    <template #actions>
      <Button as-child><a :href="router.href('vaults/new')" data-add-vault><AtlasIcon name="plus" :size="16" />{{ t.vaults.add }}</a></Button>
    </template>
    <ErrorState v-if="registry.error.value" :title="t.vaults.loadFailed" :text="registry.error.value" :busy="registry.loading.value" @retry="registry.load()" />
    <LoadingNote v-else-if="!registry.loaded.value" :label="t.vaults.loading" />
    <TransitionGroup v-else-if="vaults.length" tag="div" name="st-list" class="vault-grid">
      <TileCard
        v-for="(vault, index) in vaults"
        :key="vault.id"
        :title="vault.name"
        :description="`${vault.locationName} · ${vault.fileName}`"
        icon="lock"
        :icon-tone="tone(vault.status)"
        :href="router.href(`vaults/${vault.id}/status`)"
        :index="index"
        :highlighted="flash.highlight.value === vault.id"
        :data-vault="vault.id"
      >
        <template #badge><VaultStatusBadge :status="vault.status" /></template>
        <span class="vault-facts">
          <span><AtlasIcon name="key-round" :size="13" />{{ tv.secretCount(vault.entryCount) }}</span>
          <span v-if="vault.lastRefreshAt"><AtlasIcon name="refresh" :size="13" />{{ formatDateTime(vault.lastRefreshAt) }}</span>
        </span>
        <span v-if="vault.level"><AccessLevelBadge :options="levelOptions()" :value="vault.level" tone="neutral" /></span>
        <span v-if="vault.lastFailure" class="vault-failure"><AtlasIcon name="triangle-alert" :size="13" />{{ vault.lastFailure }}</span>
      </TileCard>
      <TileCard key="__add" :title="t.vaults.add" :description="t.vaults.addHint" icon="plus" icon-tone="neutral" :href="router.href('vaults/new')" dashed :index="vaults.length" />
    </TransitionGroup>
    <EmptyState v-else icon="lock" :title="t.vaults.emptyTitle" :text="t.vaults.emptyText">
      <Button as-child><a :href="router.href('vaults/new')"><AtlasIcon name="plus" :size="16" />{{ t.vaults.add }}</a></Button>
    </EmptyState>
  </SettingsPage>
</template>

<style scoped>
.vault-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 260px), 1fr)); gap: 12px; }
.vault-facts { display: flex; flex-wrap: wrap; gap: 4px 12px; }
.vault-facts span, .vault-failure { display: inline-flex; align-items: center; gap: 5px; }
.vault-failure { color: var(--error); }
</style>
