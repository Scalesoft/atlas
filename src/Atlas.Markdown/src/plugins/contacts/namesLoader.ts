import { Extension, type Editor } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { nameKey, refreshMarkdownRecognition, registerMarkdownRecognizer, whenMarkdownIdle,type MarkdownIntentionProvider, type MarkdownLexicon, type MarkdownLexiconPerson, type MarkdownRecognitionEntry, type MarkdownRecognizer } from '../../core'
import { normalizeContact, type ContactsPluginOptions, type MarkdownContact } from './data'
import { inlineContactJson } from './pick'

/*
 * People in the linking engine (core/recognition, P15): the eager part. The person recognizer gives the core its entries, from the
 * place's lexicon (controller option `lexicon`) or, until the host switches to it, from the host's known people (`options.known`),
 * plus the contacts the document holds; the core converts a typed whole name (one person: the chip, several: an unresolved chip) and
 * underlines the rest. The ranked „Je to …?“ lists and the actions menu rows load after the first render (names.ts, P17).
 */

// Ctrl+. is the actions menu's (core intentions, P2): its contact rows (contactIntentions) offer the name's people and „Udělat kontakt“.
export interface ContactNamesOptions { options: ContactsPluginOptions }

/** The directory's aliases of a known person, kept beside its normalized contact (`known` may carry `aliases`). */
export const knownAliases = new WeakMap<MarkdownContact, readonly string[]>()
/** The aliases a known person came with (`{ …contact, aliases: ["MMu"] }`); the snapshot never carries them. */
const aliasesIn = (person: unknown) => { const list = (person as { aliases?: unknown }).aliases; return Array.isArray(list) ? list.filter((alias): alias is string => typeof alias === 'string') : [] }
/** The known people, read once per plugin configuration (the host caches across editors too); doc contacts are added per scan. */
const knownPeople = new WeakMap<ContactsPluginOptions, Promise<readonly MarkdownContact[]>>()
/** The same once they are in, for what reads them synchronously (the recognizer's entries). */
const knownNow = new WeakMap<ContactsPluginOptions, readonly MarkdownContact[]>()
export function knownOf(options: ContactsPluginOptions): Promise<readonly MarkdownContact[]> {
  let known = knownPeople.get(options)
  if (!known) {
    const read = (person: MarkdownContact) => { const contact = normalizeContact(person), aliases = aliasesIn(person); if (aliases.length) knownAliases.set(contact, aliases); return contact }
    known = (options.known
      ? Promise.resolve().then(() => options.known!({ signal: new AbortController().signal })).then(people => people.flatMap(person => { try { return [read(person)] } catch { return [] } }), () => [])
      : Promise.resolve([])).then(people => { knownNow.set(options, people); return people })
    knownPeople.set(options, known)
  }
  return known
}

/** How many pages of the place name a lexicon person (`uses`): the ranking's „used in this space“ before any request. */
export const knownUses = new WeakMap<MarkdownContact, number>()
/** A lexicon person as the snapshot a chip holds; aliases and uses kept beside it. */
const lexiconContacts = new WeakMap<MarkdownLexiconPerson, MarkdownContact | null>()
function lexiconContact(person: MarkdownLexiconPerson): MarkdownContact | null {
  let contact = lexiconContacts.get(person)
  if (contact === undefined) {
    try {
      contact = normalizeContact({ name: person.name, ...person.contactId ? { objectId: person.contactId } : {}, ...person.userId ? { internalUser: { id: person.userId, name: person.name } } : {},
        ...person.org ? { organization: person.org } : {}, ...person.role ? { title: person.role } : {} })
      if (person.aliases.length) knownAliases.set(contact, person.aliases)
      if (person.uses) knownUses.set(contact, person.uses)
    } catch { contact = null }
    lexiconContacts.set(person, contact)
  }
  return contact
}

/** The contacts a document holds, for its names and the context of a ranking. */
export function documentContacts(doc: PMNode): MarkdownContact[] {
  const found: MarkdownContact[] = []
  doc.descendants(node => {
    if (node.type.name !== 'contactInline' && node.type.name !== 'contactCard') return true
    found.push(...((node.attrs.data as { contacts?: MarkdownContact[] } | null)?.contacts ?? []))
    return false
  })
  return found
}

/** An alias shorter than this (folded, without spaces) is not looked for in the text: „MM“ is two people's initials and a common pair. */
const MIN_ALIAS = 3
/** A name's initials as written in capitals („Jan Pavel Novák“ → „JPN“): the lexicon does not store them; only ever offered, as an alias. */
const initialsOf = (name: string) => { const words = name.trim().split(/\s+/u); return words.length >= MIN_ALIAS ? [words.map(word => word[0]!.toLocaleUpperCase('cs')).join('')] : [] }
/**
 * The person entries: everyone of the lexicon (or the known people) by full name (a chip when the only one and the lexicon is whole)
 * and by alias (only offered, D4; one of three letters as learned only: „MMu“, not „MMU“); then the document's own contacts whose name
 * nobody of them has. One person is one candidate however often they are listed.
 */
