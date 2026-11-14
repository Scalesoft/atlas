<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import { Button } from "../ui/button";
import { errorMessage } from "../../api";
import t from "../../i18n/settings-activity";
import ActivityTimeline from "./ActivityTimeline.vue";
import ErrorState from "./ErrorState.vue";
import SettingsSection from "./SettingsSection.vue";
import type { ActivityItem } from "./activity";
import { auditActivity, loadAccessAudit, type AccessAuditFilter } from "./auditActivity";

/**
 * Činnost of one person, group or space: the access audit narrowed on the server (userId as actor or subject, groupId,
 * spaceId), newest first, older pages on request, and a link to the whole audit. Wiki administrators only (the API).
 */
const props = withDefaults(defineProps<{ filter: AccessAuditFilter; label: string; description?: string; historyHref?: string; emptyText?: string }>(), {
  description: undefined,
  historyHref: undefined,
  emptyText: undefined,
});
const items = ref<ActivityItem[]>([]);
const cursor = ref<string | null>(null);
const hasMore = ref(false);
const loading = ref(true);
const error = ref("");
let controller: AbortController | undefined;

async function load(more = false) {
  controller?.abort();
  const current = (controller = new AbortController());
  loading.value = true;
  error.value = "";
  try {
    const page = await loadAccessAudit({ limit: 50, beforeId: more ? cursor.value : null, ...props.filter }, current.signal);
    const known = new Set(more ? items.value.map((item) => item.id) : []);
    items.value = [...(more ? items.value : []), ...page.items.map(auditActivity).filter((item) => !known.has(item.id))];
    cursor.value = page.nextBeforeId;
    hasMore.value = page.hasMore;
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
  <SettingsSection :title="t.panel.title" icon="scroll-text" :description="description" data-audit-panel>
    <LoadingSkeleton v-if="loading && !items.length" :rows="4" row-height="52px" :label="t.panel.loading" />
    <ErrorState v-else-if="error && !items.length" :title="t.panel.failed" :text="error" :busy="loading" @retry="load()" />
    <ActivityTimeline v-else :items="items" :label="label" :empty-title="t.panel.emptyTitle" :empty-text="emptyText ?? t.panel.emptyText" />
    <div class="st-actions audit-panel-actions">
      <Button v-if="hasMore" variant="outline" size="sm" type="button" :disabled="loading" data-audit-more @click="load(true)">
        <AtlasIcon :name="loading ? 'loader-circle' : 'history'" :size="15" :class="{ 'atlas-spinner': loading }" />{{ t.panel.older }}
      </Button>
      <a v-if="historyHref" class="st-link" :href="historyHref" data-audit-history><AtlasIcon name="scroll-text" :size="15" />{{ t.panel.wholeAudit }}</a>
    </div>
  </SettingsSection>
</template>

<style scoped>
.audit-panel-actions { justify-content: flex-start; align-items: center; }
</style>
