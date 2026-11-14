<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import LoadingNote from "../settings/LoadingNote.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import { api, errorMessage, post } from "../../api";
import type { AuthMethods } from "../../administration";
import { formatDate } from "../../formatDate";
import t from "../../i18n/components-auth";

/**
 * Propojená přihlášení (Zabezpečení): a company identity joined to the existing account. Propojit
 * opens the confirmation in place under the provider's row; it continues to the provider only
 * after the explicit Ověřit a pokračovat.
 */
const emit = defineEmits<{ dirty: [value: boolean]; busy: [value: boolean] }>();
interface LinkedIdentity {
  providerId: string;
  displayName: string;
  linkedAt: string;
}
const methods = ref<AuthMethods>(), linked = ref<LinkedIdentity[]>([]), security = ref<{ hasBuiltinCredential: boolean; credentialState: string }>();
const loading = ref(true), loadError = ref(""), busy = ref(false), error = ref(""), selected = ref<string>(), password = ref("");
const root = ref<HTMLElement>();
let disposed = false;
watch(computed(() => !!selected.value || password.value.length > 0), (value) => emit("dirty", value), { flush: "sync" });
watch(busy, (value) => emit("busy", value), { flush: "sync" });
const passwordRequired = computed(() => !!methods.value?.builtinEnabled && !!security.value?.hasBuiltinCredential && security.value.credentialState === "active");
const linkOf = (providerId: string) => linked.value.find((identity) => identity.providerId === providerId);
const selectedName = computed(() => methods.value?.providers.find((provider) => provider.id === selected.value)?.displayName);

async function load() {
  loading.value = true;
  loadError.value = "";
  try {
    const found = await api<AuthMethods>("/auth/methods");
    if (disposed) return;
    if (!found.providers.length) {
      methods.value = found;
      linked.value = [];
      return;
    }
    const [identities, state] = await Promise.all([
      api<{ items: LinkedIdentity[] }>("/profile/identities"),
      api<{ hasBuiltinCredential: boolean; credentialState: string }>("/profile/security"),
    ]);
    if (!disposed) {
      methods.value = found;
      linked.value = identities.items;
      security.value = state;
    }
  } catch (failure) {
    if (!disposed) loadError.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
async function focus(selector: string) {
  await nextTick();
  root.value?.querySelector<HTMLElement>(selector)?.focus();
}
function start(providerId: string) {
  if (busy.value) return;
  selected.value = providerId;
  password.value = "";
  error.value = "";
  void focus(`[data-identity="${providerId}"] form :is(input, button[type="submit"])`);
}
function cancel() {
  if (busy.value) return;
  const providerId = selected.value;
  selected.value = undefined;
  password.value = "";
  error.value = "";
  if (providerId) void focus(`[data-identity="${providerId}"] [data-identity-link]`);
}
async function link() {
  if (!selected.value || busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    const result = await post<{ loginUrl: string }>(`/profile/identities/${encodeURIComponent(selected.value)}/link`, {
      ...(passwordRequired.value ? { password: password.value } : {}),
      returnUrl: "/#/profile/security",
    });
    if (disposed) return;
    password.value = "";
    const destination = new URL(result.loginUrl);
    if (!["http:", "https:"].includes(destination.protocol) || destination.username || destination.password)
      throw new Error(t.identity.badUrl);
    selected.value = undefined;
    busy.value = false;
    location.assign(destination.href);
  } catch (failure) {
    error.value = errorMessage(failure);
    password.value = "";
    busy.value = false;
  }
}
onMounted(load);
onUnmounted(() => {
  disposed = true;
  password.value = "";
  emit("dirty", false);
  emit("busy", false);
});
</script>

<template>
  <SettingsSection
    :title="t.identity.title"
    icon="link"
    :description="t.identity.description"
    class="identity-connections"
  >
    <div ref="root" class="identity-body">
      <LoadingNote v-if="loading && !methods" :label="t.identity.loading" />
      <ErrorState v-else-if="loadError && !methods" :title="t.identity.loadFailed" :text="loadError" :busy="loading" @retry="load" />
      <template v-else-if="methods">
        <SettingsList v-if="methods.providers.length" :label="t.identity.providers">
          <SettingsListRow
            v-for="provider in methods.providers"
            :key="provider.id"
            :title="provider.displayName"
            icon="identity"
            :icon-tone="linkOf(provider.id) ? 'success' : 'neutral'"
            :description="linkOf(provider.id) ? t.identity.linkedOn(formatDate(linkOf(provider.id)!.linkedAt)) : t.identity.notLinkedYet"
            :data-identity="provider.id"
          >
            <template #meta>
              <StatusBadge v-if="linkOf(provider.id)" tone="success" icon="link" :label="t.identity.linked" size="sm" />
              <StatusBadge v-else icon="unlink" :label="t.identity.notLinked" size="sm" />
            </template>
            <template v-if="!linkOf(provider.id)" #actions>
              <Button variant="outline" size="sm" type="button" :disabled="busy || selected === provider.id" :aria-expanded="selected === provider.id" data-identity-link @click="start(provider.id)"><AtlasIcon name="link" :size="15" />{{ t.identity.link }}</Button>
            </template>
            <InlineReveal :open="selected === provider.id">
              <form class="identity-confirm" :aria-label="t.identity.confirmLabel" @submit.prevent="link">
                <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.identity.continueTo(selectedName ?? "") }}</p>
                <SettingsRow v-if="passwordRequired" v-slot="{ id, describedBy }" :label="t.identity.confirmPassword" :help="t.identity.confirmPasswordHelp" stacked>
                  <Input :id="id" v-model="password" type="password" required autocomplete="current-password" :aria-describedby="describedBy" :disabled="busy" />
                </SettingsRow>
                <p v-else class="st-note"><AtlasIcon name="fingerprint" :size="15" />{{ t.identity.recentSignIn }}</p>
                <Transition name="st-fade"><p v-if="error" class="identity-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p></Transition>
                <div class="st-actions is-end">
                  <Button variant="ghost" type="button" :disabled="busy" data-identity-cancel @click="cancel">{{ t.identity.cancel }}</Button>
                  <Button type="submit" :disabled="busy" :aria-busy="busy"><AtlasIcon :name="busy ? 'loader-circle' : 'external-link'" :size="15" :class="{ 'atlas-spinner': busy }" />{{ busy ? t.identity.redirecting : t.identity.verify }}</Button>
                </div>
              </form>
            </InlineReveal>
          </SettingsListRow>
        </SettingsList>
        <EmptyState v-else compact icon="identity" :title="t.identity.emptyTitle" :text="t.identity.emptyText" />
      </template>
    </div>
  </SettingsSection>
</template>

<style scoped>
.identity-body { display: grid; gap: 10px; min-width: 0; }
.identity-confirm { display: grid; gap: 10px; margin: 0 0 14px; padding: 14px 16px; border: 1px solid var(--line); border-radius: var(--st-radius-sm); background: color-mix(in srgb, var(--canvas) 55%, var(--surface)); }
.identity-error { display: flex; align-items: flex-start; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
.identity-error > svg { flex-shrink: 0; margin-top: 2px; }
</style>
