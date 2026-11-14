<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { ref, watch } from "vue";
import { lastUsagePrompt } from "../../contacts/contactLinking";
import ConfirmDialog from "../settings/ConfirmDialog.vue";

/**
 * K2 (spec 2026-10-30 §7): the reference just removed (a board's card, „Odebrat“ in a page) was the contact's last use. „Přesunout do koše“
 * (the default; owner 2026-10-30) moves it to the object trash, from which alone it is deleted for good; „Ponechat jako nepoužitý“, Esc and the close button keep it. Mounted once by the app;
 * the question comes from contactLinking.ts (`askLastUsage`).
 */
// The name stays while the dialog leaves.
const name = ref("");
watch(lastUsagePrompt, prompt => { if (prompt) name.value = prompt.name; }, { immediate: true });
</script>

<template>
  <ConfirmDialog :open="!!lastUsagePrompt" :title="t.lastUsage.title" :confirm-label="t.lastUsage.confirm" confirm-icon="trash" :cancel-label="t.lastUsage.keep"
    data-contact-last-usage @confirm="lastUsagePrompt?.answer(true)" @cancel="lastUsagePrompt?.answer(false)">
    <p>{{ t.lastUsage.lead1 }} <strong>{{ name }}</strong> {{ t.lastUsage.lead2 }}</p>
    <p>{{ t.lastUsage.text2 }}</p>
  </ConfirmDialog>
</template>
