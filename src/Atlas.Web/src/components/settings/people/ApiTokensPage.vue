<script setup lang="ts">
import t from "../../../i18n/settings-people";
import EmptyState from "../EmptyState.vue";
import SettingsPage from "../SettingsPage.vue";
import { useSettingsContext } from "../context";
import ApiTokensPanel from "./ApiTokensPanel.vue";

// Nastavení wiki → Kdo → API tokeny (wiki administrator): every personal API token of the wiki, revocable.
const context = useSettingsContext();
const emit = defineEmits<{ reviewed: [] }>();
</script>

<template>
  <SettingsPage :title="t.tokens.pageTitle" :description="t.tokens.pageDescription">
    <EmptyState v-if="!context.isWikiAdmin" icon="lock" :title="t.common.wikiAdminsOnly" :text="t.tokens.adminsOnlyText" />
    <ApiTokensPanel v-else @reviewed="emit('reviewed')" />
  </SettingsPage>
</template>
