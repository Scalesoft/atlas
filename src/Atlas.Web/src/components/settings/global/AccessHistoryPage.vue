<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import { formatDateTime } from "../../../formatDate";
import ActivityLog from "../ActivityLog.vue";
import ActivitySummarySection from "./ActivitySummarySection.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SettingsPage from "../SettingsPage.vue";
import type { ActivityItem } from "../activity";
import { auditActivity, auditCategoryLabels, loadAccessAudit } from "../auditActivity";
import { useSettingsContext } from "../context";
import t from "../../../i18n/settings-global";
import LoadMoreButton from "../../LoadMoreButton.vue";

// Audit (wiki administrator): password releases and refusals, access requests, protection, network and
// proxy changes, fresh sign-ins and every access change of accounts, groups, spaces, pages and vaults as
// "kdo co kdy". Filters Kdo / Co / Kdy / Výsledek live in the URL through ActivityLog; the details of an
// event open in place and never show a value. The overview above counts the whole period on the server. Older pages follow the opaque cursor without duplicates.
const context = useSettingsContext();
const items = ref<ActivityItem[]>([]);
const cursor = ref<string | null>(null);
const hasMore = ref(false);
const since = ref("");
const loading = ref(false);
const loaded = ref(false);
const error = ref("");
let controller: AbortController | undefined;
// A retry repeats what failed: the first page or the next older one.
let lastMore = false;

async function load(more = false) {
  if (!context.isWikiAdmin || (more && (loading.value || !hasMore.value))) return;
  lastMore = more;
  controller?.abort();
  const current = (controller = new AbortController());
  loading.value = true;
  error.value = "";
  try {
    const page = await loadAccessAudit({ limit: 50, beforeId: more ? cursor.value : null }, current.signal);
    const next = page.items.map(auditActivity);
    const known = new Set(more ? items.value.map((item) => item.id) : []);
    items.value = [...(more ? items.value : []), ...next.filter((item) => !known.has(item.id))];
    cursor.value = page.nextBeforeId;
    hasMore.value = page.hasMore;
    since.value = page.coverageStartedAt;
    loaded.value = true;
  } catch (failure) {
    if (!current.signal.aborted) error.value = errorMessage(failure);
  } finally {
    if (!current.signal.aborted) loading.value = false;
  }
}
onMounted(() => void load());
onBeforeUnmount(() => controller?.abort());
</script>

<template>
  <SettingsPage :title="t.audit.title" :description="t.audit.description">
    <template v-if="context.isWikiAdmin && loaded" #actions>
      <Button variant="outline" type="button" :disabled="loading" data-audit-refresh @click="load()"><AtlasIcon name="refresh" :size="16" />{{ t.audit.refresh }}</Button>
    </template>

    <EmptyState v-if="!context.isWikiAdmin" icon="lock" :title="t.audit.adminOnlyTitle" :text="t.audit.adminOnlyText" />
    <template v-else>
      <ActivitySummarySection />
      <ErrorState v-if="error" :title="t.audit.loadFailed" :text="error" :busy="loading" @retry="load(lastMore)" />
      <LoadingSkeleton v-if="loading && !items.length" :label="t.audit.loading" />
      <EmptyState v-else-if="loaded && !items.length" icon="scroll-text" :title="t.audit.emptyTitle" :text="t.audit.emptyText" />
      <template v-else-if="items.length">
        <ActivityLog :items="items" :label="t.audit.title" :categories="auditCategoryLabels" />
        <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.audit.note }}</p>
        <div v-if="hasMore" class="st-actions">
          <LoadMoreButton older :busy="loading" data-audit-more @click="load(true)" />
        </div>
      </template>
      <p v-if="since" class="st-note" data-audit-coverage>
        <AtlasIcon name="info" :size="15" />{{ t.audit.coverage(formatDateTime(since)) }}
      </p>
    </template>
  </SettingsPage>
</template>
