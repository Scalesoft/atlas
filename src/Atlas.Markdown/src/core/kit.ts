import { Extension, PasteRule, type AnyExtension, type Attributes, type Editor, type KeyboardShortcutCommand } from '@tiptap/core'
import CharacterCount from '@tiptap/extension-character-count'
import Highlight from '@tiptap/extension-highlight'
import Image from '@tiptap/extension-image'
import { RichImage } from '../extensions/RichImage'
import { Callout } from '../extensions/ContentBlocks'
import Link from '@tiptap/extension-link'
import Placeholder from '@tiptap/extension-placeholder'
import type { Mark as ProseMirrorMark, Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Plugin, TextSelection, type Transaction } from '@tiptap/pm/state'
import { GapCursor } from '@tiptap/pm/gapcursor'
import TableCell from '@tiptap/extension-table-cell'
import TableHeader from '@tiptap/extension-table-header'
import TableRow from '@tiptap/extension-table-row'
import TaskItem from '@tiptap/extension-task-item'
import TaskList from '@tiptap/extension-task-list'
import TextAlign from '@tiptap/extension-text-align'
import Typography from '@tiptap/extension-typography'
import Underline from '@tiptap/extension-underline'
import StarterKit from '@tiptap/starter-kit'
import { Markdown } from 'tiptap-markdown'
import { CellBackground } from '../extensions/CellBackground'
import { CellTextAlign } from '../extensions/CellTextAlign'
import { ImageUpload } from '../extensions/ImageUpload'
import { RowHeight } from '../extensions/RowHeight'
import { LinkToolbar } from '../extensions/LinkToolbar'
import { cellAlignment, isRemoteChange, MarkdownTable, tableRepairRequest } from './tableMarkdown'
import type { ImageUploadService } from '../features/images/uploadService'
import { createResourcePolicyExtension, imageSourceAttribute, normalizeResourcePolicy, type MarkdownResourcePolicy } from '../features/images/resourcePolicy'
import { resolveEditorPlaceholder } from '../features/placeholder/configuration'
import { surfaceStrings } from '../ui/strings/surface'
import { MarkdownInput } from '../engine/markdownInput'
import { flushDomSelection } from './internal'
import { MarkdownParagraph } from '../extensions/MarkdownParagraph'
import { ChipText } from '../features/chips/chipText'
import { TimedItem, TimedList } from '../features/timed/timedList'
import { Intentions } from '../features/intentions/intentions'
import { MarkdownRecognition } from './recognition/runtime'
import type { MarkdownLexiconSource } from './recognition/lexicon'
import { SelectedElementEscape } from '../features/modules/selectionEscape'
import { WordHistoryBoundary } from '../engine/historyBoundary'
import { HEADING_LEVELS, LINK_REL } from './documentConstraints'
import type { MarkdownEmbedResolver, MarkdownImportHandler, MarkdownWikiLinkResolver } from './types'

export type MarkdownProfile = 'markdown-safe' | 'rich'

export interface MarkdownContentKitOptions {
  resourcePolicy?: MarkdownResourcePolicy
  profile?: MarkdownProfile
  collaborative?: boolean
  placeholder?: string
  imageUploadUrl?: string
  imageUpload?: ImageUploadService
  extraExtensions?: AnyExtension[]
  /** Optional compatible codeBlock implementation, loaded before editor construction. */
  codeBlock?: AnyExtension
  onMarkdownImport?: MarkdownImportHandler
  resolveWikiLink?: MarkdownWikiLinkResolver
  resolveEmbed?: MarkdownEmbedResolver
  lexicon?: MarkdownLexiconSource
}

/**
 * Text as tiptap-markdown writes it, except inside an address written `<url>`: Markdown reads no backslash escapes
 * in an autolink, so an escaped `*` or `~` came back as part of the address. EditorMarkdownWriter writes the same.
 */
const autolinkText = (extension: AnyExtension) => extension.extend({
  addStorage() {
    return { ...this.parent?.(), markdown: { parse: {}, serialize(state: { inAutolink?: boolean; text(text: string, escape?: boolean): void }, node: ProseMirrorNode) {
      if (state.inAutolink) state.text(node.text!, false)
      else state.text(node.text!.replace(/</g, '&lt;').replace(/>/g, '&gt;'))
    } } }
  },
})

