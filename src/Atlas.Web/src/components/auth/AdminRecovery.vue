<script setup lang="ts">
import { onMounted, onUnmounted, reactive, ref } from "vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import FormField from "../FormField.vue";
import { disclosureHooks } from "../settings/disclosure";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import { api, clearCsrf, errorMessage, post, type User } from "../../api";
import t from "../../i18n/components-auth";
interface RecoveryStatus {
  available: boolean;
  builtinEnabled: boolean;
  tokenFilePath: string;
  instructions: string;
}
const emit = defineEmits<{ cancel: []; recovered: [user: User] }>();
const status = ref<RecoveryStatus>(),
  loading = ref(true),
  busy = ref(false),
  error = ref("");
const form = reactive({ token: "", newPassword: "", confirmPassword: "" });
let disposed = false;
async function load() {
  loading.value = true;
  error.value = "";
  try {
    const result = await api<RecoveryStatus>("/auth/recovery/status");
    if (!disposed) status.value = result;
  } catch (e) {
    if (!disposed) error.value = errorMessage(e);
  } finally {
    loading.value = false;
  }
}
async function recover() {
  if (busy.value || !status.value?.available)
    return;
  error.value = "";
  if (form.newPassword !== form.confirmPassword) {
    error.value = t.recovery.mismatch;
    return;
  }
  busy.value = true;
  try {
    const result = await post<User>("/auth/recovery", {
      token: form.token.trim(),
      newPassword: form.newPassword,
    });
    Object.assign(form, { token: "", newPassword: "", confirmPassword: "" });
    clearCsrf();
    if (!disposed) emit("recovered", result);
  } catch (e) {
    error.value = errorMessage(e);
  } finally {
    busy.value = false;
  }
}
onMounted(load);
onUnmounted(() => {
  disposed = true;
  Object.assign(form, { token: "", newPassword: "", confirmPassword: "" });
});
</script>
<template>
  <section class="admin-recovery" aria-labelledby="recovery-heading">
    <h2 id="recovery-heading">{{ t.recovery.title }}</h2>
    <p class="help">
      {{ t.recovery.help }}
    </p>
    <LoadingSkeleton v-if="loading" :rows="3" row-height="64px" :label="t.recovery.loading" />
    <template v-if="status"
      ><p class="help">{{ status.instructions }}</p>
      <p v-if="status.tokenFilePath" class="recovery-path">
        {{ t.recovery.file }} <code>{{ status.tokenFilePath }}</code>
      </p>
      <form
        v-if="status.available"
        :aria-label="t.recovery.formLabel"
        @submit.prevent="recover"
      >
        <fieldset :disabled="busy">
          <FormField :label="t.recovery.token"
            ><Input
              v-model="form.token"
              required
              type="password"
              autocomplete="off"
              spellcheck="false" /></FormField
          ><FormField :label="t.recovery.newPassword"
            ><Input
              v-model="form.newPassword"
              required
              type="password"
              minlength="12"
              maxlength="128"
              autocomplete="new-password" /></FormField
          ><FormField :label="t.recovery.confirmPassword"
            ><Input
              v-model="form.confirmPassword"
              required
              type="password"
              minlength="12"
              maxlength="128"
              autocomplete="new-password"
          /></FormField>
          <p class="help">
            {{ t.recovery.tokenHelp }}
          </p>
        </fieldset>
        <Button variant="default" :disabled="busy">
          {{ busy ? t.recovery.busy : t.recovery.submit }}
        </Button>
      </form>
      <p v-else class="notice">
        {{ t.recovery.unavailable }}
      </p></template
    >
    <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="notice error" role="alert">{{ error }}</p></Transition>
    <button
      v-if="error && !status"
      type="button"
      :disabled="busy || loading"
      @click="load"
    >
      {{ t.recovery.reload }}</button
    ><button
      class="login-alternative"
      type="button"
      :disabled="busy"
      @click="emit('cancel')"
    >
      {{ t.recovery.back }}
    </button>
  </section>
</template>
