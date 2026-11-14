<script setup lang="ts">
import t from "../../../i18n/settings-requests";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import AccessRequestsPanel from "./AccessRequestsPanel.vue";
import { useAccessRequestFeed } from "./accessRequestFeed";
import { computed } from "vue";

// Žádosti o přístup as a settings page: every pending request of the wiki (global settings) or of one space (its settings).
const props = withDefaults(defineProps<{ spaceId?: string }>(), { spaceId: undefined });
const feed = useAccessRequestFeed();
const count = computed(() => (props.spaceId ? feed?.inSpace(props.spaceId).length : feed?.count.value) ?? 0);
</script>

<template>
  <SettingsPage :title="t.page.title" :icon="spaceId ? 'user-check' : undefined" :description="spaceId ? t.page.spaceDescription : t.page.wikiDescription">
    <SettingsSection :title="t.page.pending" icon="hourglass" :count="count" :count-tone="count ? 'warning' : undefined">
      <AccessRequestsPanel :space-id="spaceId" />
    </SettingsSection>
  </SettingsPage>
</template>
