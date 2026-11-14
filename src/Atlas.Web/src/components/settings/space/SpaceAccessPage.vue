<script setup lang="ts">
import { computed } from "vue";
import t from "../../../i18n/settings-space";
import EmptyState from "../EmptyState.vue";
import SettingsPage from "../SettingsPage.vue";
import SpaceAccessView from "../access/SpaceAccessView.vue";
import { useSettingsContext } from "../context";

// Nastavení prostoru › Přístupy (`access[/new|/exceptions[/kind/id]|/people/id|/groups/id]`): the same Kdo má přístup
// panel as the security centre, with its Výjimky; a person opens as a card limited to this space.
const props = withDefaults(defineProps<{ spaceId: string; part?: string[] }>(), { part: () => [] });
const context = useSettingsContext();
const space = computed(() => context.spaces.find((item) => item.id === props.spaceId));
const allowed = computed(() => context.isWikiAdmin || space.value?.role === "Admin");
</script>

<template>
  <SpaceAccessView v-if="allowed" :space-id="spaceId" base="access" :part="part" surface="space" />
  <SettingsPage v-else :title="t.access.title" icon="users" :description="t.access.description">
    <EmptyState icon="lock" :title="t.access.cannotTitle" :text="t.access.cannotText" />
  </SettingsPage>
</template>
