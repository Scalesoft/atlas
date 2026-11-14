<script setup lang="ts">
import { computed, onUnmounted, ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingRegion from "../LoadingRegion.vue";
import { Button } from "../ui/button";
import ActivityTimeline from "../settings/ActivityTimeline.vue";
import ErrorState from "../settings/ErrorState.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import type { ActivityItem, ActivityOutcome } from "../settings/activity";
import { errorMessage } from "../../api";
import { channelMark, tokenText } from "../../channel";
import { sharingApi, type PageAccessEvent, type PageCatalogMark } from "../../sharing";
import t from "../../i18n/components-sharing";

/**
 * „Historie otevření“ of one page for its managers: who opened the page or one of its anonymous links and what
 * happened to the links, newest first, fifty at a time with an exact string cursor. Loaded only when this part opens.
 */
const props = defineProps<{ pageId: string }>();
const history = ref<PageAccessEvent[]>(), nextBeforeId = ref<string | null>(null);
const catalogMarks = ref<PageCatalogMark[]>([]);
const loading = ref(false), error = ref("");
let failedBefore: string | undefined, request = 0, disposed = false;

async function load(beforeId?: string) {
  if (loading.value) return;
  const current = ++request;
  loading.value = true; error.value = "";
  try {
    const page = await sharingApi.accessHistory(props.pageId, beforeId);
    if (disposed || current !== request) return;
    history.value = beforeId ? [...(history.value ?? []), ...page.items] : page.items;
    nextBeforeId.value = page.nextBeforeId;
    // Only the first page carries them.
    if (!beforeId) catalogMarks.value = page.catalogMarks ?? [];
  } catch (failure) {
    if (disposed || current !== request) return;
    error.value = errorMessage(failure);
    failedBefore = beforeId;
  } finally {
    if (!disposed && current === request) loading.value = false;
  }
}
void load();

const kinds: Record<PageAccessEvent["action"], { action: string; icon: string; outcome: ActivityOutcome }> = {
  "page.opened": { action: t.history.pageOpened, icon: "eye", outcome: "allowed" },
  "share.opened": { action: t.history.shareOpened, icon: "link", outcome: "allowed" },
  "share.created": { action: t.history.shareCreated, icon: "link", outcome: "changed" },
  "share.revoked": { action: t.history.shareRevoked, icon: "unlink", outcome: "changed" },
  "page.source-edited": { action: t.history.sourceEdited, icon: "code-xml", outcome: "changed" },
};
const items = computed<ActivityItem[]>(() => (history.value ?? []).map(event => {
  const kind = kinds[event.action];
  return {
    id: event.id,
    at: event.occurredAt,
    actorId: event.actorId ?? event.action,
    // An anonymous opening has no person: the link is the only attribution.
    actor: event.action === "share.opened" ? t.history.anonymous : event.actorName ?? t.history.system,
    action: [kind.action, event.action === "page.opened" ? channelMark(event.channel)?.label : undefined].filter(Boolean).join(" "),
    target: event.shareLinkName ? `„${event.shareLinkName}“` : undefined,
    context: t.history.version(event.revisionVersion),
    category: event.action,
    outcome: kind.outcome,
    icon: kind.icon,
    details: tokenText(event.apiToken) ? [{ label: t.history.token, value: tokenText(event.apiToken)! }] : undefined,
  };
}));
/** Who made the page its space's requirement catalog and who took that back (module Projector, T3). */
const markItems = computed<ActivityItem[]>(() => catalogMarks.value.map(mark => ({
  id: `catalog-${mark.id}`,
  at: mark.occurredAt,
  actorId: mark.actorId,
  actor: mark.actorName ?? t.history.system,
  action: [mark.isCatalog ? t.history.catalogOn : t.history.catalogOff, channelMark(mark.channel)?.label].filter(Boolean).join(" "),
  category: "catalog",
  outcome: "changed",
  icon: "book-open",
  details: tokenText(mark.apiToken) ? [{ label: t.history.token, value: tokenText(mark.apiToken)! }] : undefined,
})));
onUnmounted(() => { disposed = true; request++; });
</script>

<template>
  <SettingsSection :title="t.history.title" icon="history" :description="t.history.description">
    <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.history.note }}</p>
    <ErrorState v-if="error" :title="t.history.loadFailed" :text="error" :busy="loading" @retry="load(failedBefore)" />
    <LoadingRegion v-if="history || !error" :loading="loading" :ready="!!history" :rows="3" :label="t.history.loading">
      <ActivityTimeline :items="items" :label="t.history.title" :empty-title="t.history.emptyTitle" :empty-text="t.history.emptyText" />
    </LoadingRegion>
    <div v-if="markItems.length" class="page-access-catalog" data-catalog-marks>
      <h4 class="page-access-catalog-title"><AtlasIcon name="book-open" :size="15" />{{ t.history.catalog }}</h4>
      <ActivityTimeline :items="markItems" :label="t.history.catalog" empty-title="" empty-text="" />
    </div>
    <div v-if="nextBeforeId" class="st-actions">
      <Button variant="outline" size="sm" type="button" :disabled="loading" data-load-older @click="load(nextBeforeId)"><AtlasIcon name="down" :size="15" />{{ t.history.older }}</Button>
    </div>
  </SettingsSection>
</template>

<style scoped>
.page-access-catalog { display: grid; gap: 8px; margin-top: 16px; }
.page-access-catalog-title { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--text); font-size: 13px; font-weight: 650; }
</style>
