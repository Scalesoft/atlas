<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import t from "../../../i18n/settings-requests";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import { formatDate, formatDateTime } from "../../../formatDate";
import { accessRequestApi, type AccessRequest, type AccessRequestState } from "../../../secretRelease";
import { createSecretAccessEvents, type SecretAccessEvents } from "../../../secrets/secretEvents";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import type { StatusTone } from "../types";

/**
 * Moje žádosti (profile, everyone): the caller's own access requests, newest first, with their decision and note. A pending one
 * can be withdrawn. Live: accessRequestChanged on /hubs/markdown (the subscription of the password references) reloads the list.
 */
const props = withDefaults(defineProps<{ events?: () => SecretAccessEvents }>(), { events: () => createSecretAccessEvents() });
const items = ref<AccessRequest[]>([]);
const loading = ref(true);
const error = ref("");
const busy = ref<string | null>(null);
let disposed = false;
let stop: (() => void) | undefined;

async function load() {
  try {
    const page = await accessRequestApi.mine();
    if (disposed) return;
    items.value = [...page.items].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    error.value = "";
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
onMounted(() => {
  void load();
  stop = props.events().onRequestChanged(() => void load());
});
onBeforeUnmount(() => {
  disposed = true;
  stop?.();
});

const states: Record<AccessRequestState, { label: string; tone: StatusTone; icon: string }> = {
  pending: { label: t.mine.states.pending, tone: "warning", icon: "hourglass" },
  approved: { label: t.mine.states.approved, tone: "success", icon: "circle-check" },
  rejected: { label: t.mine.states.rejected, tone: "danger", icon: "circle-x" },
  cancelled: { label: t.mine.states.cancelled, tone: "neutral", icon: "ban" },
};
const pending = computed(() => items.value.filter((item) => item.state === "pending"));
const decided = computed(() => items.value.filter((item) => item.state !== "pending"));
const title = (request: AccessRequest) => request.secretTitle ?? t.mine.wholeVault(request.vaultName);
function outcome(request: AccessRequest) {
  if (request.state === "approved") return t.mine.approvedBy(request.decidedByName ?? t.mine.admin, request.grantExpiresAt ? formatDate(request.grantExpiresAt) : undefined);
  if (request.state === "rejected") return t.mine.rejectedBy(request.decidedByName ?? t.mine.admin, request.note);
  if (request.state === "cancelled") return t.mine.cancelled;
  return t.mine.sent(formatDateTime(request.createdAt), request.reason);
}
async function cancel(request: AccessRequest) {
  busy.value = request.id;
  error.value = "";
  try {
    const done = await accessRequestApi.cancel(request.id);
    items.value = items.value.map((item) => (item.id === done.id ? done : item));
  } catch (failure) {
    error.value = errorMessage(failure);
    void load();
  } finally {
    busy.value = null;
  }
}
</script>

<template>
  <div class="my-requests" data-my-requests>
    <ErrorState v-if="error && !items.length" :title="t.mine.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading" :label="t.mine.loading" />
    <template v-else>
      <p v-if="error" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
      <SettingsSection :title="t.mine.pending" icon="hourglass" :count="pending.length">
        <SettingsList v-if="pending.length" :label="t.mine.pendingLabel">
          <SettingsListRow v-for="request in pending" :key="request.id" :title="title(request)" :description="outcome(request)" :icon="request.secretId ? 'key-round' : 'lock'" :data-my-request="request.id"
            :menu="[{ id: 'cancel', label: t.mine.cancel, icon: 'close', destructive: true, disabled: busy === request.id }]" @select="cancel(request)">
            <template #meta><StatusBadge v-bind="states[request.state]" size="sm" /></template>
            <template #actions>
              <Button variant="ghost" size="sm" type="button" :disabled="busy === request.id" data-my-request-cancel @click="cancel(request)"><AtlasIcon :name="busy === request.id ? 'loader-circle' : 'close'" :size="15" :class="{ 'atlas-spinner': busy === request.id }" />{{ t.mine.cancel }}</Button>
            </template>
          </SettingsListRow>
        </SettingsList>
        <EmptyState v-else icon="circle-check" :title="t.mine.nothingWaits" :text="t.mine.nothingWaitsText" compact />
      </SettingsSection>
      <SettingsSection v-if="decided.length" :title="t.mine.decided" icon="history" :count="decided.length">
        <SettingsList :label="t.mine.decidedLabel">
          <SettingsListRow v-for="request in decided" :key="request.id" :title="title(request)" :description="outcome(request)" :icon="request.secretId ? 'key-round' : 'lock'" icon-tone="neutral" :data-my-request="request.id">
            <template #meta><StatusBadge v-bind="states[request.state]" size="sm" /></template>
          </SettingsListRow>
        </SettingsList>
      </SettingsSection>
      <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.mine.note }}</p>
    </template>
  </div>
</template>

<style scoped>
.my-requests { display: grid; gap: 16px; min-width: 0; }
</style>
