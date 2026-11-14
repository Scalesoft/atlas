<script setup lang="ts">
import "../settings/settings.css";
import "../settings/settings-tailwind.css";
import { computed, onUnmounted, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import AccessExplanation from "../settings/AccessExplanation.vue";
import ErrorState from "../settings/ErrorState.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SettingsTabs from "../settings/SettingsTabs.vue";
import type { SettingsTab } from "../settings/types";
import { errorMessage } from "../../api";
import { sharingApi, type PageAccess } from "../../sharing";
import PageAccessHistory from "./PageAccessHistory.vue";
import PageAccessPanel from "./PageAccessPanel.vue";
import PageShareLinks from "./PageShareLinks.vue";
import { pageAccessRoute, type PageAccessTab } from "./pageAccessRoute";
import { accessPaths, lastActivity, pageRoleOptions } from "./pageAccessText";
import t from "../../i18n/components-sharing";

/**
 * „Kdo má přístup“ as a view of the page (`#/pages/<id>/access`), not a dialog: its parts are real links
 * (SettingsTabs): Přístup, Anonymní odkazy and Historie otevření, the latter two for page managers only. A reader sees
 * only their own access. Every flow (a new grant, a new link) unfolds in place; only a destructive step asks in a
 * ConfirmDialog. The access state is loaded here; the parts change it and ask for a quiet reload.
 */
const props = withDefaults(defineProps<{ pageId: string; tab?: PageAccessTab; pageTitle?: string }>(), { tab: "access", pageTitle: "" });
const access = ref<PageAccess>();
const loading = ref(false), reloading = ref(false), error = ref("");
let request = 0, disposed = false;

const title = computed(() => access.value?.pageTitle ?? props.pageTitle);
const shownTab = computed<PageAccessTab>(() => (access.value?.canManage ? props.tab : "access"));
const tabs = computed<SettingsTab[]>(() => [
  { id: "access", label: t.view.tabAccess, icon: "users", href: pageAccessRoute(props.pageId) },
  { id: "links", label: t.view.tabLinks, icon: "link", href: pageAccessRoute(props.pageId, "links"), count: access.value?.shareLinks?.active || undefined },
  { id: "history", label: t.view.tabHistory, icon: "history", href: pageAccessRoute(props.pageId, "history") },
]);
const description = computed(() => {
  const where = access.value ? t.view.where(title.value, access.value.spaceName) : title.value ? t.view.whereTitle(title.value) : "";
  return access.value?.canManage === false ? t.view.yours(where) : where;
});
const me = computed(() => access.value?.myAccess);
const capability = computed(() => !me.value ? "" : me.value.canEdit ? t.view.canEdit : me.value.canReadDraft ? t.view.canReadDraft : t.view.canRead);

async function load() {
  const current = ++request;
  loading.value = true; error.value = "";
  try {
    const result = await sharingApi.pageAccess(props.pageId);
    if (disposed || current !== request) return;
    access.value = result;
  } catch (failure) {
    if (!disposed && current === request) error.value = errorMessage(failure);
  } finally {
    if (!disposed && current === request) loading.value = false;
  }
}
/** After a change: the page state only (mode, counts, links), keeping what is shown in place while it refreshes. */
async function reloadAccess() {
  const current = ++request;
  reloading.value = true; error.value = "";
  try {
    const result = await sharingApi.pageAccess(props.pageId);
    if (disposed || current !== request) return;
    access.value = result;
  } catch (failure) {
    if (!disposed && current === request) error.value = errorMessage(failure);
  } finally {
    if (!disposed && current === request) reloading.value = false;
  }
}
watch(() => props.pageId, () => {
  access.value = undefined; reloading.value = false;
  void load();
}, { immediate: true });
onUnmounted(() => { disposed = true; request++; });
</script>

<template>
  <div class="page-sharing-view settings-ui" data-page-sharing>
    <SettingsPage :title="t.view.title" icon="users" :description="description" :back="{ href: `#/pages/${pageId}`, label: t.view.back }">
      <ErrorState v-if="error" :title="t.view.loadFailed" :text="error" :busy="loading || reloading" @retry="load()" />
      <LoadingSkeleton v-if="!access && !error" instant :rows="4" :label="t.view.loading" />
      <template v-if="access">
        <SettingsTabs v-if="access.canManage" :options="tabs" :model-value="shownTab" :label="t.view.partsLabel" />
        <Transition name="st-step" mode="out-in">
          <div :key="shownTab" class="page-sharing-part" :data-share-part="shownTab">
            <SettingsSection v-if="!access.canManage" :title="t.view.yourAccess" icon="user-check" data-own-access>
              <AccessExplanation
                v-if="me"
                :subject="{ name: me.displayName, kind: 'user' }"
                :target="{ name: t.panel.pageTarget(title), icon: 'file-text' }"
                :paths="accessPaths(me.sources, { spaceName: access.spaceName, pageTitle: title })"
                :options="pageRoleOptions()"
              >
                <p class="st-note"><AtlasIcon name="circle-check" :size="15" />{{ capability }}</p>
                <p class="st-note"><AtlasIcon name="eye" :size="15" />{{ t.view.lastly(lastActivity(me)) }}</p>
                <p class="st-note"><AtlasIcon name="lock" :size="15" />{{ t.view.onlyManagers }}</p>
              </AccessExplanation>
            </SettingsSection>
            <PageAccessPanel
              v-else-if="shownTab === 'access'"
              :page-id="pageId"
              :access="access"
              :links-href="pageAccessRoute(pageId, 'links')"
              @changed="reloadAccess"
            />
            <PageShareLinks v-else-if="shownTab === 'links'" :key="pageId" :page-id="pageId" :page-title="title" :space-id="access.spaceId" :is-published="access.isPublished" @changed="reloadAccess" />
            <PageAccessHistory v-else :key="pageId" :page-id="pageId" />
          </div>
        </Transition>
      </template>
    </SettingsPage>
  </div>
</template>

<style scoped>
.page-sharing-view { width: 100%; max-width: 1080px; min-width: 0; margin: 0 auto; padding: 20px 24px 40px; }
.page-sharing-part { display: grid; gap: 18px; min-width: 0; }
@media (max-width: 680px) { .page-sharing-view { padding: 16px 16px 72px; } }
</style>
