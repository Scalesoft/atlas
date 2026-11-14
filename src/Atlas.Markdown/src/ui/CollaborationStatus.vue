<script setup lang="ts">
// Atlas Markdown collaboration — compact connection-status badge for a
// collaborative editor. CZ labels; flush-degraded and document-full take
// precedence over the plain connection state because they are the states a user
// must act on. Pure presentational: state comes from useCollab refs.
import { computed } from 'vue'
import { collaborationStrings } from './strings/collaboration'
import type { CollabStatus } from '../collab/useCollab'

const props = withDefaults(defineProps<{
  status: CollabStatus
  flushDegraded?: boolean
  documentFull?: boolean
}>(), {
  flushDegraded: false,
  documentFull: false,
})

type Tone = 'ok' | 'busy' | 'idle' | 'warn' | 'error'
interface Badge { tone: Tone; icon: string; label: string; spin?: boolean; state: string }

const badge = computed<Badge>(() => {
  const strings = collaborationStrings
  if (props.documentFull) {
    return { tone: 'error', icon: 'pi-exclamation-triangle', label: strings.documentFullLabel, state: 'document-full' }
  }
  if (props.flushDegraded) {
    return { tone: 'warn', icon: 'pi-exclamation-triangle', label: strings.flushDegradedLabel, state: 'flush-degraded' }
  }
  switch (props.status) {
    case 'connected':   return { tone: 'ok',   icon: 'pi-bolt',    label: strings.liveLabel,                  state: 'connected' }
    case 'connecting':  return { tone: 'busy', icon: 'pi-spinner', label: strings.connectingEllipsisLabel,    spin: true, state: 'connecting' }
    case 'reconnecting':return { tone: 'busy', icon: 'pi-spinner', label: strings.reconnectingEllipsisLabel,  spin: true, state: 'reconnecting' }
    case 'offline':     return { tone: 'idle', icon: 'pi-cloud',   label: strings.offlineShortLabel,          state: 'offline' }
    default:            return { tone: 'idle', icon: 'pi-user',    label: strings.localLabel,                 state: 'local' }
  }
})
</script>

<template>
  <span
    class="collab-status"
    :class="`tone-${badge.tone}`"
    :data-state="badge.state"
    data-testid="collaboration-status"
    role="status">
    <i class="pi collab-ic" :class="[badge.icon, { 'pi-spin': badge.spin }]" aria-hidden="true"/>
    <span class="collab-label">{{ badge.label }}</span>
  </span>
</template>

<style scoped>
.collab-status {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-family: var(--font-mono);
  font-size: 10px;
  letter-spacing: 0.04em;
  white-space: nowrap;
  color: var(--text-dimmer);
  transition: color var(--md-motion-fast);
}
.collab-ic { font-size: 10px; }
.tone-ok { color: var(--chip-green-fg); }
.tone-busy { color: var(--chip-amber-fg); }
.tone-warn { color: var(--chip-amber-fg); }
.tone-error { color: var(--chip-red-fg); }
.tone-idle { color: var(--text-dimmer); }
</style>
