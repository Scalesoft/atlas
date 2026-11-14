<script setup lang="ts">
import t from "../../../i18n/settings-security";
import StatusBadge from "../StatusBadge.vue";
import { modeInfo } from "./protection";
import type { ProtectionMode, ProtectionSetting } from "./types";

// The effective mode with its origin in one chip: "Vždy · zděděno" or "Vždy · vlastní".
defineProps<{ mode: ProtectionMode; setting: ProtectionSetting; from?: string }>();
</script>

<template>
  <StatusBadge
    :tone="setting === 'inherit' ? 'neutral' : mode === 'never' ? 'warning' : 'accent'"
    :icon="modeInfo[mode].icon"
    :label="t.chip(modeInfo[mode].label, setting === 'inherit', from)"
    size="sm"
  />
</template>
