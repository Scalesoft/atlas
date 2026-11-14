<script setup lang="ts">
// Atlas Markdown collaboration — stacked coloured initials of the
// remote collaborators currently present, matching their caret colour. Names
// show on hover (v-tooltip). Overflow beyond `max` collapses into a "+N" chip.
// Pure presentational: peers come from useCollab's `peers` ref.
import { computed } from 'vue'
import type { PresenceUser } from '../collab/useCollab'
// Local directive: the package must not depend on a host-registered `v-tooltip`.
import { vTooltip } from './vTooltip'
import { collaborationStrings } from './strings/collaboration'

const props = withDefaults(defineProps<{
  peers: PresenceUser[]
  /** Max avatars before collapsing the rest into a +N chip. */
  max?: number
}>(), {
  max: 4,
})

const shown = computed(() => props.peers.slice(0, props.max))
const overflow = computed(() => Math.max(0, props.peers.length - props.max))
const overflowNames = computed(() => props.peers.slice(props.max).map((p) => p.name).join(', '))
// Screen readers ignore aria-label on generic (role-less) spans, so name the whole group with the
// full roster; each avatar also gets role="img" so its own aria-label is announced when navigated.
const groupLabel = computed(() => collaborationStrings.presentCollaboratorsAriaLabel(props.peers.map((p) => p.name).join(', ')))

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  if (parts.length === 0) return '?'
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase()
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase()
}
</script>

<template>
  <div
    v-if="peers.length"
    class="presence-stack"
    data-testid="presence-stack"
    role="group"
    :aria-label="groupLabel">
    <span
      v-for="peer in shown"
      :key="peer.clientId"
      class="presence-avatar"
      data-testid="presence-avatar"
      role="img"
      :style="{ backgroundColor: peer.color }"
      v-tooltip.top="peer.name"
      :aria-label="peer.name">
      {{ initials(peer.name) }}
    </span>
    <span
      v-if="overflow"
      class="presence-avatar presence-overflow"
      data-testid="presence-overflow"
      role="img"
      v-tooltip.top="overflowNames"
      :aria-label="collaborationStrings.moreCollaboratorsAriaLabel(overflowNames)">
      +{{ overflow }}
    </span>
  </div>
</template>

<style scoped>
.presence-stack {
  display: inline-flex;
  align-items: center;
}
.presence-avatar {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  margin-left: -6px;
  border-radius: 50%;
  border: 2px solid var(--bg-1, var(--bg));
  box-sizing: border-box;
  font-family: var(--font-mono);
  font-size: 9px;
  font-weight: 700;
  letter-spacing: 0.02em;
  color: #fff;
  text-transform: uppercase;
  user-select: none;
  cursor: default;
}
.presence-avatar:first-child { margin-left: 0; }
.presence-overflow {
  background: var(--bg-3);
  color: var(--text-dim);
}
</style>
