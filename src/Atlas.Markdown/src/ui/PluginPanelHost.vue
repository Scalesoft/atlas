<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import type { MarkdownEditorController } from '../core'
import type { MarkdownPluginPanel } from '../core/plugins/types'
import { surfaceStrings } from './strings/surface'

const props = defineProps<{ controller: MarkdownEditorController; panel: MarkdownPluginPanel }>()
const host = ref<HTMLElement | null>(null)
const failed = ref(false)
const lifetime = new AbortController()
let cleanup: void | (() => void)
let unsubscribe: (() => void) | undefined
let disposed = false

function dispose(): void {
  if (disposed) return
  disposed = true
  unsubscribe?.()
  unsubscribe = undefined
  lifetime.abort()
  try { cleanup?.() } catch (error) { console.error(`Plugin panel ${props.panel.id} failed to clean up.`, error) }
  cleanup = undefined
}

onMounted(() => {
  if (!host.value) return
  let result: unknown
  try {
    result = props.panel.mount({ container: host.value, controller: props.controller, signal: lifetime.signal })
    if (result !== undefined && typeof result !== 'function') {
      // Async lifecycle returns are unsupported. Observe a hostile/rejected thenable so an invalid
      // third-party panel cannot also create an unhandled rejection after the synchronous error.
      if (typeof (result as { then?: unknown })?.then === 'function') {
        void Promise.resolve(result).catch(error => console.error(`Plugin panel ${props.panel.id} rejected after invalid mount.`, error))
      }
      throw new TypeError('Plugin panel cleanup must be a function.')
    }
    cleanup = result as void | (() => void)
  } catch (error) {
    cleanup = undefined
    lifetime.abort()
    failed.value = true
    console.error(`Plugin panel ${props.panel.id} failed to mount.`, error)
    return
  }
  if (props.controller.state.lifecycle === 'destroyed') dispose()
  else unsubscribe = props.controller.subscribe(state => { if (state.lifecycle === 'destroyed') dispose() })
})

onBeforeUnmount(dispose)
</script>

<template>
  <section ref="host" class="md-plugin-panel" :data-plugin-panel="panel.id" :aria-label="panel.label">
    <span v-if="failed" role="status">{{ surfaceStrings.pluginPanelFailed }}</span>
  </section>
</template>
