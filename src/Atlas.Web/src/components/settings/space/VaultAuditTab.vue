<script setup lang="ts">
import t from "../../../i18n/settings-space";
import { onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import type { Vault } from "../../../vaults";
import ActivityLog from "../ActivityLog.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import type { ActivityItem } from "../activity";
import { auditActivity, auditCategoryLabels, vaultAuditSource } from "../auditActivity";
import { useSettingsContext } from "../context";
import LoadingNote from "../LoadingNote.vue";
import LoadMoreButton from "../../LoadMoreButton.vue";

// Audit of one vault (V5 GET /api/vaults/{id}/audit): password releases and refusals, client reports,
// access requests and grants of the vault and its secrets, for every Spravuje holder (space
// administrators and the wiki administrator included). Filters Kdo / Co / Kdy / Výsledek live in the URL.
const props = defineProps<{ vault: Vault }>();
const context = useSettingsContext();
const available = vaultAuditSource.available({ isWikiAdmin: context.isWikiAdmin, level: props.vault.level });
const items = ref<ActivityItem[]>([]);
const cursor = ref<string | null>(null);
const hasMore = ref(false);
const loading = ref(false);
const error = ref("");
let controller: AbortController | undefined;

async function load(more = false) {
  if (!available) return;
  controller?.abort();
  controller = new AbortController();
  loading.value = true;
  error.value = "";
  try {
    const page = await vaultAuditSource.load(props.vault.id, { limit: 50, beforeId: more ? cursor.value : null }, controller.signal);
    const next = page.items.map(auditActivity);
    items.value = more ? [...items.value, ...next.filter((item) => !items.value.some((known) => known.id === item.id))] : next;
    cursor.value = page.nextBeforeId;
    hasMore.value = page.hasMore;
  } catch (failure) {
    if (!controller.signal.aborted) error.value = errorMessage(failure);
  } finally {
    loading.value = false;
  }
}
onMounted(() => void load());
onBeforeUnmount(() => controller?.abort());
</script>

<template>
  <div class="vault-audit">
    <template v-if="available">
      <ErrorState v-if="error" :title="t.audit.loadFailed" :text="error" :busy="loading" @retry="load()" />
      <LoadingNote v-else-if="loading && !items.length" :label="t.audit.loading" />
      <template v-else>
        <ActivityLog :items="items" :label="t.audit.label(vault.name)" :categories="auditCategoryLabels" />
        <!-- Only with something to load: an empty row still took its gap and pushed the note 28 px off the log. -->
        <div v-if="hasMore" class="st-actions">
          <LoadMoreButton older :busy="loading" data-audit-more @click="load(true)" />
        </div>
        <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.audit.note }}</p>
      </template>
    </template>
    <EmptyState v-else icon="lock" :title="t.audit.adminsOnly" :text="t.audit.adminsOnlyText" />
  </div>
</template>

<style scoped>
.vault-audit { display: grid; gap: 14px; min-width: 0; }
</style>
