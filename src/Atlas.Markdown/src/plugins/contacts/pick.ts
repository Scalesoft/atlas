import type { Command, Editor } from '@tiptap/core'
import { TextSelection, type EditorState, type Transaction } from '@tiptap/pm/state'
import { dispatchMarkdownHistoryOperation } from '../../core'
import { normalizeContact, type ContactsPluginOptions, type MarkdownContact } from './data'

/** Longest selected text that „Udělat kontakt“ takes as a name. */
export const SELECTION_NAME_LIMIT = 80

/**
 * The selected text as a person's name: one line of plain text in one text block (no code, no contact or other inline node in it),
 * at most 80 characters, with a letter. `from`/`to` leave out the spaces around it (a double-click selects the space after a word).
 */
export function selectedName(state: EditorState): { from: number; to: number; text: string } | null {
  const { selection } = state
  if (!(selection instanceof TextSelection) || selection.empty || !selection.$from.sameParent(selection.$to)) return null
  const parent = selection.$from.parent
  if (!parent.isTextblock || parent.type.spec.code) return null
  const raw = state.doc.textBetween(selection.from, selection.to, '\n', '￼')
  if (/[\n￼]/u.test(raw) || (state.schema.marks.code && state.doc.rangeHasMark(selection.from, selection.to, state.schema.marks.code))) return null
  const text = raw.trim()
  if (!text || text.length > SELECTION_NAME_LIMIT || !/\p{L}/u.test(text)) return null
  const from = selection.from + (raw.length - raw.trimStart().length)
  return { from, to: from + text.length, text: text.replace(/\s+/gu, ' ') }
}

/** The contact nodes the picker answered: in the text one after another („Jan, Eva“), or one card each. */
type Node = (inline: boolean, contact: MarkdownContact) => Record<string, unknown>
const contents = (contacts: MarkdownContact[], inline: boolean, node: Node) => inline
  ? contacts.flatMap((contact, index) => [...index ? [{ type: 'text', text: ', ' }] : [], node(true, contact)])
  // One card per person, one after another: text and headings can go between them.
  : contacts.map(contact => node(false, contact))

/**
 * `contacts.pick`: the host's person picker with the typed text (`options.pick`), or without one the `@` people suggestion.
 * `inline` unset: into the text when the line has text, a card on an empty line. `selection`: the selected name is the query and
 * the person replaces it (one undo step gives the text back); not available for a selection that is no name (`selectedName`).
 */
export function pickInto(options: ContactsPluginOptions, node: Node, { query = '', inline, selection: fromSelection }: { query?: string; inline?: boolean; selection?: boolean }): Command {
  return ({ tr, dispatch, editor, commands: run }) => {
    const $from = tr.selection.$from
    if (!editor.isEditable || $from.parent.type.spec.code) return false
    const selected = fromSelection ? selectedName(editor.state) : null
    if (fromSelection && !selected) return false
    const text = selected?.text ?? query.replace(/\s+/gu, ' ').trim()
    if (!options.pick) {
      if (!options.directory || !options.suggestions) return false
      if (!dispatch) return true
      if (selected) tr.delete(selected.from, selected.to)
      const at = tr.selection.$from
      const previous = at.parentOffset ? tr.doc.textBetween(at.pos - 1, at.pos) : ''
      return run.insertContent(`${previous && !/[\s(]/u.test(previous) ? ' @' : '@'}${text}`)
    }
    if (!dispatch) return true
    const asInline = selected ? true : inline ?? $from.parent.content.size > 0
    let position = selected?.from ?? $from.pos, end = selected?.to ?? position
    const abort = new AbortController()
    const follow = ({ transaction }: { transaction: Transaction }) => { position = transaction.mapping.map(position); end = Math.max(position, transaction.mapping.map(end, -1)) }
    const stop = () => abort.abort()
    const done = () => { editor.off('transaction', follow); editor.off('destroy', stop) }
    // After this command's own transaction: `position` is already in its document.
    queueMicrotask(() => {
      if (editor.isDestroyed) return
      editor.on('transaction', follow); editor.on('destroy', stop)
      const anchor = () => { const { left, top, bottom } = editor.view.coordsAtPos(Math.min(position, editor.state.doc.content.size)); return new DOMRect(left, top, 1, bottom - top) }
      Promise.resolve().then(() => options.pick!({ query: text, inline: asInline, signal: abort.signal, anchor, editor })).then(result => {
        done()
        if (abort.signal.aborted || editor.isDestroyed) return
        const contacts = (result == null ? [] : Array.isArray(result) ? result : [result]).map(contact => normalizeContact(contact))
        if (!contacts.length || !editor.isEditable) { editor.view.focus(); return }
        const content = contents(contacts, asInline, node)
        if (!selected) { editor.chain().focus().insertContentAt(Math.min(position, editor.state.doc.content.size), content).run(); return }
        // The selected name, unless it was edited meanwhile: then the person goes beside what is there now.
        const still = editor.state.doc.textBetween(position, end, ' ', '￼').replace(/\s+/gu, ' ') === text
        replaceWithContacts(editor, still ? position : end, end, content)
      }).catch(() => { done(); if (!editor.isDestroyed) editor.view.focus() })
    })
    return true
  }
}

/** Puts contact nodes in place of `from`–`to` as one undo step of its own; a caret on them goes after them, one elsewhere stays. */
export function replaceWithContacts(editor: Editor, from: number, to: number, content: Record<string, unknown>[]) {
  const nodes = content.map(item => editor.schema.nodeFromJSON(item))
  const { selection } = editor.state, elsewhere = selection.empty && (selection.from < from || selection.from > to)
  const tr = editor.state.tr.replaceWith(from, to, nodes)
  if (!elsewhere) tr.setSelection(TextSelection.create(tr.doc, tr.mapping.map(to)))
  dispatchMarkdownHistoryOperation(editor, tr)
  editor.view.focus()
}

/** The contact of a person put in the text in place of a name. */
export const inlineContactJson = (contact: MarkdownContact) => ({ type: 'contactInline', attrs: { data: { version: 1, contacts: [normalizeContact(contact)] } } })
