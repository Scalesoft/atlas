import { posToDOMRect, type Editor } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { createSuggestionList, createSuggestionPopup, dismissMarkdownRecognition, dispatchMarkdownHistoryOperation, markdownRecognitionAt, markdownRecognitionCandidates, nameKey, tokenize, type MarkdownIntention, type MarkdownIntentionProvider, type MarkdownRecognitionHit } from '../../core'
import { contactsStrings } from './strings'
import { linkInserted, normalizeContact, type ContactsPluginOptions, type MarkdownContact } from './data'
import { inlineContactJson, replaceWithContacts, selectedName } from './pick'
import { closeList, documentContacts, knownOf, knownUses, openLists, unresolvedContact } from './namesLoader'

/*
 * Names that are people (owner 2026-10-30: pages linked by design), the part that loads after the editor's first render (P17): the
 * linking engine (core/recognition) recognises the names, with the person entries of namesLoader.ts; here they are ranked and offered.
 * - Typed: the core makes a whole known name the contact chip (one person) or an unresolved chip (several people of that name: nobody
 *   is picked for the person). Backspace right after gives the text back without the trigger, Ctrl+Z undoes only the chip.
 * - Written: known names get the core's dotted underline; a click asks „Je to …?“ and the actions menu (Ctrl+Space, Ctrl+., right
 *   button) offers them, ranked by the page's context.
 * - Aliases, initials and inflected forms („MMu“, „Jany Novákové“) are underlined and offered like a name, never made a chip by
 *   themselves (owner D4).
 * Nothing goes to the directory by itself: one person goes in as the snapshot the host knows (the render links it as every
 * unlinked snapshot), several stay unresolved until picked, an explicit pick goes through `options.link` with `insert` and is
 * told to the directory (`pickedPerson`).
 */

export const displayName = (person: MarkdownContact) => person.internalUser?.name ?? person.name
/** What tells two people of one name apart. */
export const describePerson = (person: MarkdownContact) => [person.organization, person.title, person.department, person.emails?.[0]].filter(Boolean).join(' · ')

export interface RankedPerson { contact: MarkdownContact; reason?: string }
// shortcut: a few role abbreviations only; grow the list when people miss one.
const ROLE_WORDS: Record<string, string[]> = { pm: ['projektovy', 'projektova', 'manazer', 'manazerka', 'project', 'manager'], ceo: ['reditel', 'reditelka'], cto: ['technicky', 'technicka', 'reditel'] }
const wordsOf = (text: string) => tokenize(text).map(token => token.key)
/** The role abbreviations the words spell out („projektová manažerka“ → pm). */
const spelled = (words: readonly string[]) => Object.entries(ROLE_WORDS).flatMap(([short, long]) => (words.filter(word => long.includes(word)).length >= 2 ? [short] : []))
/**
 * Whether the words around (`near`, with the abbreviations they spell out) name a field: its whole value („Modelsoft“, „Nemocnice Na
 * Kopečku“), its initials („Projektová manažerka“ → pm) or a role abbreviation it names or spells out. Never one of its words alone:
 * „čekáme na“ says nothing of „Nemocnice Na Kopečku“.
 */
function namesField(field: string, near: ReadonlySet<string>) {
  // One-letter words never reach `near` (contextOf): a legal form („a.s.“, „s.r.o.“) must not block the organization's match.
  const words = wordsOf(field).filter(word => word.length > 1)
  return words.length > 0 && (words.every(word => near.has(word)) || [...(words.length > 1 ? [words.map(word => word[0]).join('')] : []),
    ...words.filter(word => Object.hasOwn(ROLE_WORDS, word)), ...spelled(words)].some(role => near.has(role)))
}
/** The people picked lately in this tab, the latest first: every list of people ranks them up. */
// shortcut: in memory per tab; keep them in the profile when people ask for them across reloads.
const recent: string[] = []
const RECENT = 20
const pickKey = (contact: MarkdownContact) => contact.objectId ?? contact.internalUser?.id ?? nameKey(contact.name)
/** An explicit pick of a person (`@`, „Je to …“, an unresolved chip, the actions menu). */
export function rememberPick(contact: MarkdownContact) {
  const key = pickKey(contact), at = recent.indexOf(key)
  if (at >= 0) recent.splice(at, 1)
  recent.unshift(key)
  recent.length = Math.min(recent.length, RECENT)
}
const recency = (contact: MarkdownContact) => { const at = recent.indexOf(pickKey(contact)); return at < 0 ? 0 : RECENT - at }