export interface MarkdownKit {
  extensions: AnyExtension[]
  capabilities: {
    mergedCells: boolean
    cellBackground: boolean
    rowHeight: boolean
  }
}

const MarkdownSafeTableCommands = Extension.create({
  name: 'markdownSafeTableCommands',
  addCommands() {
    return {
      mergeCells: () => () => false,
      splitCell: () => () => false,
      mergeOrSplit: () => () => false,
      toggleHeaderCell: () => () => false,
      toggleHeaderRow: () => () => false,
      toggleHeaderColumn: () => () => false,
    }
  },
  addProseMirrorPlugins() {
    return [new Plugin({
      appendTransaction(transactions, oldState, state) {
        const requested = tableRepairRequest(transactions)
        // A remote change is normalized by one collaborator on request, as in MarkdownTable.
        if (!requested && (!transactions.some(transaction => transaction.docChanged) || transactions.some(isRemoteChange))) return null
        // Unchanged tables were normalized when they last changed; normalizing them on any edit would repeat a remote change's repair on every client.
        const unchanged = new Set<ProseMirrorNode>()
        if (!requested) oldState.doc.descendants(node => { if (node.type.name === 'table') unchanged.add(node) })
        const tr = state.tr
        state.doc.descendants((table, tablePos) => {
          if (table.type.name !== 'table') return
          if (requested ? !requested.includes(tablePos) : unchanged.has(table)) return false
          const alignments: (string | null)[] = []
          table.forEach(row => row.forEach((cell, _offset, column) => {
            alignments[column] ||= cellAlignment(cell)
          }))
          table.forEach((row, rowOffset, rowIndex) => row.forEach((cell, cellOffset, column) => {
            const type = state.schema.nodes[rowIndex === 0 ? 'tableHeader' : 'tableCell']
            const alignment = alignments[column] ?? null
            if (cell.type !== type || cell.attrs.textAlign !== alignment || cell.attrs.align !== alignment) {
              tr.setNodeMarkup(tablePos + rowOffset + cellOffset + 2, type, { ...cell.attrs, textAlign: alignment, align: alignment })
            }
          }))
        })
        return tr.docChanged ? (requested ? tr.setMeta('addToHistory', false) : tr) : null
      },
    })]
  },
})

type MarkdownImageNode = { isBlock: boolean; attrs: { src: string; alt?: string | null; title?: string | null } }
type MarkdownImageState = { write(text: string): void; esc(text: string): string; closeBlock(node: unknown): void }
/** prosemirror-markdown's image output, closed as its own block when the image is a block node. */
function serializeImage(state: MarkdownImageState, node: MarkdownImageNode): void {
  const title = node.attrs.title ? ` "${node.attrs.title.replace(/"/g, '\\"')}"` : ''
  state.write(`![${state.esc(node.attrs.alt || '')}](${node.attrs.src.replace(/[()]/g, '\\$&')}${title})`)
  if (node.isBlock) state.closeBlock(node)
}

type MarkdownCodeBlockState = { write(text: string): void; text(text: string, escape?: boolean): void; ensureNewLine(): void; closeBlock(node: unknown): void }
/** tiptap-markdown's code block output; a language in the reserved `a-` namespace is written `a-code <language>` (dialect §2.5). */
function serializeCodeBlock(state: MarkdownCodeBlockState, node: ProseMirrorNode): void {
  const language = String(node.attrs.language || '')
  state.write(`\`\`\`${language.startsWith('a-') ? `a-code ${language}` : language}\n`)
  state.text(node.textContent, false)
  state.ensureNewLine()
  state.write('```')
  state.closeBlock(node)
}
/**
 * Inline code combines with the other marks (rich revision 4), so a mark's paste rules (`**x**`, `==x==`, a bare URL) must skip
 * code text themselves: Tiptap's paste rules skip code only through mark exclusion. Typing is safe: input rules skip code marks.
 */
const outsideInlineCode = <T extends AnyExtension>(mark: T): T => mark.extend({
  addPasteRules() {
    return ((this.parent?.() ?? []) as PasteRule[]).map(rule => new PasteRule({
      find: rule.find,
      handler: props => {
        const code = props.state.schema.marks.code
        return code && props.state.doc.rangeHasMark(props.range.from, props.range.to, code) ? null : rule.handler(props)
      },
    }))
  },
}) as T
const dialectCodeBlock = (codeBlock: AnyExtension): AnyExtension => codeBlock.extend({
  addStorage() { return { ...this.parent?.(), markdown: { ...(this.parent?.() as { markdown?: object } | undefined)?.markdown, serialize: serializeCodeBlock } } },
})

