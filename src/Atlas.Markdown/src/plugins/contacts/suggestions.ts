import { Extension } from '@tiptap/core'
import { PluginKey, type Transaction } from '@tiptap/pm/state'
import Suggestion from '@tiptap/suggestion'
import { createMarkdownSuggestionRenderer, dispatchMarkdownHistoryOperation, markdownChangeMapping, shouldShowMarkdownSuggestion } from '../../core'
import { contactsStrings } from './strings'
import { linkInserted, normalizeContact, type ContactsPluginOptions, type MarkdownContact } from './data'
import { loadNames, namesNow } from './namesLoader'

/**
 * `@` people: the directory's answer (debounced, the host caches), the people the page's context names first among equally good
 * matches (names.ts `contextOrder`: on this page, the role words before the `@`, picked lately); a pick is told to the directory with the typed text
 * (names.ts `pickedPerson`), which may learn it as the person's alias („@MMu“ → Marek Musil). names.ts loads lazily (namesLoader.ts):
 * it loads beside the directory's search, and an answer before it is in (or when it cannot load) keeps the directory's order.
 */
export function contactSuggestions(options: ContactsPluginOptions) {
  const key = new PluginKey('markdownContactSuggestions')
  const config = typeof options.suggestions === 'object' ? options.suggestions : {}
  const resultIds = new WeakMap<MarkdownContact, string>()
  const listed = (values: readonly MarkdownContact[]) => values.map((value, index) => {
    const contact = normalizeContact(value)
    resultIds.set(contact, `${contact.internalUser?.id ?? contact.directoryId ?? contact.name}:${index}`)
    return contact
  })
  const cached = options.directory?.cached?.bind(options.directory)
  return Extension.create({
    name: 'markdownContactSuggestions',
    addProseMirrorPlugins() {
      const editor = this.editor
      return [Suggestion<MarkdownContact>({
        editor, pluginKey: key, char: '@', allowSpaces: true, shouldShow: shouldShowMarkdownSuggestion,
        allowedPrefixes: [' ', '\n', '('], items: () => [],
        allow: ({ editor, state, range }) => editor.isEditable && !state.selection.$from.parent.type.spec.code
          && (!state.schema.marks.code || !state.doc.rangeHasMark(range.from, range.to, state.schema.marks.code))
          && (!state.schema.marks.link || !state.doc.rangeHasMark(range.from, range.to, state.schema.marks.link)),
        command: ({ editor, range, props }) => {
          if (!editor.isEditable) return
          const contact = normalizeContact(props)
          const typed = editor.state.doc.textBetween(range.from, range.to)
          const put = (from: number, to: number, value: MarkdownContact) => {
            dispatchMarkdownHistoryOperation(editor, editor.state.tr.replaceWith(from, to, editor.schema.nodes.contactInline!.create({ data: { version: 1, contacts: [value] } })))
            editor.view.focus()
            void loadNames().then(names => names.pickedPerson(options, typed.slice(1), value), () => undefined)
          }
          if (!options.link) { put(range.from, range.to, contact); return }
          // As every insert: the host links the person first and may ask about a match. The typed `@…` leaves now (the list closes) and
          // the person goes where it was; going back brings the typed text, and with it the list, back.
          let at = range.from
          editor.view.dispatch(editor.state.tr.delete(range.from, range.to))
          const follow = ({ transaction }: { transaction: Transaction }) => { at = markdownChangeMapping(transaction).map(at) }
          const abort = new AbortController(), stop = () => abort.abort()
          editor.on('transaction', follow); editor.on('destroy', stop)
          const anchor = () => { const { left, top, bottom } = editor.view.coordsAtPos(Math.min(at, editor.state.doc.content.size)); return new DOMRect(left, top, 1, bottom - top) }
          void linkInserted(options, contact, { editor, anchor }, abort.signal).then(linked => {
            editor.off('transaction', follow); editor.off('destroy', stop)
            if (editor.isDestroyed || !editor.isEditable) return
            const position = Math.min(at, editor.state.doc.content.size)
            if (linked) put(position, position, linked)
            else { editor.view.dispatch(editor.state.tr.insertText(typed, position)); editor.view.focus() }
          })
        },
        render: createMarkdownSuggestionRenderer<MarkdownContact>({
          pluginKey: key, label: contactsStrings.label, testId: 'contact-suggestions',
          hintText: contactsStrings.suggestionHint, emptyText: contactsStrings.suggestionEmpty,
          source: { search: async (query, context) => {
            // The ordering loads beside the search, never after it (P17): an answer before it is in shows in the directory's order.
            void loadNames().catch(() => undefined)
            const found = listed(await options.directory!.search(query, context))
            // The words before the `@` in its paragraph („PM @ja“), not the query itself.
            const names = namesNow()
            if (!names) return found
            const { $from } = editor.state.selection, before = $from.parent.textBetween(0, $from.parentOffset, ' ', ' ')
            return names.contextOrder(found, { doc: editor.state.doc, text: before.slice(0, Math.max(0, before.lastIndexOf('@'))), query })
          }, cached: cached && (query => listed(cached(query))), debounceMs: config.debounceMs ?? 200, limit: config.limit ?? 20, minQueryLength: 1 },
          mapItem: contact => ({ id: resultIds.get(contact)!, title: contact.internalUser?.name ?? contact.name, description: [contact.department, contact.organization, contact.emails?.[0]].filter(Boolean).join(' · '), avatar: contact.internalUser?.photo ?? contact.photo }),
        }),
      })]
    },
  })
}