/**
 * A person explicitly picked for `query` (`@MMu`, an underlined „Mára“, a written name): remembered for the ranking, and told to the
 * directory, whose host may teach its contact the query as an alias (Atlas: never a plain beginning of the name, contactSearch.ts
 * `teachesAlias`). Nothing is learned from an entity's own fields or a search field: those editors have no people at all.
 */
export function pickedPerson(options: ContactsPluginOptions, query: string, contact: MarkdownContact) {
  rememberPick(contact)
  try { options.directory?.learn?.(query.replace(/\s+/gu, ' ').trim(), contact) } catch { /* the pick stands */ }
}

/**
 * What the page says of each person without asking anything (per keystroke, no AI): whether the document holds them, which of their
 * function, department, organization or page role the words around say („PM“, „projektová manažerka“), how lately they were picked.
 */
function contextOf(people: readonly MarkdownContact[], context: { doc: PMNode; text: string }) {
  const here = new Set(documentContacts(context.doc).filter(contact => !contact.unresolved).map(pickKey))
  const words = wordsOf(context.text).filter(word => word.length > 1), near = new Set([...words, ...spelled(words)])
  return people.map(contact => ({ here: here.has(pickKey(contact)),
    field: [contact.role, contact.title, contact.department, contact.organization].find(value => value && namesField(value, near)),
    recent: recency(contact) }))
}
const byKeys = (a: { key: number[]; index: number }, b: { key: number[]; index: number }) => { for (let i = 0; i < a.key.length; i++) if (a.key[i] !== b.key[i]) return b.key[i]! - a.key[i]!; return a.index - b.index }

/** 0 the name starts as typed, 1 every typed word begins a word of the name, 2 anything else the directory matched (an alias, initials, another field). */
function nameMatch(query: string, contact: MarkdownContact) {
  const typed = wordsOf(query), name = wordsOf(contact.name)
  if (!typed.length || !typed.every(word => name.some(part => part.startsWith(word)))) return 2
  return name[0]!.startsWith(typed[0]!) ? 0 : 1
}

/**
 * `people` (the directory's answer for `query`, the best match first) with those the page's context names first (contextOf), but only
 * among neighbours that match `query` equally well (nameMatch): a weaker match never becomes the preselected first row. Sync and cheap:
 * the `@` list per answer.
 */
export function contextOrder(people: readonly MarkdownContact[], context: { doc: PMNode; text: string; query: string }): MarkdownContact[] {
  if (people.length < 2) return [...people]
  const scores = contextOf(people, context), tiers = people.map(contact => nameMatch(context.query, contact))
  let run = 0
  return people.map((contact, index) => {
    if (index && tiers[index] !== tiers[index - 1]) run++
    const { here, field, recent: lately } = scores[index]!
    return { contact, index, key: [-run, here ? 1 : 0, field ? 1 : 0, lately] }
  }).sort(byKeys).map(({ contact }) => contact)
}

/**
 * The people of one name, the most likely first, each with why: who the document already holds, whose function, department or
 * page role the words around the name say („PM“, „projektová manažerka“), who is used in this space, who was picked lately in this
 * tab, who is used most. No AI.
 */
export async function rankPeople(options: ContactsPluginOptions, people: readonly MarkdownContact[], context: { doc: PMNode; text: string; signal: AbortSignal }): Promise<RankedPerson[]> {
  if (people.length < 2) return people.map(contact => ({ contact }))
  const usage = await Promise.all(people.map(person => person.objectId && options.usage
    ? options.usage(person.objectId, { signal: context.signal }).catch(() => null) : Promise.resolve(null)))
  return ranked(people, context, usage)
}
/**
 * The same order at once, from what the page and the place's lexicon say (its `uses`, no network): what a list shows before rankPeople refines it (P17).
 * `role`: a template place („PM“), where whose function the role names counts before who the page already holds.
 */
