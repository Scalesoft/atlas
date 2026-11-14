<script setup lang="ts">
import { computed } from 'vue'
import type { MarkdownEditorController } from '../core'
import type { MarkdownPanelPlacement } from '../core/plugins/types'
import PluginPanelHost from './PluginPanelHost.vue'

const props = defineProps<{
  controller: MarkdownEditorController
  placement: MarkdownPanelPlacement
  variant: 'document' | 'compact'
}>()
const panels = computed(() => props.controller.panels[props.placement]
  .filter(panel => !panel.variants || panel.variants.includes(props.variant)))
</script>

<template>
  <div v-if="panels.length" class="md-plugin-panels" :data-placement="placement">
    <PluginPanelHost v-for="panel in panels" :key="panel.id" :controller="controller" :panel="panel" />
  </div>
</template>

<style scoped>
.md-plugin-panels { display: grid; gap: 8px; }
.md-plugin-panels[data-placement="inspector"] { padding: 8px 12px; border-top: 1px solid var(--line); }
.md-plugin-panels[data-placement="status"] { padding: 4px 12px; color: var(--text-dim); font-size: 12px; }
</style>
