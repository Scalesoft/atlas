<script setup lang="ts">
import { computed } from "vue";
import StatusBadge from "../StatusBadge.vue";
import { accountStatus, accountStatusLabels } from "./accountStatus";
import type { DirectoryUser } from "../../../administration";

// The state of an account (Aktivní / Čeká na aktivaci / Deaktivovaný) as icon and word.
const props = withDefaults(defineProps<{ account: Pick<DirectoryUser, "isActive" | "credentialState">; size?: "sm" | "md" }>(), { size: "sm" });
const status = computed(() => accountStatus(props.account));
const view = computed(() => accountStatusLabels[status.value]);
</script>

<template>
  <StatusBadge :tone="view.tone" :icon="view.icon" :label="view.label" :size="size" :data-account-status="status" />
</template>
