<script setup lang="ts">
import t from "../../i18n/components-library";
import componentsLibraryHome from "../../i18n/components-library-home";
import { computed } from "vue";
import type { PageSummary, Space } from "../../api";
import { formatAgo, formatDateTime } from "../../formatDate";
import { roleLabel } from "../../roles";
import { spaceSettingsHash } from "../../settingsRoutes";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingRegion from "../LoadingRegion.vue";
import PageStatusBadge from "../PageStatusBadge.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SummaryList from "../settings/SummaryList.vue";
import TileCard from "../settings/TileCard.vue";
import type { SummaryItem } from "../settings/types";
import { Button } from "../ui/button";
import "../settings/settings.css";

/**
 * O prostoru (refresh 2.5, settings places): what a space is about, for everyone who can open it. The description,
 * the facts of the space header (pages, drafts, last change) with the account's own access, and the space's template
 * pages; only with `canManage` also "Správa prostoru", links into the sections of Nastavení prostoru. It shows nothing
 * about other people's access. Presentational: the shell owns the data (the overview's space and page list, the
 * templates from `useSpaceTemplates`; `templates` is `undefined` until the first answer) and the routing (the links
 * are plain hashes, a template opens through `openPage`).
 *
 * Choose it to tell readers about a space. SpaceHeader is the head of the overview (identity and the two actions
 * of the space) and PageTemplatesPage is the administrator's section for creating templates; neither is read-only
 * for a reader, and neither says how the reader stands in the space.
 */
const props = defineProps<{
  space: Space;
  pages: readonly PageSummary[];
  canManage?: boolean;
  templates?: readonly PageSummary[];
  templatesLoading?: boolean;
  templatesError?: string;
}>();
const emit = defineEmits<{ openPage: [pageId: string]; retryTemplates: [] }>();

const drafts = computed(() => props.pages.filter((page) => !page.isPublished).length);
const facts = computed<SummaryItem[]>(() => [
  { key: "pages", icon: "page", label: t.about.pages, value: componentsLibraryHome.pages(props.pages.length) },
  ...(drafts.value ? [{ key: "drafts", icon: "drafts", label: t.about.drafts, value: componentsLibraryHome.drafts(drafts.value) }] : []),
  ...(props.space.lastChangedAt ? [{ key: "changed", icon: "recent", label: componentsLibraryHome.header.lastChange }] : []),
  { key: "role", icon: "shield-check", label: componentsLibraryHome.access, value: roleLabel(props.space.role) },
]);
const adminLinks = [
  { section: "general", title: t.about.links.general, icon: "settings" },
  { section: "access", title: t.about.links.access, icon: "users" },
  { section: "sharing", title: t.about.links.sharing, icon: "link" },
  { section: "templates", title: t.about.links.templates, icon: "page" },
  { section: "vaults", title: t.about.links.vaults, icon: "lock" },
];
</script>

<template>
  <div class="space-about settings-ui" data-space-about>
    <p v-if="space.description" class="space-about-description">{{ space.description }}</p>
    <SummaryList :items="facts" :label="t.about.facts">
      <template #value-changed>
        <time v-if="space.lastChangedAt" :datetime="space.lastChangedAt" :title="formatDateTime(space.lastChangedAt)">{{ formatAgo(space.lastChangedAt) }}</time>
      </template>
    </SummaryList>

    <ErrorState v-if="templatesError" :title="t.about.templatesFailed" :text="templatesError" @retry="emit('retryTemplates')" />
    <SettingsSection v-else :title="t.about.templates" icon="page" :count="templates?.length">
      <LoadingRegion :loading="!!templatesLoading" :ready="templates !== undefined" :rows="2" row-height="60px" note :label="t.about.loadingTemplates">
        <EmptyState v-if="!templates?.length" compact icon="page" :title="t.about.noTemplates" :text="t.about.noTemplatesText" />
        <SettingsList v-else :label="t.about.spaceTemplates">
          <SettingsListRow v-for="item in templates" :key="item.id" :title="item.title" icon="page">
            <template #title><span class="space-about-template" :title="item.title">{{ item.title }}</span></template>
            <template #meta><PageStatusBadge :is-published="item.isPublished" :version="item.version" :published-version="item.isPublished ? item.version : null" /></template>
            <template #actions>
              <Button type="button" variant="outline" size="sm" :data-open-template="item.id" :aria-label="t.about.openTemplate(item.title)" @click="emit('openPage', item.id)">{{ t.about.open }}<AtlasIcon name="arrow-right" :size="15" /></Button>
            </template>
          </SettingsListRow>
        </SettingsList>
      </LoadingRegion>
    </SettingsSection>

    <SettingsSection v-if="canManage" flush :title="t.about.manage" icon="settings" data-space-about-admin>
      <div class="space-about-links">
        <TileCard v-for="(link, index) in adminLinks" :key="link.section" compact :title="link.title" :icon="link.icon" :href="spaceSettingsHash(space.id, link.section)" :index="index" data-space-about-link />
      </div>
    </SettingsSection>
  </div>
</template>

<style scoped>
/* The root is the container: the links answer to the room the overview gives it (sidebar and dock open or closed), not
   to the screen. */
.space-about { container: space-about / inline-size; display: grid; gap: 16px; width: 100%; min-width: 0; }
.space-about-description { max-width: 72ch; margin: 0; font-size: 14px; line-height: 1.6; overflow-wrap: anywhere; }
/* A long template name ends in an ellipsis (its full name is the title); the row's text column may shrink below it. */
.space-about :deep(.settings-list-row-text) { grid-template-columns: minmax(0, 1fr); }
.space-about-template { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.space-about-links { display: grid; grid-template-columns: minmax(0, 1fr); gap: 12px; }
@container space-about (min-width: 520px) { .space-about-links { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@container space-about (min-width: 860px) { .space-about-links { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
</style>
