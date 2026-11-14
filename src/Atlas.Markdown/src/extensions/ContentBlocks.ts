import { Node, type Editor } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { Selection } from '@tiptap/pm/state'
import { createMarkdownContextToolbar, markdownContextIcon, openMarkdownContextPopover, type MarkdownContextToolbar } from '../features/modules/contextToolbar'
import { createIconElement, type MarkdownIconName } from '../ui/iconMarkup'
import { closeMarkdownHistory, dispatchMarkdownHistoryOperation } from '../engine/historyBoundary'
import { blocksStrings } from '../ui/strings/blocks'
import { defineMarkdownAttributeValidator } from '../schema/constraints'
import { observeMarkdownEditability } from '../features/modules/editability'

const variants = ['note', 'tip', 'warning', 'important', 'caution']
/** The GitHub alert type written for each variant: all five GitHub alerts round-trip unchanged. */
const alertTypes: Readonly<Record<string, string>> = { note: 'NOTE', tip: 'TIP', warning: 'WARNING', important: 'IMPORTANT', caution: 'CAUTION' }
interface MarkdownBlockState {
  write(text?: string): void; ensureNewLine(): void; renderContent(node: PMNode): void; closeBlock(node: PMNode): void
  wrapBlock(delim: string, firstDelim: string | null, node: PMNode, render: () => void): void
}
const variantLabel = (index: number) => ([
  blocksStrings.calloutNote, blocksStrings.calloutTip, blocksStrings.calloutWarning, blocksStrings.calloutImportant, blocksStrings.calloutCaution,
])[index]
// Colour alone carries the variant for nobody who cannot see it; the glyph says which kind this is.
const variantIcons: readonly MarkdownIconName[] = ['info', 'lightbulb', 'triangle-alert', 'message-square-warning', 'octagon-alert']
/** The icon and name of a variant, the same in the editor's header and in read mode. */
function fillCalloutLabel(header: HTMLElement, variant: unknown): void {
  const index = Math.max(0, variants.indexOf(String(variant)))
  const text = header.ownerDocument.createElement('span'); text.textContent = variantLabel(index)!
  header.replaceChildren(createIconElement(header.ownerDocument, variantIcons[index] ?? 'info', 16), text)
}
/**
 * Read mode (the static renderer) shows the same header as the editor. It is added to the rendered DOM only,
 * never to `renderHTML`, so copied HTML, `getHTML` and parsing keep the plain callout.
 */
export function decorateStaticCallouts(root: ParentNode): void {
  root.querySelectorAll<HTMLElement>('aside[data-callout]').forEach(callout => {
    if (callout.querySelector(':scope > [data-callout-label]')) return
    const header = callout.ownerDocument.createElement('div')
    header.className = 'md-callout-label'; header.dataset.calloutLabel = ''
    fillCalloutLabel(header, callout.dataset.variant)
    callout.prepend(header)
  })
}
/** Selects the block, so the keyboard returns to it after its edit; false when it is gone. */
function selectBlock(editor: Editor, getPos: () => number | undefined): boolean {
  const position = getPos()
  if (position === undefined) return false
  editor.commands.setNodeSelection(position); editor.view.focus()
  return true
}
/**
 * A container block's own edit where it is shown (P1): `open` draws it at an anchor (the block's label, its toolbar button) and
 * returns its close; the same gesture on the same anchor closes it again (an open popover marks its anchor `aria-expanded`).
 */
function inPlaceEdit(open: (anchor: HTMLElement) => () => void) {
  let close: (() => void) | undefined
  return {
    toggle(anchor: HTMLElement) { const again = anchor.getAttribute('aria-expanded') === 'true'; close?.(); close = again ? undefined : open(anchor) },
    close() { close?.(); close = undefined },
  }
}
/** Trailing menu affordance shared with the table toolbars. */
function chevron(doc: Document): SVGSVGElement {
  const icon = markdownContextIcon(doc, 'chevron-down')
  icon.classList.add('bb-trigger-chevron')
  return icon
}

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    callout: {
      /** Changes the variant of the callout around the selection: one undo step, the content kept. False outside a callout or for an unknown variant. */
      setCalloutVariant: (variant: string) => ReturnType
    }
  }
}

