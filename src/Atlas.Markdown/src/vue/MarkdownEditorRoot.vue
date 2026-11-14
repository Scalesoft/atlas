<script setup lang="ts" generic="P extends MarkdownProfile">
import { provide, watch } from 'vue'
import type { CommandState, MarkdownCommand, MarkdownEditorController, MarkdownEditorState, MarkdownProfile } from '../core'
import { editorContext, useControllerState } from './context'

const props = defineProps<{ controller: MarkdownEditorController<P> }>()
const controller = props.controller
provide(editorContext, controller)
const state = useControllerState(controller)
watch(() => props.controller, next => {
  if (next !== controller) throw new Error('Remount MarkdownEditorRoot with a new key when replacing its controller.')
})
function dispatch(command: MarkdownCommand) {
  const applied = controller.dispatch(command)
  if (applied) controller.focus()
  return applied
}
const query = (command: MarkdownCommand) => controller.query(command)
defineSlots<{
  default?(context: {
    controller: MarkdownEditorController<P>
    state: MarkdownEditorState
    dispatch: (command: MarkdownCommand) => boolean
    query: (command: MarkdownCommand) => CommandState
  }): unknown
}>()
</script>

<template>
  <slot :controller="controller" :state="state" :dispatch="dispatch" :query="query" />
</template>
