<script setup lang="ts">
import type { CommandState } from '../core'
import type { MarkdownAction } from '../core/plugins/types'
import { MarkdownIcon } from '../icons'
import { legacyIconName, type MarkdownIconName } from './iconMarkup'

defineProps<{ action: MarkdownAction & { readonly state: CommandState } }>()
defineEmits<{ activate: [] }>()
/** `icon: 'svg:paperclip'` names a registry entry; the old component names still resolve. */
function svgIconFor(icon: string | undefined): MarkdownIconName | null {
  return icon?.startsWith('svg:') ? legacyIconName(icon.slice(4)) : null
}
</script>

<template>
  <button type="button" class="md-plugin-action" :disabled="!action.state.enabled"
    :aria-pressed="action.state.active" :title="action.description" :data-command="action.id"
    :data-group="action.group" @mousedown.prevent @click="$emit('activate')">
    <span v-if="action.icon" class="md-plugin-action-icon" aria-hidden="true">
      <MarkdownIcon v-if="svgIconFor(action.icon)" :name="svgIconFor(action.icon)!" :size="16" />
      <i v-else-if="action.icon.startsWith('pi ')" :class="action.icon" />
      <span v-else>{{ action.icon }}</span>
    </span>
    <span>{{ action.label }}</span>
  </button>
</template>

<style scoped>
.md-plugin-action { display: inline-flex; align-items: center; gap: 6px; font: inherit; font-size: 13px; color: var(--text); background: var(--paper, var(--bg-2)); border: 1px solid var(--line); border-radius: var(--radius-sm, 6px); padding: 0 10px; height: var(--control-h-sm, 32px); cursor: pointer; }
.md-plugin-action-icon, .md-plugin-action-icon :deep(svg) { display: inline-grid; place-items: center; width: var(--icon-sm, 16px); height: var(--icon-sm, 16px); }
.md-plugin-action:hover { background: var(--accent-soft); border-color: var(--accent-line); }
.md-plugin-action:disabled { opacity: .5; cursor: not-allowed; }
</style>
