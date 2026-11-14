<script setup lang="ts">
import t from "../../i18n/settings-shell";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";

/**
 * A page that follows server push (useLiveFeed: job runs, the server log, a page's PDF export) says quietly that its live updates are off
 * (the connection is lost, or the server refused the feed) and offers a manual refresh; what is shown stays. Nothing polls behind it. Use
 * ConflictNote for a 409, ErrorState for a failed load.
 */
defineProps<{ busy?: boolean }>();
const emit = defineEmits<{ refresh: [] }>();
</script>

<template>
  <p class="live-update-note" role="status" data-live-update-note>
    <AtlasIcon name="info" :size="15" aria-hidden="true" />
    <span>{{ t.liveUpdate.unavailable }}</span>
    <Button size="sm" variant="ghost" type="button" :disabled="busy" data-live-update-refresh @click="emit('refresh')">{{ t.liveUpdate.refresh }}</Button>
  </p>
</template>

<style scoped>
.live-update-note { display: flex; align-items: center; gap: 7px; margin: 0; color: var(--muted); font-size: 13px; }
.live-update-note > svg { flex-shrink: 0; }
</style>
