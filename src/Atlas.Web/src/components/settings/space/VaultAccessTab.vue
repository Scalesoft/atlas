<script setup lang="ts">
import ResourceAccess from "../access/ResourceAccess.vue";
import t from "../../../i18n/settings-space";

// Přístupy of a vault or one secret: who reaches it and how (ResourceAccess over /api/access/resources/{vault|secret}/{id}).
// Every grant change of a vault or secret is a security change: ResourceAccess confirms it with a fresh sign-in for this
// vault or secret, reloads on a conflict and says what happened; Přidat přístup opens in place of the list.
defineProps<{ target: "vault" | "secret"; id: string }>();
</script>

<template>
  <ResourceAccess
    class="vault-access-tab"
    :kind="target"
    :id="id"
    :note="target === 'secret' ? t.vault.accessNote : undefined"
  />
</template>
