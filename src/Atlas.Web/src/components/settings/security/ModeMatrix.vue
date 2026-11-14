<script setup lang="ts">
import t from "../../../i18n/settings-security";
import AtlasIcon from "../../AtlasIcon.vue";
import { modeInfo } from "./protection";
import type { ProtectionMode } from "./types";

// The mini table of a mode: in a trusted LAN/VPN versus elsewhere, fresh sign-in or not.
defineProps<{ mode: ProtectionMode }>();
</script>

<template>
  <span class="mode-matrix" :data-mode="mode">
    <span class="mode-matrix-row">
      <span class="mode-matrix-where"><AtlasIcon name="building-2" :size="13" />LAN/VPN</span>
      <span class="mode-matrix-what" :data-reauth="modeInfo[mode].trusted || undefined"><AtlasIcon :name="modeInfo[mode].trusted ? 'fingerprint' : 'check'" :size="13" />{{ modeInfo[mode].trusted ? t.matrix.signInAgain : t.matrix.noSignIn }}</span>
    </span>
    <span class="mode-matrix-row">
      <span class="mode-matrix-where"><AtlasIcon name="globe" :size="13" />{{ t.matrix.elsewhere }}</span>
      <span class="mode-matrix-what" :data-reauth="modeInfo[mode].elsewhere || undefined"><AtlasIcon :name="modeInfo[mode].elsewhere ? 'fingerprint' : 'check'" :size="13" />{{ modeInfo[mode].elsewhere ? t.matrix.signInAgain : t.matrix.noSignIn }}</span>
    </span>
  </span>
</template>

<style scoped>
.mode-matrix { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 1px; overflow: hidden; border: 1px solid var(--line); border-radius: 8px; background: var(--line); font-size: 12px; }
.mode-matrix-row { display: contents; }
.mode-matrix-where, .mode-matrix-what { display: flex; align-items: center; gap: 4px; padding: 4px 7px; min-width: 0; white-space: nowrap; font-size: 11.5px; background: var(--surface); }
.mode-matrix-where { color: var(--muted); }
.mode-matrix-what { color: var(--success); font-weight: 600; }
.mode-matrix-what[data-reauth] { color: var(--info); }
</style>