export const Callout = Node.create({
  name: 'callout', group: 'block', content: 'block+', defining: true,
  addAttributes: () => ({ variant: { default: 'note', validate: defineMarkdownAttributeValidator({ kind: 'enum', values: variants }, value => { if (!variants.includes(String(value))) throw new TypeError('Invalid callout variant.') }),
    parseHTML: element => variants.includes(element.dataset.variant ?? '') ? element.dataset.variant : 'note' } }),
  parseHTML: () => [{ tag: 'aside[data-callout]' }],
  addCommands: () => ({
    setCalloutVariant: variant => ({ tr, dispatch }) => {
      if (!variants.includes(variant)) return false
      const { $from } = tr.selection
      for (let depth = $from.depth; depth > 0; depth--) if ($from.node(depth).type.name === 'callout') {
        if (dispatch) closeMarkdownHistory(tr.setNodeAttribute($from.before(depth), 'variant', variant))
        return true
      }
      return false
    },
  }),
  renderHTML: ({ node }) => ['aside', { 'data-callout': '', 'data-variant': node.attrs.variant, class: 'md-callout' }, 0],
  addKeyboardShortcuts() {
    // J4.3: Backspace at the start of the first paragraph lifts it out. joinBackward would wrap the callout into a quote or list before it.
    return { Backspace: ({ editor }) => {
      const { selection } = editor.state, { $from } = selection
      // An empty paragraph right after a callout goes, the caret to the end of the callout's text; joinBackward pulled it in.
      if (selection.empty && $from.depth && $from.parent.type.name === 'paragraph' && !$from.parent.content.size
        && editor.state.doc.resolve($from.before()).nodeBefore?.type.name === this.name) {
        const tr = editor.state.tr.delete($from.before(), $from.after())
        editor.view.dispatch(tr.setSelection(Selection.near(tr.doc.resolve($from.before() - 1), -1)).scrollIntoView())
        return true
      }
      if (!selection.empty || $from.parentOffset || $from.depth < 2 || $from.node(-1).type.name !== this.name || $from.index(-1)) return false
      return editor.commands.lift(this.name)
    } }
  },
  addStorage: () => ({ markdown: { serialize(state: MarkdownBlockState, node: PMNode) {
    // A GitHub alert, parsed back by the editor dialect (engine/markdownImportSyntax.ts).
    state.wrapBlock('> ', null, node, () => {
      state.write(`[!${alertTypes[node.attrs.variant] ?? 'NOTE'}]`)
      const first = node.firstChild
      // A first paragraph continues the marker line, as GitHub writes it. Any other block (a list starting
      // at 2, a fence) and a paragraph that would read as a setext underline follow an empty quote line.
      if (first?.type.name === 'paragraph' && !/^\s*=+\s*$/.test(first.textContent)) {
        state.ensureNewLine()
        // Write the quote prefix now: otherwise a leading mark (bold, highlight…) records its position before
        // the prefix, and delimiter trimming then eats the "> ".
        state.write()
      } else state.closeBlock(node)
      state.renderContent(node)
    })
  } } }),
  addNodeView: () => ({ node: initial, editor, getPos, view }) => {
    let node = initial
    const doc = view.dom.ownerDocument
    const dom = doc.createElement('aside'); dom.className = 'md-callout'; dom.dataset.callout = ''
    const header = doc.createElement('div'); header.className = 'md-callout-label'; header.contentEditable = 'false'
    // The label opens the type menu, a writer's control: no Tab stop for a reader (J6.1, §7).
    const tabStop = () => { header.tabIndex = editor.isEditable ? 0 : -1 }
    tabStop()
    const stopEditable = observeMarkdownEditability(editor, view.dom, tabStop)
    const contentDOM = doc.createElement('div'); dom.append(header, contentDOM)
    const abort = new AbortController(); let toolbar: MarkdownContextToolbar | undefined, typeButton: HTMLButtonElement | undefined
    // The box's own edit is its type: a click on its label, Enter or F2 open the menu there (the text inside is written as anywhere).
    // The toolbar's button opens the same menu at the label, and a press on it closes the menu instead of reopening it.
    const edit = inPlaceEdit(anchor => openMarkdownContextPopover({ editor, anchor, title: blocksStrings.calloutTypeLabel, variant: 'menu', signal: abort.signal, interactionRoots: typeButton && [typeButton], render(container, dismiss) {
      const choices = doc.createElement('div'); choices.className = 'md-context-choices'
      variants.forEach((variant, index) => {
        const choice = doc.createElement('button'); choice.type = 'button'; choice.dataset.variant = variant
        choice.setAttribute('role', 'menuitemradio'); choice.setAttribute('aria-checked', String(node.attrs.variant === variant))
        const swatch = doc.createElement('span'); swatch.className = 'md-context-choice-swatch'; swatch.setAttribute('aria-hidden', 'true')
        const label = doc.createElement('span'); label.textContent = variantLabel(index)!
        choice.append(createIconElement(doc, variantIcons[index] ?? 'info', 16), swatch, label)
        // An attribute step preserves the selected container and its toolbar.
        choice.onclick = () => { const position = getPos(); if (position !== undefined && editor.isEditable) dispatchMarkdownHistoryOperation(editor, editor.state.tr.setNodeAttribute(position, 'variant', variant)); dismiss() }
        choices.append(choice)
      }); container.append(choices)
    } }))
    header.onkeydown = event => { if (['Enter', ' ', 'F2'].includes(event.key) && editor.isEditable) { event.preventDefault(); if (selectBlock(editor, getPos)) edit.toggle(header) } }
    queueMicrotask(() => {
      if (abort.signal.aborted || editor.isDestroyed) return
      toolbar = createMarkdownContextToolbar({ editor, anchor: header, getPos, label: blocksStrings.calloutToolbarLabel, signal: abort.signal, onActivate: () => edit.toggle(header) })
      typeButton = toolbar.addButton({ label: blocksStrings.calloutTypeButtonText, icon: 'sliders-horizontal', text: blocksStrings.calloutTypeButtonText, onClick: () => edit.toggle(header) })
      // The same affordance the table menus use: a chevron says the button opens a menu.
      typeButton.append(chevron(doc))
      toolbar.addSeparator(); toolbar.addButton({ label: blocksStrings.calloutRemove, icon: 'trash', onClick() { if (editor.isEditable) dispatchMarkdownHistoryOperation(editor, editor.state.tr.deleteSelection()) } })
    })
    const render = () => {
      dom.dataset.variant = node.attrs.variant
      fillCalloutLabel(header, node.attrs.variant)
    }
    render()
    return { dom, contentDOM, stopEvent: event => event.type !== 'contextmenu' && header.contains(event.target as globalThis.Node),
      ignoreMutation: mutation => mutation.type !== 'selection' && !contentDOM.contains(mutation.target),
      update(next) { if (next.type !== node.type) return false; node = next; render(); return true },
      destroy() { abort.abort(); stopEditable(); edit.close(); toolbar?.destroy() },
    }
  },
})
