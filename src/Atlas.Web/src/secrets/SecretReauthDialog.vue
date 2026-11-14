<script setup lang="ts">
import { computed } from "vue";
import "../components/settings/settings.css";
import ReauthDialog from "../components/settings/ReauthDialog.vue";
import { provideReauthEnvironment } from "../components/settings/useReauth";
import t from "../i18n/secrets-picker";
import type { SecretReauthController } from "./secretReauth";

/**
 * The fresh sign-in a release asks for (403 reauth_required), in the shared ReauthDialog: the environment follows the pending challenge, so one
 * dialog serves every reference of the editor. Loaded with the first request, never with the page.
 */
const props = defineProps<{ controller: SecretReauthController }>();
const pending = computed(() => props.controller.pending.value);
provideReauthEnvironment({
  get method() { return pending.value?.environment.method ?? "local"; },
  get supported() { return pending.value?.environment.supported ?? false; },
  verifyPassword: password => pending.value?.environment.verifyPassword(password) ?? Promise.resolve(false),
  verifyHub: () => pending.value?.environment.verifyHub() ?? Promise.resolve(false),
});
const copying = computed(() => pending.value?.request.operation === "copy");
</script>

<template>
  <ReauthDialog
    :open="!!pending"
    :attempt="pending?.challenge.id"
    :title="copying ? t.reauth.titleCopy : t.reauth.titleReveal"
    :heading="t.reauth.heading"
    :description="t.reauth.description"
    :confirm-label="copying ? t.reauth.confirmCopy : t.reauth.confirmReveal"
    :operation="pending?.request.operation"
    :target="pending ? { secretId: pending.request.secretId } : {}"
    @confirm="pending?.confirm()"
    @cancel="pending?.cancel()"
  />
</template>
