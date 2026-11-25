<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { errorMessage } from "../../../api";
import { hubErrorMessage, hubModeLabels, profileHubApi, type ProfileHubStatus } from "../../../hub";
import { formatDateTime } from "../../../formatDate";
import t from "../../../i18n/settings-access";
import { Button } from "../../ui/button";
import SettingsSection from "../SettingsSection.vue";
import SummaryList from "../SummaryList.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";

const emit = defineEmits<{ verified: [] }>();
const status = ref<ProfileHubStatus>();
const busy = ref(false);
const error = ref("");
const now = ref(Date.now());
let expiryTimer: ReturnType<typeof setInterval> | undefined;
let disposed = false;
const stateLabel = computed(() => {
  const identity = status.value?.identity;
  if (!identity) return "";
  if (status.value?.mode === "Disabled") return t.hub.verificationOff;
  if (identity.verifiedUntil && new Date(identity.verifiedUntil).getTime() <= now.value) return t.hub.expired;
  return t.hub.state[identity.state] ?? t.hub.state.Unverified!;
});
const date = (value: string | null) => value ? formatDateTime(value) : t.hub.notYet;
const facts = computed(() => {
  const identity = status.value?.identity;
  return identity ? [
    { key: "state", icon: "shield-check", label: t.hub.status, value: stateLabel.value },
    { key: "mode", icon: "settings", label: t.hub.mode, value: status.value?.mode ? hubModeLabels[status.value.mode] : t.hub.off },
    { key: "until", icon: "clock", label: t.hub.verifiedUntil, value: identity.verifiedUntil ? date(identity.verifiedUntil) : t.hub.noValid },
    { key: "success", icon: "circle-check", label: t.hub.lastSuccess, value: date(identity.lastSuccessAt) },
    { key: "attempt", icon: "refresh", label: t.hub.lastAttempt, value: date(identity.lastAttemptAt) },
  ] : [];
});
async function load(verify = false) {
  if (busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    if (verify) await profileHubApi.verify();
    const next = await profileHubApi.status();
    if (disposed) return;
    status.value = next;
    if (verify) emit("verified");
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) busy.value = false;
  }
}
onMounted(() => {
  expiryTimer = setInterval(() => { now.value = Date.now(); }, 1000);
  void load();
});
onBeforeUnmount(() => { disposed = true; clearInterval(expiryTimer); });
</script>

<template>
  <SettingsSection v-if="status?.identity || !status" :title="t.hub.title" icon="users" :description="t.hub.description" :aria-busy="busy || undefined">
    <LoadingNote v-if="busy && !status" :label="t.hub.loading" />
    <ErrorState v-if="error && !status" :title="t.hub.loadFailed" :text="error" :busy="busy" @retry="load()" />
    <template v-if="status?.identity">
      <SummaryList :items="facts" :label="t.hub.label" />
      <p v-if="status.mode === 'Preview'" class="st-help">{{ t.hub.previewNote }}</p>
      <p v-if="status.identity.lastErrorCode" class="st-help">{{ hubErrorMessage(status.identity.lastErrorCode) }}</p>
      <p v-if="error" role="alert">{{ error }}</p>
      <Button variant="outline" type="button" :disabled="busy || status.mode === 'Disabled'" data-profile-hub-verify @click="load(true)">{{ t.hub.refresh }}</Button>
    </template>
  </SettingsSection>
</template>
