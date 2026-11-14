<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import LoadingNote from "../settings/LoadingNote.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import ConfirmWithReauth from "../settings/ConfirmWithReauth.vue";
import { disclosureHooks } from "../settings/disclosure";
import { provideReauthEnvironment } from "../settings/useReauth";
import { createApiReauthEnvironment } from "../settings/security/apiReauth";
import { createSecurityState } from "../settings/security/securityState";
import { api, clearCsrf, errorMessage, post } from "../../api";
import { securityChange } from "../../protection";
import t from "../../i18n/components-auth";

/**
 * Heslo (Zabezpečení): the own local password. The change opens in place under the section; a
 * mismatch is said at the confirmation field, a refusal from the server under the form.
 * An account without one (signed in through the Hub) sets its first one here, with a sign-in name
 * when it has none yet, confirmed by a fresh Hub sign-in (ConfirmWithReauth) instead of a current
 * password; from then on it signs in either way.
 */
const emit = defineEmits<{ changed: []; dirty: [value: boolean]; busy: [value: boolean] }>();
const security = ref<{ hasBuiltinCredential: boolean; credentialState: "active" | "pending" | "none"; username: string | null; email: string | null }>();
const loading = ref(true), loadError = ref(""), busy = ref(false), error = ref(""), notice = ref("");
const changing = ref(false), submitted = ref(false);
const form = reactive({ currentPassword: "", newPassword: "", confirmation: "", username: "" });
// The profile is not inside the settings host, so the fresh sign-in of the first password is provided here.
const reauthState = createSecurityState();
provideReauthEnvironment(createApiReauthEnvironment(reauthState));
const settingFirst = computed(() => security.value?.credentialState !== "active");
const firstMismatch = computed(() => (form.confirmation && form.newPassword !== form.confirmation ? t.password.mismatch : ""));
const canSetFirst = computed(() => form.newPassword.length >= 12 && form.newPassword === form.confirmation && (!!security.value?.username || !!form.username.trim()));
const root = ref<HTMLElement>();
let disposed = false;
watch(computed(() => Object.values(form).some((value) => value.length > 0)), (value) => emit("dirty", value), { flush: "sync" });
watch(busy, (value) => emit("busy", value), { flush: "sync" });
const mismatch = computed(() => (submitted.value && form.newPassword !== form.confirmation ? t.password.mismatch : ""));

function clear() {
  Object.assign(form, { currentPassword: "", newPassword: "", confirmation: "", username: "" });
  submitted.value = false;
}
async function load() {
  loading.value = true;
  loadError.value = "";
  try {
    const result = await api<typeof security.value>("/profile/security");
    if (!disposed) security.value = result;
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
function open() {
  clear();
  error.value = "";
  notice.value = "";
  changing.value = true;
  if (settingFirst.value) {
    form.username = security.value?.email ?? "";
    void reauthState.load();
  }
  void focus(settingFirst.value ? ".first-password input" : ".password-form input");
}
async function setFirst(proof: string | null) {
  if (busy.value || !canSetFirst.value) return;
  error.value = "";
  busy.value = true;
  try {
    await securityChange("/profile/password/local", "POST", { username: form.username.trim() || null, newPassword: form.newPassword }, proof);
    clear();
    if (disposed) return;
    changing.value = false;
    await load();
    notice.value = t.password.localSet(security.value?.username ?? "");
    void focus("[data-password-change]");
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) busy.value = false;
  }
}
function cancel() {
  if (busy.value) return;
  clear();
  error.value = "";
  changing.value = false;
  void focus("[data-password-change]");
}
async function save() {
  if (busy.value || security.value?.credentialState !== "active") return;
  error.value = "";
  notice.value = "";
  submitted.value = true;
  if (mismatch.value) return;
  busy.value = true;
  try {
    await post("/profile/password", { currentPassword: form.currentPassword, newPassword: form.newPassword });
    clear();
    clearCsrf();
    if (disposed) return;
    changing.value = false;
    notice.value = t.password.changed;
    emit("changed");
    void focus("[data-password-change]");
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) busy.value = false;
  }
}
onMounted(load);
onUnmounted(() => {
  disposed = true;
  clear();
  emit("dirty", false);
  emit("busy", false);
});
</script>

