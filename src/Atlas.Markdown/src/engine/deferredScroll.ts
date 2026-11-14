import { Extension } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'

/**
 * ProseMirror scrolls the caret into view inside the transaction, right after the DOM changed: on every keystroke that forced a
 * layout and then measured each scroll parent (~20 reads, J12.5), and again for a collaborator's change that keeps the caret in
 * view. Here the transactions of a frame share one scroll in the next animation frame, after this task's other writes and before
 * paint, so the caret never shows outside the view; ProseMirror still scrolls only when the caret is near an edge.
 */
export const DeferredScrollToSelection = Extension.create({
  name: 'markdownDeferredScroll',
  addProseMirrorPlugins() {
    let frame = 0, scrolling = false
    return [new Plugin({
      key: new PluginKey('markdownDeferredScroll'),
      props: {
        handleScrollToSelection(view) {
          if (scrolling || typeof requestAnimationFrame !== 'function') return false
          frame ||= requestAnimationFrame(() => {
            frame = 0
            if (view.isDestroyed) return
            scrolling = true
            // shortcut: scrollToSelection is ProseMirror's own (internal) scroll, the only way to keep its scroll margins and parents.
            try { (view as EditorView & { scrollToSelection(): void }).scrollToSelection() } finally { scrolling = false }
          })
          return true
        },
      },
      view: () => ({ destroy() { if (frame) cancelAnimationFrame(frame); frame = 0 } }),
    })]
  },
})
