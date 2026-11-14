<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useEditorContext } from './context'
import { editorForController } from '../core/internal'
import { useTiptapVueContext } from './useTiptapVueContext'

const props = defineProps<{ ariaLabel: string }>()
const controller = useEditorContext()
const host = ref<HTMLElement | null>(null)
let detach: (() => void) | undefined
const vueContext = useTiptapVueContext()
let detachContext: (() => void) | undefined
onMounted(() => {
  // The node-view context before the view is built: its node views are drawn once, not drawn plain and then redrawn (3.6-G open).
  const attachContext = (redraw: boolean) => { const editor = editorForController(controller); if (editor && !detachContext) detachContext = vueContext.attach(editor, redraw) }
  detach = controller.attachView(host.value!, { ariaLabel: props.ariaLabel, beforeMount: () => attachContext(false) })
  attachContext(true)
})
onBeforeUnmount(() => { try { detach?.() } finally { detachContext?.() } })
watch(() => props.ariaLabel, label => {
  if (!label?.trim()) throw new TypeError('A non-empty aria label is required.')
  host.value?.querySelector('[role="textbox"]')?.setAttribute('aria-label', label)
})
</script>

<template><div ref="host" class="editor-body" /></template>
