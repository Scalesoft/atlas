<script setup lang="ts">
import { computed } from "vue";
import StatusBadge from "./settings/StatusBadge.vue";
import { channelMark, tokenText, type ApiTokenRef } from "../channel";

/**
 * „přes MCP“ / „přes API“ / „přes asistenta“ beside an author or a publication. Static, a fact about the row, so it never animates.
 * Nothing for web, legacy (null) and unknown channels. With `detailed` (revision preview) import and system are named
 * as well and the token name follows when the server sent it (owner and wiki administrators only; the assistant has none).
 * Never an AI glyph: MCP is a protocol, and the assistant's mark is a fact about the row, not model output.
 */
const props = defineProps<{ channel?: string | null; token?: ApiTokenRef | null; detailed?: boolean }>();
const mark = computed(() => channelMark(props.channel, props.detailed));
const token = computed(() => (props.detailed ? tokenText(props.token) : null));
</script>

<template>
  <span v-if="mark" class="channel-badge" data-channel-badge :data-channel="channel" :title="token ? `${mark.label} · ${token}` : mark.label">
    <StatusBadge tone="neutral" :icon="mark.icon" :label="mark.label" size="sm" />
    <span v-if="token" class="channel-badge-token">{{ token }}</span>
  </span>
</template>

<style scoped>
.channel-badge { display: inline-flex; align-items: center; gap: 6px; max-width: 100%; vertical-align: middle; }
.channel-badge-token { color: var(--muted); font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