/** Backspace at the start of a heading makes it text first (J4.3, as Notion); the next Backspace joins as before. */
type HeadingWriter = (state: unknown, node: ProseMirrorNode, parent: ProseMirrorNode, index: number) => void
const headingToText = (heading: AnyExtension): AnyExtension => heading.extend({
  // A host's extension may write the heading line itself: its storage's `markdown.heading` (Atlas: a requirement's `{#id state=…}`
  // block, as its server writes it). Without one the line is prosemirror-markdown's.
  addStorage() {
    return { ...this.parent?.(), markdown: { parse: {}, serialize(this: { editor: Editor }, state: { write(text: string): void; renderInline(node: ProseMirrorNode, fromBlockStart?: boolean): void; closeBlock(node: ProseMirrorNode): void },
      node: ProseMirrorNode, parent: ProseMirrorNode, index: number) {
      const storages = Object.values(this.editor.storage as unknown as Record<string, { markdown?: { heading?: HeadingWriter } } | undefined>)
      const own = storages.find(storage => typeof storage?.markdown?.heading === 'function')?.markdown!.heading
      if (own) { own(state, node, parent, index); return }
      state.write(`${'#'.repeat(node.attrs.level as number)} `)
      state.renderInline(node, false)
      state.closeBlock(node)
    } } }
  },
  addKeyboardShortcuts() {
    return { ...this.parent?.(), Backspace: () => {
      const { selection } = this.editor.state
      if (!selection.empty || selection.$from.parent.type.name !== this.name || selection.$from.parentOffset) return false
      // „## “ just turned into a heading: Backspace gives the characters back, as everywhere else.
      return this.editor.commands.undoInputRule() || this.editor.commands.setParagraph()
    } }
  },
})

/**
 * tiptap's list Backspace runs undoInputRule (a fresh map of every command) for each list type on every Backspace, ~3 ms a key on a
 * long page (J12.5). Inside the text it can do nothing else, and the core keymap's chain starts with the same undoInputRule.
 */
/**
 * A gap cursor hides the native caret by a class on the root of its own (`md-gap-cursor`, engine/editorFactory.ts): ProseMirror's
 * `.ProseMirror-hideselection` also marks every block selection, and an inherited caret colour toggled there restyled the whole page.
 */
const caretHiddenAtGap = (gapcursor: AnyExtension): AnyExtension => gapcursor.extend({
  addProseMirrorPlugins() {
    return [...(this.parent?.() ?? []), new Plugin({ props: { attributes: (state): Record<string, string> => state.selection instanceof GapCursor ? { class: 'md-gap-cursor' } : {} } })]
  },
})

const quickListBackspace = (listKeymap: AnyExtension): AnyExtension => listKeymap.extend({
  addKeyboardShortcuts() {
    const shortcuts = (this.parent?.() ?? {}) as Record<string, KeyboardShortcutCommand>
    const backspace = shortcuts.Backspace
    return { ...shortcuts, ...backspace ? { Backspace: props => {
      const { selection } = props.editor.state
      return !(selection.empty && selection.$from.parentOffset > 0) && backspace(props)
    } } : {} }
  },
})

/**
 * Several characters typed as one input (text replacement, dictation, an automation, a burst the browser joins) go in as typed: all but
 * the last at once, the last through the input rules. tiptap's rules match the typed text together with the text before it and, given
 * several characters, dropped those before their match: `/tabulka 2x2` became `2×2` (J5.5).
 */
const TypedTextInput = Extension.create({
  name: 'markdownTypedTextInput',
  addProseMirrorPlugins() {
    const editor = this.editor
    return [new Plugin({ props: { handleDOMEvents: { beforeinput(view, event) {
    const characters = event.inputType === 'insertText' && !event.isComposing && !view.composing && view.editable ? [...event.data ?? ''] : []
    if (characters.length < 2) return false
    event.preventDefault()
    // A click just before moved the DOM caret, its selectionchange may not have run yet: the text went to the old caret (campaign 2026-11-05).
    flushDomSelection(editor)
    const { from, to } = view.state.selection, last = characters.pop()!
    view.dispatch(view.state.tr.insertText(characters.join(''), from, to))
    const at = view.state.selection.from
    if (!view.someProp('handleTextInput', handle => handle(view, at, at, last, () => view.state.tr.insertText(last, at, at)))) view.dispatch(view.state.tr.insertText(last, at, at))
    return true
  } } } })]
  },
})