export const rankNow = (people: readonly MarkdownContact[], context: { doc: PMNode; text: string; role?: boolean }): RankedPerson[] => people.length < 2 ? people.map(contact => ({ contact })) : ranked(people, context, [])
function ranked(people: readonly MarkdownContact[], context: { doc: PMNode; text: string; role?: boolean }, usage: readonly ({ here: number; total: number; related?: boolean } | null | undefined)[]): RankedPerson[] {
  const scores = contextOf(people, context)
  const scored = people.map((contact, index) => {
    const { here, field, recent: lately } = scores[index]!, used = usage[index]
    // Until the usage service answers, the lexicon's count of the place's pages naming the person stands for it.
    const inSpace = used ? used.here : knownUses.get(contact) ?? 0
    const reason = [here ? contactsStrings.rankOnPage : '', field ?? '',
      inSpace ? contactsStrings.rankInSpace : used?.total ? contactsStrings.rankUsed(used.total) : '', used?.related ? contactsStrings.rankRelated : ''].filter(Boolean).join(' · ')
    const head = context.role ? [field ? 1 : 0, here ? 1 : 0] : [here ? 1 : 0, field ? 1 : 0]
    return { contact, index, reason, key: [...head, inSpace, used?.related ? 1 : 0, lately, used?.total ?? 0] }
  })
  scored.sort(byKeys)
  return scored.map(({ contact, reason }) => (reason ? { contact, reason } : { contact }))
}
/** The paragraph around a position, the context words of a name. */
const textAround = (doc: PMNode, pos: number) => { const $pos = doc.resolve(Math.min(pos, doc.content.size)); return $pos.parent.textBetween(0, $pos.parent.content.size, ' ', ' ') }

/**
 * The people of a name as the editor's suggestion list at `rect`, ranked (first preselected; arrows, Enter or Tab, Esc), and the
 * „not that one“ row last; the caret stays in the text (namesLoader.ts routes the keys). Used for a written name and an unresolved chip.
 * `refine` (rankPeople's answer) reorders the open list only while the person has not moved in it: the network never moves a row away.
 */
export function offerPeople(editor: Editor, request: { rect: () => DOMRect; people: readonly RankedPerson[]; refine?: Promise<readonly RankedPerson[]>; pick: (person: MarkdownContact) => void; none?: { label: string; run: () => void } }): boolean {
  if (!request.people.length || !editor.isEditable) return false
  closeList(editor)
  const doc = editor.view.dom.ownerDocument
  let people = request.people, moved = false, drawing = true
  const list = createSuggestionList({ document: doc, label: contactsStrings.nameOfferLabel, testId: 'contact-name-offer', onActiveChange: () => { if (!drawing) moved = true }, onSelect: id => {
    closeList(editor)
    if (id === 'no') { request.none?.run(); editor.view.focus(); return }
    request.pick(people[Number(id)]!.contact)
  } })
  const draw = () => {
    drawing = true
    list.update([...people.map(({ contact, reason }, i) => ({ id: String(i), title: contactsStrings.nameOfferIs(displayName(contact)),
      description: [describePerson(contact), reason].filter(Boolean).join(' — ') || undefined, icon: 'svg:user', avatar: contact.internalUser?.photo ?? contact.photo })),
      ...request.none ? [{ id: 'no', title: request.none.label, icon: 'svg:x' }] : []])
    drawing = false
  }
  draw()
  const rect = () => { try { return request.rect() } catch { return editor.view.dom.getBoundingClientRect() } }
  const popup = createSuggestionPopup({ editor, content: list.element, contextElement: editor.view.dom, getReferenceRect: rect, onDismiss: () => closeList(editor) })
  const entry = { list, destroy() { popup.destroy(); list.destroy() } }
  openLists.set(editor, entry)
  void request.refine?.then(next => { if (openLists.get(editor) === entry && !moved && next.length === people.length) { people = next; draw() } }, () => undefined)
  return true
}

/** People of a name, read as a whole name in the linking engine's index (the people the typing and the underlines know), once the host's are in. */
const peopleNamed = async (editor: Editor, options: ContactsPluginOptions, name: string) => { await knownOf(options); return markdownRecognitionCandidates(editor, name, 'person') as readonly MarkdownContact[] }

