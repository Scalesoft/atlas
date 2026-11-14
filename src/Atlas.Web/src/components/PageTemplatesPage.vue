<script setup lang="ts">
import t from "../i18n/components-b";
import { onUnmounted, ref, watch } from "vue";
import { errorMessage, type Space } from "../api";
import { pageTemplateApi, type PageTemplateSummary } from "../pageTemplateApi";
import AtlasIcon from "./AtlasIcon.vue";
import EmptyState from "./settings/EmptyState.vue";
import ErrorState from "./settings/ErrorState.vue";
import LoadingRegion from "./LoadingRegion.vue";
import LoadingNote from "./settings/LoadingNote.vue";
import PageStatusBadge from "./PageStatusBadge.vue";
import SettingsList from "./settings/SettingsList.vue";
import SettingsListRow from "./settings/SettingsListRow.vue";
import SettingsPage from "./settings/SettingsPage.vue";
import SettingsSection from "./settings/SettingsSection.vue";
import UsagePanel from "./objects/UsagePanel.vue";
import { Button } from "./ui/button";

// Šablony stránek, a section of Nastavení prostoru: template pages are ordinary pages, so a row
// only opens the page; creating one goes through the normal new-page flow. Each row folds out
// „Použito na“: the pages created from the template (relation template).
const props = defineProps<{ space: Space }>();
const emit = defineEmits<{ select: [pageId: string]; create: [] }>();
const items = ref<PageTemplateSummary[]>([]);
const loading = ref(true), loaded = ref(false);
const error = ref("");
let request = 0;
let mounted = true;

async function load() {
  const current = ++request;
  loading.value = true;
  error.value = "";
  try {
    const result = await pageTemplateApi.list(props.space.id);
    if (mounted && current === request) { items.value = result; loaded.value = true; }
  } catch (cause) {
    if (mounted && current === request) error.value = errorMessage(cause);
  } finally {
    if (mounted && current === request) loading.value = false;
  }
}
watch(() => props.space.id, () => { items.value = []; loaded.value = false; void load(); }, { immediate: true });
onUnmounted(() => { mounted = false; request++; });
</script>

<template>
  <div class="page-templates-page" :aria-busy="loading">
    <SettingsPage :title="t.templates.title" icon="page" :description="t.templates.description">
      <template #actions>
        <Button type="button" data-action="new-template" @click="emit('create')"><AtlasIcon name="plus" :size="16" />{{ t.templates.new }}</Button>
      </template>
      <ErrorState v-if="error" :title="t.templates.loadFailed" :text="error" @retry="load" />
      <SettingsSection v-else :title="t.templates.templates" icon="page" :count="loaded ? items.length : undefined">
        <!-- A refresh says so beside the count, so no line is reserved above the list (it pushed the empty state 28 px down). -->
        <template #title-extra><LoadingNote inline :active="loading && loaded" :label="t.templates.loading" /></template>
        <LoadingRegion :loading="loading" :ready="loaded" :rows="3" row-height="60px" :label="t.templates.loadingTemplates" class="templates-rows">
          <EmptyState v-if="!items.length" icon="page" :title="t.templates.emptyTitle" :text="t.templates.emptyText">
            <Button type="button" variant="outline" @click="emit('create')"><AtlasIcon name="plus" :size="16" />{{ t.templates.new }}</Button>
          </EmptyState>
          <SettingsList v-else :label="t.templates.spaceTemplates">
            <SettingsListRow v-for="item in items" :key="item.id" :title="item.title" icon="page">
              <template #meta><PageStatusBadge :is-published="item.isPublished" :version="item.version" :published-version="item.isPublished ? item.version : null" /></template>
              <template #actions><Button type="button" variant="outline" size="sm" :data-select-template="item.id" @click="emit('select', item.id)">{{ t.templates.open }}<AtlasIcon name="arrow-right" :size="15" /></Button></template>
              <UsagePanel collapsible :object-id="item.id" @open="id => emit('select', id)" />
            </SettingsListRow>
          </SettingsList>
        </LoadingRegion>
      </SettingsSection>
    </SettingsPage>
  </div>
</template>

<style scoped>
.page-templates-page { width: 100%; min-width: 0; }
/* The list reaches the card edges like a list placed in the card directly (settings.css), and the empty state is frameless
   there, as one directly in a card is (it drew a dashed box inside the card). */
.templates-rows :deep(.loading-region-content > .settings-list) { margin: calc(var(--st-body-pad-y, 16px) * -1) calc(var(--st-pad-x, 18px) * -1); --st-row-pad-x: var(--st-pad-x, 18px); }
.templates-rows :deep(.loading-region-content > .st-empty) { border-style: none; padding-block: 20px; }
</style>
