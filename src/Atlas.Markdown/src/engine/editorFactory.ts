import { Editor, type EditorOptions } from '@tiptap/core'

/**
 * ProseMirror puts `.ProseMirror-hideselection` on the editor's root while a node, block range, cell or gap selection hides the native one.
 * Tiptap's base rules for it (`.ProseMirror-hideselection *`, `… *::selection`) restyled every element of the page at each toggle
 * (~16 000 on a big page, 0.45 s at 4× CPU); so did an inherited caret colour on the root. The native selection then lies only in the
 * selected blocks or table, so only they hide it; a caret shows only at a gap cursor, whose own class hides it (core/kit.ts).
 */
const scopedHideSelection = `
.ProseMirror.md-gap-cursor { caret-color: transparent; }
:is(.ProseMirror-selectednode, .ProseMirror-selectednoderange, .tableWrapper:has(.selectedCell))::selection,
:is(.ProseMirror-selectednode, .ProseMirror-selectednoderange, .tableWrapper:has(.selectedCell)) *::selection { background: transparent; }
`

/** Rewrites Tiptap's one shared style tag (it reuses the tag for every later editor of the document) once it exists. */
export function scopeHideSelection(doc: Document): void {
  const tag = doc.querySelector<HTMLStyleElement>('style[data-tiptap-style]')
  if (!tag || tag.hasAttribute('data-atlas-hideselection')) return
  tag.textContent = (tag.textContent ?? '').replace(/\.ProseMirror-hideselection \*[^{]*\{[^}]*\}\s*/g, '') + scopedHideSelection
  tag.setAttribute('data-atlas-hideselection', '')
}

/** The single production construction boundary for Tiptap runtimes. */
export function createTiptapEditor(options: Partial<EditorOptions>): Editor {
  const editor = new Editor(options)
  // Tiptap injects its style when the view is created: in the constructor with an element, else at mount.
  if (typeof document !== 'undefined') scopeHideSelection(document)
  editor.on('mount', () => scopeHideSelection(editor.view.dom.ownerDocument))
  return editor
}
