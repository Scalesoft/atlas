<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import SearchField from "../SearchField.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import SpaceAccessView from "../access/SpaceAccessView.vue";
import { matchesQuery } from "../access/accessView";
import { useSettingsContext } from "../context";
import t from "../../../i18n/settings-global";
import { useSettingsRoute } from "../useSettingsRoute";
import { locale } from "../../../i18n";

/**
 * Správa wiki → Prostory → Prostory a přístupy (`access/spaces[/{id}[/…]]`): every space of the wiki, and for one space who has
 * access to it (the same view as the space's own Přístupy, plus Odkazy and Činnost). Wiki administrator only.
 */
const props = withDefaults(defineProps<{ part?: string[] }>(), { part: () => [] });
const context = useSettingsContext();
const router = useSettingsRoute();
const spaces = computed(() => context.spaces.filter((space) => !space.itemAccessOnly).sort((a, b) => a.name.localeCompare(b.name, locale)));
const search = computed({
  get: () => router.route.value.query.hledat ?? "",
  set: (value: string) => router.setQuery({ hledat: value || undefined }),
});
const shown = computed(() => spaces.value.filter((space) => matchesQuery(`${space.name} ${space.description ?? ""}`, search.value)));
const spaceId = computed(() => props.part[0]);
</script>

<template>
  <SettingsPage v-if="!context.isWikiAdmin" :title="t.spacesAccess.title">
    <EmptyState icon="lock" :title="t.spacesAccess.adminOnlyTitle" :text="t.spacesAccess.adminOnlyText" />
  </SettingsPage>
  <SpaceAccessView v-else-if="spaceId" :key="spaceId" :space-id="spaceId" :base="`access/spaces/${spaceId}`" :part="part.slice(1)" surface="global" />
  <SettingsPage v-else :title="t.spacesAccess.title" :description="t.spacesAccess.description">
    <div v-if="spaces.length > 6" class="spaces-filters" role="search" :aria-label="t.spacesAccess.filter">
      <div class="spaces-search"><SearchField v-model="search" :label="t.spacesAccess.search" :placeholder="t.spacesAccess.searchPlaceholder" data-space-search /></div>
      <p class="spaces-count" aria-live="polite">{{ t.counts.spaces(shown.length) }}</p>
    </div>
    <SettingsSection :label="t.spacesAccess.section(shown.length)">
      <SettingsList v-if="shown.length" :label="t.spacesAccess.title">
        <SettingsListRow v-for="space in shown" :key="space.id" :title="space.name" :description="[space.description, space.pageCount ? t.counts.pages(space.pageCount) : undefined].filter(Boolean).join(' · ')" :icon="space.icon || 'book-open'" :data-space-row="space.id">
          <template #actions>
            <Button variant="ghost" size="sm" as-child><a :href="router.href(`access/spaces/${space.id}`)" :aria-label="t.access.whoHasAccessFor(space.name)" :data-open-space="space.id"><AtlasIcon name="users" :size="15" />{{ t.spacesAccess.whoHasAccess }}</a></Button>
          </template>
        </SettingsListRow>
      </SettingsList>
      <EmptyState v-else-if="spaces.length" compact icon="search" :title="t.spacesAccess.noMatchTitle" :text="t.spacesAccess.noMatchText" />
      <EmptyState v-else compact icon="book-open" :title="t.spacesAccess.emptyTitle" :text="t.spacesAccess.emptyText" />
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.spaces-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.spaces-search { flex: 1 1 240px; min-width: min(200px, 100%); max-width: 360px; }
.spaces-count { margin: 0 0 0 auto; color: var(--muted); font-size: 12.5px; font-variant-numeric: tabular-nums; }
</style>
