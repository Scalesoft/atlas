<script setup lang="ts">
// The shell's eager texts: the search palette shows ErrorState on the first screen, so it must not pull in the settings dictionary.
import shell from "../../i18n/shell";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";

// A failure next to what failed, with the reason and a way forward. Never a toast only.
withDefaults(defineProps<{ title: string; text?: string; retryLabel?: string; busy?: boolean }>(), { text: undefined, retryLabel: undefined, busy: false });
const emit = defineEmits<{ retry: [] }>();
</script>

<template>
  <div class="error-state" role="alert">
    <span class="st-icon-tile" data-tone="danger"><AtlasIcon name="octagon-alert" :size="20" /></span>
    <div class="error-state-text">
      <strong>{{ title }}</strong>
      <p v-if="text">{{ text }}</p>
      <slot />
    </div>
    <Button variant="outline" size="sm" type="button" :disabled="busy" @click="emit('retry')"><AtlasIcon name="refresh" :size="15" />{{ retryLabel ?? shell.common.retry }}</Button>
  </div>
</template>

<style scoped>
.error-state { display: flex; flex-wrap: wrap; align-items: center; gap: 12px 14px; padding: 14px 16px; border: 1px solid color-mix(in srgb, var(--error) 35%, var(--line)); border-radius: var(--st-radius); background: color-mix(in srgb, var(--error-bg) 55%, var(--surface)); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.error-state-text { flex: 1 1 240px; min-width: 0; }
.error-state strong { font-size: 14px; }
.error-state p { margin: 3px 0 0; color: var(--muted); font-size: 13px; line-height: 1.55; }
</style>
