<script setup lang="ts">
import type { Editor } from '@tiptap/core'
import { ref, watch } from 'vue'
import type { MarkdownEditorController, MarkdownProfile } from '../core/types'
import BlockHandle from './BlockHandle.vue'
import BlockInserter from './BlockInserter.vue'
import FormattingToolbar from './FormattingToolbar.vue'
import TableToolbar from './TableToolbar.vue'
import TableCellMenu from './TableCellMenu.vue'
import type { MarkdownFormattingItem } from './formatting'

const cellPopover = ref(false)
const tablePopover = ref(false)

withDefaults(defineProps<{
  editor: Editor
  profile: MarkdownProfile
  boundary: HTMLElement | null
  compact: boolean
  formatting?: readonly MarkdownFormattingItem[]
  hideBlockHandle: boolean
  showBlockAddButton?: boolean
  showBlockInserter?: boolean
  /** The selection bubble on <body>, fixed: for an editor inside a zoomed or clipping host (a board's canvas). */
  bubbleOnBody?: boolean
  /** False when the host draws its own bar over a reader's text selection: the reader's bubble stays off (one bar for readers). */
  readerBubble?: boolean
  /** Supplies plugin selection actions to the formatting bubble. */
  controller?: MarkdownEditorController
}>(), { showBlockAddButton: true, showBlockInserter: false, readerBubble: true })
const emit = defineEmits<{ 'formatting-popover': [open: boolean]; 'table-popover': [open: boolean] }>()
watch([cellPopover, tablePopover], ([cell, table]) => emit('table-popover', cell || table))
</script>

<template>
  <!-- Tiptap moves control roots into portals. Vue needs a stable local root when readonly removes them. -->
  <div style="display: contents">
    <div v-if="!compact && !hideBlockHandle" style="display: contents">
      <BlockHandle :editor="editor" :show-add-button="showBlockAddButton" :controller="controller" />
    </div>
    <BlockInserter v-if="!compact && showBlockInserter" :editor="editor" :boundary="boundary" />
    <FormattingToolbar :editor="editor" :profile="profile" :boundary="boundary" :compact="compact" :body-bubble="bubbleOnBody"
      :reader-bubble="readerBubble" :formatting="formatting" :controller="controller"
      @popover-open="$emit('formatting-popover', $event)" />
    <div style="display: contents">
      <TableToolbar :editor="editor" :profile="profile" :boundary="boundary"
        @popover-open="tablePopover = $event" />
    </div>
    <div style="display: contents">
      <TableCellMenu :editor="editor" :profile="profile" @popover-open="cellPopover = $event" />
    </div>
  </div>
</template>
