import type { NodeViewRenderer } from '@tiptap/core'
import type { ViewMutationRecord } from '@tiptap/pm/view'
import { createIconElement, createMarkdownContextToolbar, writeMarkdownClipboard, openMarkdownContextPopover, markdownOverlaysFor, observeMarkdownProximity, type MarkdownContextToolbar } from '../../core'
import { codeHighlighted, requestCodeHighlighting } from './codeHighlighting'
import { editorToolsStrings } from './runtimeStrings'
import { markdownCodeLanguages, setCodeBlockLanguage } from './code'

export const codeBlockNodeView: NodeViewRenderer = ({ editor, view, node: initialNode, getPos }) => {
  const doc = view.dom.ownerDocument
  // The copy button belongs to the block for everyone, reader and writer, whatever the caret does: it sits in a wrapper, so long lines scroll under it.
  const dom = doc.createElement('div'); dom.className = 'md-code-block'
  const pre = doc.createElement('pre'); pre.className = 'tt-code-block'
  const code = doc.createElement('code'); pre.append(code)
  const copyButton = doc.createElement('button'); copyButton.type = 'button'; copyButton.className = 'md-code-copy'; copyButton.contentEditable = 'false'
  copyButton.append(createIconElement(doc, 'copy', 14)); dom.append(pre, copyButton)
  let node = initialNode; let toolbar: MarkdownContextToolbar | undefined; let closed = false; let closePanel: (() => void) | undefined
  const updateLanguage = () => { code.className = node.attrs.language ? `language-${node.attrs.language}` : '' }
  // Finish focus synchronization before native caret placement. On Firefox the
  // first click in this scrollable code view otherwise restores the old caret.
  // Do not prevent default: native text/range selection must still run.
  const focusCode = (event: MouseEvent) => {
    if (!event.defaultPrevented && event.button === 0 && editor.isEditable && !view.hasFocus()) view.focus()
  }
  pre.addEventListener('mousedown', focusCode)
  // J6.4: Alt+F10 from the code gives the keyboard to the block's toolbar (the language), Tab goes on to the copy button.
  const toToolbar = (event: KeyboardEvent) => { if (event.altKey && event.key === 'F10') { const first = toolbar?.element.querySelector<HTMLElement>('button'); if (first) { event.preventDefault(); first.focus({ preventScroll: true }) } } }
  pre.addEventListener('keydown', toToolbar)
  let copiedTimer: ReturnType<typeof setTimeout> | undefined
  const copy = (button: HTMLElement) => {
    const say = (label: string, done: boolean) => {
      if (closed) return
      button.setAttribute('aria-label', label)
      if (button === copyButton) {
        copyButton.dataset.state = done ? 'copied' : 'failed'; copyButton.title = label
        copyButton.replaceChildren(createIconElement(doc, done ? 'check' : 'copy', 14))
        clearTimeout(copiedTimer); copiedTimer = setTimeout(() => {
          if (closed) return
          delete copyButton.dataset.state; copyButton.title = editorToolsStrings.copyCode; copyButton.setAttribute('aria-label', editorToolsStrings.copyCode)
          copyButton.replaceChildren(createIconElement(doc, 'copy', 14))
        }, 1600)
      }
    }
    void writeMarkdownClipboard(doc, node.textContent).then(done => say(done ? editorToolsStrings.copySuccess : editorToolsStrings.copyFailed, done))
  }
  updateLanguage()
  // Until it nears the viewport the block is its plain code (the same text and height): highlighting and the toolbar come then (plan 3.6-G).
  let hydrated = false
  const proximity = observeMarkdownProximity(dom, () => queueMicrotask(() => {
    if (closed || editor.isDestroyed) return
    hydrated = true
    requestCodeHighlighting(view, getPos)
    let language: HTMLButtonElement | undefined
    // A click into the code is the caret in its text; Enter or F2 on the selected block open its own edit, the language (P1).
    toolbar = createMarkdownContextToolbar({ editor, anchor: dom, getPos, label: editorToolsStrings.codeContext, selectOnClick: false, markAnchor: false, placement: 'inside-top-end',
      onActivate: () => { toolbar?.refresh(); language?.click() },
      isVisible: () => {
        const position = getPos(); const selection = editor.state.selection
        const focused = editor.view.dom.contains(doc.activeElement) || toolbar?.element.contains(doc.activeElement)
          || markdownOverlaysFor(editor.view.dom).some(element => element.contains(doc.activeElement))
        return !!focused && position !== undefined && selection.from >= position && selection.to <= position + node.nodeSize
      },
    })
    language = toolbar.addButton({ label: editorToolsStrings.codeLanguage, icon: 'settings', onClick(anchor) {
      closePanel = openMarkdownContextPopover({ editor, anchor, title: editorToolsStrings.codeLanguage, variant: 'menu', render(container, close) {
        for (const language of markdownCodeLanguages) {
          const button = doc.createElement('button'); button.type = 'button'; button.setAttribute('role', 'menuitemradio')
          button.setAttribute('aria-checked', String(node.attrs.language === language.value || !node.attrs.language && language.value === 'plaintext'))
          button.textContent = language.label
          button.onclick = () => { const position = getPos(); if (position !== undefined) setCodeBlockLanguage(editor, position, language.value); close() }
          container.append(button)
        }
      } })
    } })
  }))
  copyButton.setAttribute('aria-label', editorToolsStrings.copyCode); copyButton.title = editorToolsStrings.copyCode
  // A press must not move the caret or the selection, and a reader has neither.
  copyButton.addEventListener('mousedown', event => event.preventDefault())
  copyButton.addEventListener('click', () => copy(copyButton))
  return { dom, contentDOM: code,
    // Only the code's own text is the editor's; the button's label and icon change on their own.
    ignoreMutation: (mutation: ViewMutationRecord) => mutation.type !== 'selection' && !code.contains(mutation.target),
    update(next, decorations) {
      if (next.type !== initialNode.type) return false
      node = next; updateLanguage()
      // A replaced block (a new language, a collaborator's resync) lost its highlighting with its old node.
      if (hydrated && !codeHighlighted(decorations)) requestCodeHighlighting(view, getPos)
      return true
    },
    destroy() { closed = true; proximity.dispose(); clearTimeout(copiedTimer); pre.removeEventListener('mousedown', focusCode); pre.removeEventListener('keydown', toToolbar); closePanel?.(); toolbar?.destroy() },
  }
}
