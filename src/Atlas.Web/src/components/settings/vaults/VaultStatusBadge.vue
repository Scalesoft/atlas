<script setup lang="ts">
import t from "../../../i18n/settings-vaults";
import StatusBadge from "../StatusBadge.vue";
import type { VaultStatus } from "../../../vaults";
import type { StatusTone } from "../types";

// V2 vault states (src/vaults.ts) as icon + word.
defineProps<{ status: VaultStatus; size?: "sm" | "md" }>();
const map: Record<VaultStatus, { tone: StatusTone; icon: string; label: string }> = {
  unlocked: { tone: "success", icon: "circle-check", label: t.status.unlocked },
  needsPassword: { tone: "warning", icon: "key-round", label: t.status.needsPassword },
  unavailable: { tone: "danger", icon: "triangle-alert", label: t.status.unavailable },
  error: { tone: "danger", icon: "octagon-alert", label: t.status.error },
};
</script>

<template>
  <StatusBadge :tone="map[status].tone" :icon="map[status].icon" :label="map[status].label" :size="size ?? 'sm'" />
</template>