/** An explicit pick in place of a written name, linked as every insert is; the name stays when they go back or it changed meanwhile. */
async function put(editor: Editor, options: ContactsPluginOptions, range: { from: number; to: number }, written: string, person: MarkdownContact) {
  let { from, to } = range
  const follow = ({ transaction }: { transaction: import('@tiptap/pm/state').Transaction }) => { from = transaction.mapping.map(from, 1); to = Math.max(from, transaction.mapping.map(to, -1)) }
  const abort = new AbortController(), stop = () => abort.abort()
  editor.on('transaction', follow); editor.on('destroy', stop)
  const anchor = () => { try { return posToDOMRect(editor.view, from, to) } catch { return editor.view.dom.getBoundingClientRect() } }
  const linked = person.unresolved ? person : await linkInserted(options, normalizeContact(person), { editor, anchor }, abort.signal).catch(() => null)
  editor.off('transaction', follow); editor.off('destroy', stop)
  if (editor.isDestroyed) return
  if (!linked || !editor.isEditable || editor.state.doc.textBetween(from, to, ' ', '￼') !== written) { editor.view.focus(); return }
  replaceWithContacts(editor, from, to, [inlineContactJson(linked)])
  pickedPerson(options, written, linked)
}

/** „Je to Jana Nováková?“ at an underlined name, ranked; „Není to on/ona“ (or „Nikdo z nich“) lets that occurrence be. */
export function offerName(editor: Editor, options: ContactsPluginOptions, hit: MarkdownRecognitionHit): boolean {
  const people = hit.candidates as readonly MarkdownContact[]
  if (!people.length || !editor.isEditable || editor.state.doc.textBetween(hit.from, hit.to, ' ', '￼') !== hit.text) return false
  const context = { doc: editor.state.doc, text: textAround(editor.state.doc, hit.from) }
  return offerPeople(editor, { rect: () => posToDOMRect(editor.view, hit.from, hit.to), people: rankNow(people, context),
    refine: rankPeople(options, people, { ...context, signal: new AbortController().signal }), pick: person => void put(editor, options, hit, hit.text, person),
    none: { label: people.length > 1 ? contactsStrings.nameOfferNone : contactsStrings.nameOfferNo, run: () => dismissMarkdownRecognition(editor, hit) } })
}

/** Sets a contact node's person (an unresolved chip picked), one undo step; with `options` the pick is told to the directory too. */
export function pickPerson(editor: Editor, pos: number, person: MarkdownContact, options?: ContactsPluginOptions) {
  const node = editor.state.doc.nodeAt(pos)
  if (!node || node.type.name !== 'contactInline' || !editor.isEditable) return
  const { unresolved: _, ...picked } = normalizeContact(person)
  const written = (node.attrs.data as { contacts?: MarkdownContact[] } | null)?.contacts?.[0]?.name ?? ''
  dispatchMarkdownHistoryOperation(editor, editor.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, data: { version: 1, contacts: [picked] } }))
  editor.view.focus()
  if (options) pickedPerson(options, written, picked); else rememberPick(picked)
}

/** The people an unresolved chip may be, ranked by the page's context. */
export async function unresolvedPeople(options: ContactsPluginOptions, editor: Editor, pos: number, name: string, signal: AbortSignal): Promise<RankedPerson[]> {
  return rankPeople(options, await peopleNamed(editor, options, name), { doc: editor.state.doc, text: textAround(editor.state.doc, pos), signal })
}

/**
 * An unresolved chip's people at the chip, open at once in the page's order and refined by usage (offerPeople); false when nobody of
 * that name is known (the caller then asks the host's picker).
 */
export async function offerUnresolved(options: ContactsPluginOptions, editor: Editor, request: { pos: number; name: string; rect: () => DOMRect; signal: AbortSignal; pick: (person: MarkdownContact) => void }): Promise<boolean> {
  const people = await peopleNamed(editor, options, request.name)
  if (request.signal.aborted || !people.length) return false
  const context = { doc: editor.state.doc, text: textAround(editor.state.doc, request.pos) }
  return offerPeople(editor, { rect: request.rect, people: rankNow(people, context), refine: rankPeople(options, people, { ...context, signal: request.signal }), pick: request.pick })
}

/**
 * The contact rows of the actions menu (Ctrl+Space, Alt+Enter, Ctrl+., right button): the people of an unresolved chip; the people of
 * a known name that is selected or under the caret (each one, in the page's order at once, and „Nevím, kdo z nich“ for several; an
 * underlined one also „Není to on/ona“); „Udělat kontakt…“ over a selected name.
 */
