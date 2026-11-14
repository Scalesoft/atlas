<script setup lang="ts">
import { ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import { clearCsrf, errorMessage, post } from "../../api";
import t from "../../i18n/profile";

/**
 * Ostatní zařízení (Zabezpečení, SEC-A-6): signs the account out everywhere but here. A lost laptop or a stolen cookie no longer
 * waits for a password change, which an account signing in only through OIDC does not even have. The server rotates the account's
 * stamp, so the personal API tokens end too; the confirmation says so.
 */
const text = t.otherSessions;
const emit = defineEmits<{ changed: [] }>();
const confirming = ref(false), busy = ref(false), error = ref(""), notice = ref("");

async function endOthers() {
  busy.value = true;
  error.value = "";
  try {
    await post("/profile/sessions/end-others", {});
    // The CSRF token belongs to the old stamp.
    clearCsrf();
    confirming.value = false;
    notice.value = text.done;
    emit("changed");
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <SettingsSection :title="text.title" icon="logout" :description="text.description">
    <ResultBanner v-if="notice" :message="notice" @dismiss="notice = ''" />
    <SettingsRow :label="text.action" :label-for="false" :help="text.help">
      <div class="st-actions"><Button variant="outline" type="button" :disabled="busy" data-end-other-sessions @click="confirming = true; notice = ''"><AtlasIcon name="logout" :size="15" />{{ text.action }}</Button></div>
    </SettingsRow>
    <ConfirmDialog :open="confirming" :title="text.confirmTitle" :confirm-label="text.confirm" confirm-icon="logout" :busy="busy" :error="error || undefined" @confirm="endOthers" @cancel="confirming = false">
      <p>{{ text.confirmText }}</p>
    </ConfirmDialog>
  </SettingsSection>
</template>
