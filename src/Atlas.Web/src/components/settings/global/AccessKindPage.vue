<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SearchField from "../SearchField.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import SettingsTabs from "../SettingsTabs.vue";
import ResourceAccess from "../access/ResourceAccess.vue";
import { accessKind } from "../access/accessKinds";
import type { AccessResource, AccessSubject } from "../access/accessModel";
import { useSettingsContext } from "../context";
import type { SettingsTab } from "../types";
import { useSettingsRoute } from "../useSettingsRoute";
import t from "../../../i18n/settings-global";
import { locale } from "../../../i18n";

/**
 * Nastavení wiki → Co → a plugin's kind (`access/{kind}[/{id}[/{tab}]]`), generic for every kind the registry knows:
 * its resources (GET /api/access/resources?kind=&q=, searched on the server) and for one of them Kdo má přístup, plus
 * the tabs the plugin registered for its resources (registerAccessKind({ id, tabs })). Nothing here names a plugin.
 */
const props = withDefaults(defineProps<{ kindId: string; part?: string[] }>(), { part: () => [] });
const context = useSettingsContext();
const router = useSettingsRoute();
const kind = computed(() => accessKind(props.kindId));
const base = computed(() => `access/${props.kindId}`);
const resourceId = computed(() => props.part[0]);
const tab = computed(() => props.part[1] ?? "access");

const resources = ref<AccessResource[]>([]);
const loading = ref(false);
const loaded = ref(false);
const error = ref("");
const query = ref("");
let request = 0;
let timer: ReturnType<typeof setTimeout> | undefined;
async function load() {
  const current = ++request;
  loading.value = true;
  try {
    const found = await accessApi.resources(props.kindId, query.value);
    if (current !== request) return;
    resources.value = found;
    loaded.value = true;
    error.value = "";
  } catch (failure) {
    if (current === request) error.value = errorMessage(failure);
  } finally {
    if (current === request) loading.value = false;
  }
}
watch(() => props.kindId, () => context.isWikiAdmin && void load(), { immediate: true });
watch(query, () => {
  clearTimeout(timer);
  timer = setTimeout(() => void load(), 250);
});
onBeforeUnmount(() => clearTimeout(timer));

const resource = computed(() => resources.value.find((item) => item.id === resourceId.value));
const detail = (item: AccessResource) => [item.spaceName, item.parentTitle, item.note].filter(Boolean).join(" · ") || undefined;
const tabs = computed<SettingsTab[]>(() => [
  { id: "access", label: t.access.whoHasAccess, icon: "users", href: router.href(`${base.value}/${resourceId.value}`) },
  ...(kind.value.tabs ?? []).map((extra) => ({ id: extra.id, label: extra.label, icon: extra.icon, href: router.href(`${base.value}/${resourceId.value}/${extra.id}`) })),
]);
const extraTab = computed(() => kind.value.tabs?.find((extra) => extra.id === tab.value));
const subjectHref = (subject: AccessSubject) => router.href(subject.kind === "group" ? `people/groups/${subject.id}` : `people/accounts/${subject.id}`);
const groupHref = (id: string) => router.href(`people/groups/${id}`);
</script>

<template>
  <SettingsPage v-if="!context.isWikiAdmin" :title="kind.many">
    <EmptyState icon="lock" :title="t.access.adminOnlyTitle" :text="t.access.adminOnlyText" />
  </SettingsPage>

  <SettingsPage
    v-else-if="resourceId"
    :key="`${kindId}-${resourceId}`"
    :title="resource?.title ?? kind.one"
    :icon="kind.icon"
    :description="[kind.one, resource ? detail(resource) : undefined].filter(Boolean).join(' · ')"
    :back="{ href: router.href(base), label: kind.many }"
  >
    <SettingsTabs v-if="tabs.length > 1" :options="tabs" :model-value="tab" :label="t.access.tabsLabel(kind.one)" />
    <component :is="extraTab.component" v-if="extraTab" :kind="kindId" :id="resourceId" />
    <ResourceAccess v-else :kind="kindId" :id="resourceId" :subject-href="subjectHref" :group-href="groupHref" />
  </SettingsPage>

  <SettingsPage v-else :key="kindId" :title="kind.many" :description="kind.plugin ? t.access.module(kind.plugin, Object.values(kind.levels).join(' · ')) : Object.values(kind.levels).join(' · ')">
    <div class="kind-filters" role="search" :aria-label="t.access.filterLabel(kind.many)">
      <div class="kind-search"><SearchField v-model="query" :label="t.access.searchLabel(kind.many)" :placeholder="t.access.searchPlaceholder(kind.one.toLocaleLowerCase(locale))" data-kind-search /></div>
      <LoadingNote inline :active="loading && loaded" :label="t.access.searching" />
    </div>
    <ErrorState v-if="error" :title="t.access.loadFailed(kind.many)" :text="error" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-else-if="!loaded" :label="t.access.loading(kind.many)" />
    <SettingsSection v-else :label="t.access.section(kind.many, resources.length)">
      <SettingsList v-if="resources.length" :label="kind.many">
        <SettingsListRow v-for="item in resources" :key="item.id" :title="item.title" :description="detail(item)" :icon="kind.icon" icon-tone="neutral" :data-kind-resource="item.id" :menu="[{ id: 'access', label: t.access.whoHasAccess, icon: 'users' }]" @select="router.navigate(`${base}/${item.id}`)">
          <template #actions>
            <Button variant="ghost" size="sm" as-child><a :href="router.href(`${base}/${item.id}`)" :aria-label="t.access.whoHasAccessFor(item.title)"><AtlasIcon name="users" :size="15" />{{ t.access.whoHasAccess }}</a></Button>
          </template>
        </SettingsListRow>
      </SettingsList>
      <EmptyState v-else compact icon="search" :title="query.trim() ? t.access.noMatchTitle : t.access.nothingTitle" :text="query.trim() ? t.access.noMatchText : t.access.nothingText" />
      <p v-if="resources.length >= 20" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.access.firstTwenty }}</p>
    </SettingsSection>
  </SettingsPage>
</template>

<style scoped>
.kind-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.kind-search { flex: 1 1 240px; min-width: min(200px, 100%); max-width: 360px; }
</style>