/**
 * Every key acts on the caret the writer sees. The browser queues `selectionchange` behind input events, so a key pressed while the page
 * is busy right after a click or End (the caret entering the text starts editing) ran its command on the old selection: Enter split the
 * first block (review 2026-11-06). ProseMirror reads the DOM selection only on that event; read it before the keymaps, first of all plugins.
 */
const DomCaretFirst = Extension.create({
  name: 'markdownDomCaretFirst',
  priority: 100_000,
  addProseMirrorPlugins() {
    const editor = this.editor
    return [new Plugin({ props: { handleKeyDown() { flushDomSelection(editor); return false } } })]
  },
})

/**
 * A reader's task checkbox is inert (TaskItem turns its change back) and says so, as a requirement's does (J6.1, J7.1). TaskItem's node
 * view never hears of `editable`: the boxes are marked when it flips and, while reading, when a remote change draws new ones.
 */
const ReaderTaskItem = TaskItem.extend({
  addProseMirrorPlugins() {
    return [...this.parent?.() ?? [], new Plugin({ view(view) {
      let editable: boolean | undefined
      const update = (next: typeof view, previous?: typeof view.state) => {
        if (next.editable === editable && (editable || next.state.doc === previous?.doc)) return
        editable = next.editable
        next.dom.querySelectorAll('li.tt-task-item > label > input').forEach(input => editable ? input.removeAttribute('aria-disabled') : input.setAttribute('aria-disabled', 'true'))
      }
      update(view)
      return { update }
    } })]
  },
})

/** A text selection whose ends are in different text blocks. */
const acrossBlocks = (selection: unknown): selection is TextSelection =>
  selection instanceof TextSelection && !selection.empty && !selection.$from.sameParent(selection.$to)

/**
 * Input over a selection across text blocks (G1 and fifth review). Enter: the range goes first, then Enter on the caret runs through the
 * whole keymap (a heading's or a host's split rule, a list item's, a code block's newline); tiptap's splitBlock threw on such a range and
 * did nothing. Text an IME, a dead key, autocorrect or Android commits (beforeinput the browser would apply to the DOM): ProseMirror
 * inserts it over the range itself, else the browser's own replace took the decorations' text (chip labels) into the document and lost
 * the character; a composition starting over such a range deletes it first. A range inside one block stays the editor's own.
 */
const RangeEnter = Extension.create({
  name: 'markdownRangeEnter',
  priority: 101,
  addKeyboardShortcuts() {
    return { Enter: () => {
      const { view } = this.editor
      if (!acrossBlocks(view.state.selection) || !this.editor.isEditable) return false
      const deleted = view.state.tr.deleteSelection().scrollIntoView()
      view.dispatch(deleted)
      // The caret's Enter, as a keypress there would run it, in the delete's undo step: history joins a transaction marked as appended
      // to it (by ranges alone an Enter a position past the deleted range, e.g. a paragraph before the heading, became its own step).
      const dispatch = view.dispatch
      view.dispatch = tr => dispatch(tr.setMeta('appendedTransaction', deleted))
      try {
        view.someProp('handleKeyDown', handler => handler(view, new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })))
      } finally {
        view.dispatch = dispatch
      }
      return true
    } }
  },
  addProseMirrorPlugins() {
    const editor = this.editor
    return [new Plugin({ props: { handleDOMEvents: {
      beforeinput(view, event) {
        const data = event.data ?? event.dataTransfer?.getData('text/plain') ?? ''
        if (!['insertText', 'insertReplacementText', 'insertCompositionText'].includes(event.inputType) || !data || !editor.isEditable || !acrossBlocks(view.state.selection)) return false
        event.preventDefault()
        view.dispatch(view.state.tr.insertText(data).scrollIntoView())
        return true
      },
      compositionstart(view) {
        if (editor.isEditable && acrossBlocks(view.state.selection)) view.dispatch(view.state.tr.deleteSelection())
        return false
      },
    } } })]
  },
})