export function personEntries(options: ContactsPluginOptions, lexicon: MarkdownLexicon | undefined, doc: PMNode): MarkdownRecognitionEntry[] {
  // A lexicon person is one by its identity key; known people by the contact, account, directory entry or e-mail they share.
  const people = lexicon ? lexicon.people.flatMap(person => { const contact = lexiconContact(person); return contact ? [{ contact, id: person.key }] : [] })
    : (knownNow.get(options) ?? []).map(contact => ({ contact, id: undefined }))
  const entries: MarkdownRecognitionEntry[] = [], keys = new Set<string>(), ids = new Map<string, string>()
  let unnamed = 0
  const idOf = (contact: MarkdownContact) => {
    const own = [contact.objectId && `object:${contact.objectId}`, contact.internalUser?.id && `user:${contact.internalUser.id}`, contact.directoryId && `directory:${contact.directoryId}`,
      ...(contact.emails ?? []).map(email => `email:${email.toLowerCase()}`)].filter((key): key is string => !!key)
    const id = own.map(key => ids.get(key)).find(Boolean) ?? own[0] ?? `name:${++unnamed}`
    own.forEach(key => ids.set(key, id))
    return id
  }
  const person = (contact: MarkdownContact, auto: boolean, aliases: readonly string[], id = idOf(contact)) => {
    entries.push({ kind: 'person', id, name: contact.name, form: 'full', auto, inflects: true, target: contact }); keys.add(nameKey(contact.name))
    for (const alias of aliases) {
      const key = nameKey(alias)
      if (key.replace(/ /gu, '').length < MIN_ALIAS) continue
      entries.push({ kind: 'person', id, name: alias.trim(), form: 'alias', auto: false, cased: !key.includes(' ') && key.length <= MIN_ALIAS, target: contact }); keys.add(key)
    }
  }
  people.forEach(({ contact, id }) => person(contact, !lexicon?.truncated, [...knownAliases.get(contact) ?? [], ...initialsOf(contact.name)], id))
  // A document's contact named like someone's alias („Mára Musil“) is not added, so that name stays an alias: never a chip by itself.
  documentContacts(doc).forEach(contact => { if (!contact.unresolved && !keys.has(nameKey(contact.name))) person(contact, true, []) })
  return entries
}

/** The contact of a name several people have, nobody picked yet: the name as written. */
export const unresolvedContact = (name: string): MarkdownContact => ({ name: name.replace(/\s+/gu, ' ').trim(), unresolved: true })

/** The open „Je to …?“ list of an editor (names.ts offerPeople). Here, eager: an unresolved chip opens it before the ranking loads. */
interface OpenList { list: { onKeyDown(event: KeyboardEvent): boolean }; destroy(): void }
export const openLists = new WeakMap<Editor, OpenList>()
export const closeList = (editor: Editor) => { const open = openLists.get(editor); openLists.delete(editor); open?.destroy() }
/** An open list takes arrows, Home/End, Enter and Tab (picks) and Esc (closes); any other key closes it and goes on to the text. */
function openListKeyDown(editor: Editor, open: OpenList, event: KeyboardEvent): boolean {
  if (event.key === 'Escape') { closeList(editor); return true }
  if (['ArrowUp', 'ArrowDown', 'Home', 'End', 'Enter', 'Tab'].includes(event.key) && !event.ctrlKey && !event.metaKey && !event.altKey) {
    if (event.key === 'Tab') return open.list.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' })) || true
    return open.list.onKeyDown(event) || true
  }
  if (!['Shift', 'Control', 'Alt', 'Meta'].includes(event.key)) closeList(editor)
  return false
}

let loading: Promise<typeof import('./names')> | undefined, loaded: typeof import('./names') | undefined
// A failed download (offline, a deploy replaced the chunk) is not kept: the next click or Ctrl+Space asks again.
export const loadNames = () => loading ??= import('./names').then(module => (loaded = module), (error: unknown) => { loading = undefined; throw error })
/** The ranking once it is in, for what must not wait for its download (the `@` list's order). */
export const namesNow = () => loaded

/** The person recognizer of one editor: entries, chips, and a click on an underlined name („Je to …?“, names.ts). */
const personRecognizer = (options: ContactsPluginOptions): MarkdownRecognizer => ({
  kind: 'person',
  unresolved: true,
  entries: (lexicon, doc) => personEntries(options, lexicon, doc),
  chip: hit => hit.decision === 'unresolved' ? inlineContactJson(unresolvedContact(hit.text))
    : hit.decision === 'chip' && hit.candidates.length === 1 ? inlineContactJson(hit.candidates[0] as MarkdownContact) : null,
  offer: (editor, hit) => { void loadNames().then(names => names.offerName(editor, options, hit), () => undefined); return true },
})

export function contactNames(config: ContactNamesOptions) {
  return Extension.create({
    name: 'markdownContactNames',
    // Before every Enter, Tab and Escape (lists, blocks, a selected chip's edit): an open list of people takes them.
    priority: 1200,
    onCreate() {
      const editor = this.editor, stop = registerMarkdownRecognizer(editor, personRecognizer(config.options))
      editor.on('destroy', stop)
      // The lists and rows after the first render, while the editor idles.
      whenMarkdownIdle(() => void loadNames().catch(() => undefined))
    },
    addProseMirrorPlugins() {
      const editor = this.editor
      return [new Plugin({
        key: new PluginKey('markdownContactNames'),
        // An open list first, loaded or not.
        props: { handleKeyDown(_view, event) { const open = !event.isComposing && openLists.get(editor); return open ? openListKeyDown(editor, open, event) : false } },
        view: () => {
          // The host's answer is the slow part: asked with the first render, the core reads the people again when it is in.
          if (config.options.known) void knownOf(config.options).then(() => refreshMarkdownRecognition(editor))
          return { destroy: () => closeList(editor) }
        },
      })]
    },
  })
}

/** The contact rows of the actions menu (names.ts `contactIntentions`), with the ranking loaded first. */
export const contactIntentions = (options: ContactsPluginOptions, pickSelection?: (editor: Editor) => boolean): MarkdownIntentionProvider =>
  async context => (await loadNames()).contactIntentions(options, pickSelection)(context)
