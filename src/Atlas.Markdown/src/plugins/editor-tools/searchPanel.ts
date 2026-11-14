import { createApp } from 'vue'
import type { Editor } from '@tiptap/core'
import SearchPanel from './SearchPanel.vue'

/** The find panel's body (the popover frame is opened by openEditorSearch, which shows a stub until this chunk is here). */
export const searchRenderer = (editor: Editor) => (container: HTMLElement, close: () => void) => {
  const app = createApp(SearchPanel, { editor, close }); app.mount(container)
  return () => { app.unmount(); if (!editor.isDestroyed) editor.commands.clearSearch() }
}
