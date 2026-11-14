<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import type { MarkdownEditorController } from '../core'
import { useControllerState } from '../vue/context'
import { isMarkdownComposition } from '../features/modules/composition'
import { MarkdownIcon } from '../icons'
import { surfaceStrings } from './strings/surface'

const props = defineProps<{ controller: MarkdownEditorController }>()
const state = useControllerState(props.controller)
const open = ref(false)
const trigger = ref<HTMLButtonElement>()
const panelId = useId()
const projection = computed(() => {
  void state.value.contentRevision
  return open.value && state.value.lifecycle === 'ready' ? props.controller.getProjection() : null
})
function close() { open.value = false; trigger.value?.focus() }
function keydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || isMarkdownComposition(event)) return
  event.stopPropagation(); close()
}
</script>

<template>
  <div class="md-source" @keydown="keydown">
    <button ref="trigger" type="button" class="md-source-trigger" :aria-expanded="open" :aria-controls="panelId"
      :disabled="state.lifecycle !== 'ready'" @click="open = !open">
      <MarkdownIcon name="square-code" :size="16" />
      {{ surfaceStrings.markdownLabel }} <MarkdownIcon class="md-source-chevron" name="chevron-down" :size="16" />
    </button>
    <section v-if="open" :id="panelId" class="md-source-panel" :aria-label="surfaceStrings.sourcePanelAriaLabel">
      <p v-if="projection?.markdownLossy" class="md-source-note">{{ surfaceStrings.sourceLossyNote }}</p>
      <textarea readonly spellcheck="false" :aria-label="surfaceStrings.sourceTextareaAriaLabel" :value="projection?.markdown ?? ''" />
    </section>
  </div>
</template>

<style scoped>
.md-source { padding: 8px 16px; border-bottom: 1px solid var(--line); }
.md-source-trigger { display: inline-flex; align-items: center; gap: 7px; padding: 6px 11px; border: 1px solid var(--line); border-radius: 99px; background: var(--paper); color: var(--text-dim); font: inherit; font-size: 12px; cursor: pointer; }
.md-source-trigger svg { width: 16px; height: 16px; }
.md-source-trigger:hover, .md-source-trigger[aria-expanded="true"] { background: var(--accent-soft); border-color: var(--accent-line); color: var(--accent); }
.md-source-trigger:disabled { opacity: .5; cursor: default; }
.md-source-chevron { margin-left: 4px; transform: rotate(-90deg); transition: transform var(--md-motion-fast); }
.md-source-trigger[aria-expanded="true"] .md-source-chevron { transform: none; }
.md-source-panel { margin-top: 10px; overflow: hidden; border: 1px solid var(--line); border-radius: 10px; background: var(--bg-2); }
.md-source-note { margin: 0; padding: 10px 12px 8px; color: var(--text-dim); font-size: 12px; }
.md-source-panel textarea { display: block; box-sizing: border-box; width: 100%; min-height: 180px; max-height: 50vh; padding: 12px; resize: vertical; border: 0; border-radius: 0; background: var(--paper); color: var(--text); font: 12px/1.7 var(--font-mono, ui-monospace, monospace); tab-size: 2; --focus-offset: -2px; }
.md-source-panel textarea:first-child { border-top: 0; }
.md-source-panel .md-source-note + textarea { border-top: 1px solid var(--line); }
</style>