<template>
  <SettingsSection :title="t.password.title" icon="lock" :description="t.password.description" class="password-settings">
    <div ref="root" class="password-body st-bleed">
      <LoadingNote v-if="loading && !security" :label="t.password.loading" />
      <ErrorState v-else-if="loadError && !security" :title="t.password.loadFailed" :text="loadError" :busy="loading" @retry="load" />
      <template v-else-if="security">
        <template v-if="settingFirst">
          <ResultBanner v-if="notice" :message="notice" @dismiss="notice = ''" />
          <InlineReveal :open="!changing">
            <SettingsRow :label="t.password.local" :label-for="false" :help="t.password.localHelp">
              <div class="st-actions"><Button variant="outline" type="button" data-password-change @click="open"><AtlasIcon name="key-round" :size="15" />{{ t.password.setLocal }}</Button></div>
            </SettingsRow>
          </InlineReveal>
          <InlineReveal :open="changing">
            <ConfirmWithReauth class="first-password" :confirm-label="t.password.setPassword" :cancel-label="t.password.cancel" :title="t.password.confirmTitle" :disabled="!canSetFirst" :busy="busy" :error="error" @confirm="setFirst" @cancel="cancel">
              <SettingsRow v-if="!security.username" v-slot="{ id, describedBy }" :label="t.password.username" :help="t.password.usernameHelp">
                <Input :id="id" v-model="form.username" required maxlength="100" autocomplete="username" :aria-describedby="describedBy" :disabled="busy" />
              </SettingsRow>
              <SettingsRow v-else :label="t.password.username" :label-for="false"><span>{{ security.username }}</span></SettingsRow>
              <SettingsRow v-slot="{ id, describedBy }" :label="t.password.newPassword" :help="t.password.newPasswordHelp">
                <Input :id="id" v-model="form.newPassword" required type="password" minlength="12" maxlength="128" autocomplete="new-password" :aria-describedby="describedBy" :disabled="busy" />
              </SettingsRow>
              <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.password.confirmPassword" :error="firstMismatch || undefined">
                <Input :id="id" v-model="form.confirmation" required type="password" minlength="12" maxlength="128" autocomplete="new-password" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="busy" />
              </SettingsRow>
            </ConfirmWithReauth>
          </InlineReveal>
        </template>
        <template v-else>
          <ResultBanner v-if="notice" :message="notice" @dismiss="notice = ''" />
          <InlineReveal :open="!changing">
            <SettingsRow :label="t.password.change" :label-for="false" :help="t.password.changeHelp">
              <div class="st-actions"><Button variant="outline" type="button" data-password-change @click="open"><AtlasIcon name="key-round" :size="15" />{{ t.password.changeAction }}</Button></div>
            </SettingsRow>
          </InlineReveal>
          <InlineReveal :open="changing">
            <form class="password-form" :aria-label="t.password.formLabel" @submit.prevent="save">
              <SettingsRow v-slot="{ id, describedBy }" :label="t.password.current">
                <Input :id="id" v-model="form.currentPassword" required type="password" autocomplete="current-password" :aria-describedby="describedBy" :disabled="busy" />
              </SettingsRow>
              <SettingsRow v-slot="{ id, describedBy }" :label="t.password.newPassword" :help="t.password.newPasswordHelp">
                <Input :id="id" v-model="form.newPassword" required type="password" minlength="12" maxlength="128" autocomplete="new-password" :aria-describedby="describedBy" :disabled="busy" />
              </SettingsRow>
              <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.password.confirmPassword" :error="mismatch || undefined">
                <Input :id="id" v-model="form.confirmation" required type="password" minlength="12" maxlength="128" autocomplete="new-password" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="busy" />
              </SettingsRow>
              <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="password-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p></Transition>
              <div class="st-actions is-end">
                <Button variant="ghost" type="button" :disabled="busy" data-password-cancel @click="cancel">{{ t.password.cancel }}</Button>
                <Button type="submit" :disabled="busy" :aria-busy="busy"><AtlasIcon :name="busy ? 'loader-circle' : 'key-round'" :size="15" :class="{ 'atlas-spinner': busy }" />{{ t.password.changeAction }}</Button>
              </div>
            </form>
          </InlineReveal>
        </template>
      </template>
    </div>
  </SettingsSection>
</template>

<style scoped>
/* Not a grid with a gap: the row and the form fold in turn, and a folded reveal would still keep its gap. The banner owns its space.
   The body reaches the card edges (st-bleed, settings.css), so its rows line up with the rows of every other card and are
   not inset twice (32 px above the first label instead of 16); what is not a row keeps the card inset itself. */
.password-body { min-width: 0; }
.password-body > :not(.inline-reveal-root) { margin: var(--st-body-pad-y, 16px) var(--st-pad-x, 18px); }
.password-body > :deep(.result-banner) { margin: var(--st-body-pad-y, 16px) var(--st-pad-x, 18px) 0; }
/* A column of flex items: the error unfolds with the gap above it (disclosure.ts). */
.password-form { display: flex; flex-direction: column; gap: 4px; }
.password-form > :not(.settings-row) { margin-inline: var(--st-pad-x, 18px); }
.password-form > .st-actions:last-child { margin-bottom: var(--st-body-pad-y, 16px); }
/* The fresh sign-in box and its buttons keep the card inset; the rows of the slot reach the edges like the rows above. */
.first-password { padding-bottom: var(--st-body-pad-y, 16px); }
.first-password > :deep(:not(.confirm-reauth-summary)) { margin-inline: var(--st-pad-x, 18px); }
.password-error { display: flex; align-items: flex-start; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
.password-error > svg { flex-shrink: 0; margin-top: 2px; }
</style>