export function createMarkdownContentKit(options: MarkdownContentKitOptions = {}): MarkdownKit {
  const resourcePolicy = normalizeResourcePolicy(options.resourcePolicy)
  if (options.codeBlock && (options.codeBlock.name !== 'codeBlock' || options.codeBlock.type !== 'node')) {
    throw new TypeError('codeBlock must be a compatible codeBlock node extension.')
  }
  const profile = options.profile ?? 'markdown-safe'
  const rich = profile === 'rich'
  const extensions: AnyExtension[] = [
    StarterKit.extend({
      addExtensions() {
        return (this.parent?.() ?? []).map(extension => extension.name === 'codeBlock' ? dialectCodeBlock(extension)
          // Inline code combines with the other marks as Markdown does (**`x`**, [`x`](url)); ranked last, it is written innermost.
          : extension.name === 'code' ? extension.extend({ excludes: 'code', priority: 99 })
          : extension.name === 'heading' ? headingToText(extension)
          : extension.name === 'text' ? autolinkText(extension)
          : extension.name === 'listKeymap' ? quickListBackspace(extension)
          : extension.name === 'gapCursor' ? caretHiddenAtGap(extension)
          : extension.type === 'mark' ? outsideInlineCode(extension) : extension)
      },
    }).configure({
      heading: { levels: [...HEADING_LEVELS] },
      codeBlock: options.codeBlock ? false : { HTMLAttributes: { class: 'tt-code-block' } },
      undoRedo: options.collaborative ? false : undefined,
      link: false,
      underline: false,
      // MarkdownParagraph: the same node with a Markdown serializer that keeps a quote prefix before a leading mark.
      paragraph: false,
      // Backspace and Delete at an item's edge as in a bullet list, timed items too (skipped where the schema has none: markdown-safe).
      listKeymap: { listTypes: [{ itemName: 'listItem', wrapperNames: ['bulletList', 'orderedList'] }, { itemName: 'taskItem', wrapperNames: ['taskList'] },
        { itemName: 'timedItem', wrapperNames: ['timedList'] }] },
    }),
    MarkdownParagraph,
    ...(options.codeBlock ? [dialectCodeBlock(options.codeBlock)] : []),
    outsideInlineCode(Link).extend({
      // Text typed at a link's end is not part of the link (tiptap makes an autolinking mark inclusive); typed inside, it is.
      inclusive: false,
      // prosemirror-markdown's link, except that an address `<…>` would cut (a blank, `<` or `>` in it) is written [text](url). As EditorMarkdownWriter.
      addStorage() {
        type LinkState = { inAutolink?: boolean }
        return { ...this.parent?.(), markdown: { parse: {}, serialize: {
          open(state: LinkState, mark: ProseMirrorMark, parent: ProseMirrorNode, index: number) {
            const href = String(mark.attrs.href), content = parent.child(index)
            state.inAutolink = !mark.attrs.title && /^\w+:/.test(href) && !/[\s<>]/.test(href) && content.isText && content.text === href
              && content.marks[content.marks.length - 1] === mark && (index === parent.childCount - 1 || !mark.isInSet(parent.child(index + 1).marks))
            return state.inAutolink ? '<' : '['
          },
          close(state: LinkState, mark: ProseMirrorMark) {
            const autolink = state.inAutolink
            state.inAutolink = undefined
            return autolink ? '>' : `](${String(mark.attrs.href).replace(/[()"]/g, '\\$&')}${mark.attrs.title ? ` "${String(mark.attrs.title).replace(/"/g, '\\"')}"` : ''})`
          },
          mixable: true,
        } } }
      },
      // Autolink reads the transaction that fills the editor from the shared document (y-sync) like typing, and linked a bare URL
      // of the first text block on open: the page counted as changed. Only what is typed or pasted is linked. A transaction appended
      // to the fill or the seed (a repeated heading id renamed) belongs to it too.
      addProseMirrorPlugins() {
        const opening = (tr: Transaction) => { const root: Transaction = tr.getMeta('appendedTransaction') ?? tr; return root.getMeta('y-sync$') || root.getMeta('preventAutolink') }
        return (this.parent?.() ?? []).map(plugin => {
          const append = plugin.spec.appendTransaction
          return !append ? plugin : new Plugin({ ...plugin.spec,
            appendTransaction(transactions, before, after) { return transactions.some(opening) ? null : append.call(this, transactions, before, after) } })
        })
      },
      // A link opens in a new tab (target _blank, always with rel="noopener noreferrer") or in place, and carries no class: rel and class
      // are never taken from the document or from pasted HTML (reverse tabnabbing, UI redressing). The server refuses other stored values
      // (schema/builtin.ts); engine/legacyDocument.ts strips them from older documents.
      addAttributes() {
        return {
          ...this.parent?.(),
          target: { default: '_blank', parseHTML: () => null, renderHTML: attributes => attributes.target === '_blank' ? { target: '_blank', rel: LINK_REL } : {} },
          rel: { default: LINK_REL, parseHTML: () => null, renderHTML: () => ({}) },
          class: { default: null, parseHTML: () => null, renderHTML: () => ({}) },
        }
      },
    }).configure({
      openOnClick: false,
      autolink: true,
      HTMLAttributes: {},
    }),
    (rich ? RichImage : Image).extend({
      addAttributes() {
        const attributes: Attributes = this.parent?.() ?? {}
        return { ...attributes, src: { ...attributes.src, ...imageSourceAttribute(resourcePolicy) } }
      },
      // tiptap-markdown's default image serializer is inline and glued a block image to the next block.
      addStorage() { return { ...this.parent?.(), markdown: { serialize: serializeImage } } },
    }).configure({ allowBase64: true, HTMLAttributes: { class: 'tt-image' } }),
    ImageUpload.configure({ uploadUrl: options.imageUploadUrl ?? null, upload: options.imageUpload ?? null }),
    createResourcePolicyExtension(resourcePolicy),
    // A selected table stays one selected element (its handle, „Co s tím jde dělat“, Delete), not a selection of all its cells.
    MarkdownTable.configure({ resizable: rich, cellMinWidth: 72, handleWidth: 7, allowTableNodeSelection: true, HTMLAttributes: { class: 'tt-table' } }),
    TableRow,
    TableHeader,
    TableCell,
    TaskList.configure({ HTMLAttributes: { class: 'tt-task-list' } }),
    ReaderTaskItem.configure({ nested: true, HTMLAttributes: { class: 'tt-task-item' } }),
    ...(rich ? [
      Callout,
      // „Časová odrážka“ (rich revision 5); markdown-safe reads its Markdown as a bullet list.
      TimedList, TimedItem,
      // `==text==`, parsed back by the editor dialect (engine/markdownImportSyntax.ts).
      outsideInlineCode(Highlight.extend({
        addStorage() { return { ...this.parent?.(), markdown: { serialize: { open: '==', close: '==', mixable: true, expelEnclosingWhitespace: true } } } },
      })).configure({ multicolor: false, HTMLAttributes: { class: 'tt-highlight' } }),
      Underline,
      TextAlign.configure({ types: ['heading', 'paragraph'], alignments: ['left', 'center', 'right'] }),
    ] : []),
    CellTextAlign.configure({ columnScope: !rich }),
    ...(rich ? [CellBackground, RowHeight] : []),
    Typography,
    LinkToolbar,
    DomCaretFirst,
    TypedTextInput,
    RangeEnter,
    ChipText,
    Intentions,
    MarkdownRecognition.configure({ lexicon: options.lexicon }),
    SelectedElementEscape,
    WordHistoryBoundary,
    CharacterCount,
    Placeholder.configure({
      placeholder: ({ editor, node }) => node.type.name === 'paragraph'
        ? resolveEditorPlaceholder(editor, options.placeholder)
        : node.type.name === 'heading' ? surfaceStrings.headingPlaceholder : '',
      showOnlyCurrent: true,
      includeChildren: false,
    }),
    // Markdown text paste is parsed by MarkdownInput, the shared import boundary;
    // native plain-text paste (Shift+paste) and code blocks remain literal in PM.
    Markdown.configure({ html: false, linkify: true, breaks: false, transformPastedText: false }),
    MarkdownInput.configure({ onImport: options.onMarkdownImport, resolveWikiLink: options.resolveWikiLink, resolveEmbed: options.resolveEmbed }),
    ...(!rich ? [MarkdownSafeTableCommands] : []),
    ...(options.extraExtensions ?? []),
  ]

  return {
    extensions,
    capabilities: {
      mergedCells: rich,
      cellBackground: rich,
      rowHeight: rich,
    },
  }
}