export function contactIntentions(options: ContactsPluginOptions, pickSelection?: (editor: Editor) => boolean): MarkdownIntentionProvider {
  return async ({ editor, empty, node, nodePos }) => {
    if (!editor.isEditable) return []
    const rows: MarkdownIntention[] = []
    const person = (contact: MarkdownContact, reason: string | undefined, label: string, run: () => void, priority: number): MarkdownIntention =>
      ({ id: `contacts.person.${priority}`, label, description: [describePerson(contact), reason].filter(Boolean).join(' — ') || undefined, icon: 'svg:user', group: 'contacts', priority, keywords: ['kontakt', 'osoba'], run })
    const unresolved = node?.type.name === 'contactInline' && nodePos !== undefined ? (node.attrs.data as { contacts: MarkdownContact[] }).contacts[0] : undefined
    if (unresolved?.unresolved) {
      const ranked = rankNow(await peopleNamed(editor, options, unresolved.name), { doc: editor.state.doc, text: textAround(editor.state.doc, nodePos!) })
      ranked.forEach(({ contact, reason }, i) => rows.push(person(contact, reason, contactsStrings.nameOfferIs(displayName(contact)), () => pickPerson(editor, nodePos!, contact, options), 10 + i)))
      return rows
    }
    // A linked person at the caret: „Umí…“ rates what they know (Mapa znalostí), in the host's rating at the chip.
    const linked = (node?.type.name === 'contactInline' || node?.type.name === 'contactCard') && nodePos !== undefined ? (node.attrs.data as { contacts: MarkdownContact[] }).contacts[0] : undefined
    if (linked?.objectId && options.rateSkills) rows.push({ id: 'contacts.skills', label: contactsStrings.intentionSkills, description: displayName(linked), icon: 'svg:lightbulb', group: 'contacts', priority: 20,
      keywords: ['umí', 'znalost', 'dovednost', 'skill'], run: () => { const anchor = editor.view.nodeDOM(nodePos!); if (anchor instanceof HTMLElement) options.rateSkills!(linked.objectId!, { anchor, signal: new AbortController().signal }) } })
    // The underlined name around the caret (the menu's word may carry a comma after the name).
    const caret = editor.state.selection.from
    const range = empty ? markdownRecognitionAt(editor.state, caret, 'person') : undefined
    const at = range ? { from: range.from, to: range.to } : !empty && selectedName(editor.state) ? { from: selectedName(editor.state)!.from, to: selectedName(editor.state)!.to } : undefined
    if (at) {
      const written = editor.state.doc.textBetween(at.from, at.to, ' ', '￼')
      const people = range ? range.candidates as readonly MarkdownContact[] : await peopleNamed(editor, options, written)
      const ranked = rankNow(people, { doc: editor.state.doc, text: textAround(editor.state.doc, at.from) })
      const place = (contact: MarkdownContact) => () => {
        if (editor.state.doc.textBetween(at.from, at.to, ' ', '￼') !== written) return
        if (contact.unresolved) { replaceWithContacts(editor, at.from, at.to, [inlineContactJson(contact)]); return }
        const anchor = () => { try { return posToDOMRect(editor.view, at.from, at.to) } catch { return editor.view.dom.getBoundingClientRect() } }
        void linkInserted(options, contact, { editor, anchor }, new AbortController().signal).then(linked => {
          if (!linked || editor.state.doc.textBetween(at.from, at.to, ' ', '￼') !== written) return
          replaceWithContacts(editor, at.from, at.to, [inlineContactJson(linked)])
          pickedPerson(options, written, linked)
        })
      }
      ranked.forEach(({ contact, reason }, i) => rows.push(person(contact, reason, contactsStrings.nameOfferIs(displayName(contact)), place(contact), 10 + i)))
      if (people.length > 1) rows.push({ id: 'contacts.unresolved', label: contactsStrings.intentionUnresolved, icon: 'svg:users', group: 'contacts', priority: 30, run: place(unresolvedContact(written)) })
      // An underlined name the person says is nobody of them stays plain text (as „Není to on/ona“ of a click's list).
      if (range && people.length) rows.push({ id: 'contacts.not', label: people.length > 1 ? contactsStrings.nameOfferNone : contactsStrings.nameOfferNo, icon: 'svg:x', group: 'contacts', priority: 35,
        run: () => dismissMarkdownRecognition(editor, at) })
    }
    if (!empty && pickSelection && selectedName(editor.state)) rows.push({ id: 'contacts.pick', label: contactsStrings.selectionLabel, description: contactsStrings.intentionPickDescription, icon: 'svg:user', group: 'contacts', priority: 40, keywords: ['kontakt', 'osoba'], run: () => { pickSelection(editor) } })
    return rows
  }
}
