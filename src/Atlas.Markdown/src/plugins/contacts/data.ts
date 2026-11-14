import type { Editor } from '@tiptap/core'
import { markdownModuleUrl } from '../../core'
import { contactsStrings } from './strings'

export interface MarkdownContact {
  name: string
  /** Page-local role or note of this reference ("PM projektu"); never copied into the directory contact. One line. */
  role?: string
  organization?: string
  department?: string
  title?: string
  /** Login or account name, e.g. for a company directory or chat; shown on the card, not exported to vCard. */
  username?: string
  photo?: string
  website?: string
  address?: string
  phones?: string[]
  emails?: string[]
  /** Persisted snapshot; refresh is always an explicit user action. */
  directoryId?: string
  /** The directory contact object (`atlas.contact`) this snapshot came from, lower-case GUID; lets the host index where a contact is used. */
  objectId?: string
  /** Presentation preference only; never an authorization or presence assertion. Omitted means auto. */
  kind?: 'auto' | 'internal' | 'external'
  /** Explicit host-provided internal identity snapshot, independent of a generic directory entry. */
  internalUser?: MarkdownInternalContactUser
  /**
   * The name of several known people and nobody picked yet: the editor offers them (ranked by the page's context) and never links
   * or creates a directory contact by itself; picking one replaces the snapshot. Without objectId only.
   */
  unresolved?: true
}
/**
 * A template's place for a person (P15 „Struktura z šablon“): an unresolved contact whose name is its page role („PM“, „Čekáme na“);
 * inline Markdown `[PM](a-contact:?name=PM&role=PM&unresolved=true)`. It shows as the unresolved chip with the role as its hint, and picking a
 * person fills it, the role staying the page role. No node or field of its own: every reader of contacts (the server, Markdown, MCP) sees an
 * unresolved contact, which is never linked or indexed; filled it is just the contact chip, emptied by „Převést na text“ just the role as text.
 */
export const contactPlace = (role: string): MarkdownContact => ({ name: role, role, unresolved: true })
/** The role on this page, unless it only repeats the contact's own position (the subtitle already says it). */
export const contactRole = (contact: MarkdownContact): string | undefined => (contact.role && contact.role.trim().toLocaleLowerCase() !== contact.title?.trim().toLocaleLowerCase() ? contact.role : undefined)
export const isContactPlace = (contact: MarkdownContact | null | undefined): boolean => !!contact?.unresolved && !!contact.role && contact.role === contact.name
export interface MarkdownInternalContactUser { id: string; name?: string; photo?: string }
export interface ContactsData { version: 1; contacts: MarkdownContact[] }
export interface ContactDirectoryService {
  search(query: string, context: { signal: AbortSignal }): Promise<readonly MarkdownContact[]>
  /** What `search` would answer that the host already knows (its lexicon), synchronously: the `@` list's first rows at once (P17). */
  cached?(query: string): readonly MarkdownContact[]
  get(id: string, context: { signal: AbortSignal }): Promise<MarkdownContact | null>
  /** Match against the host application's authenticated internal directory. Null means no internal match. */
  resolveInternal?(contact: Readonly<MarkdownContact>, context: { signal: AbortSignal }): Promise<MarkdownInternalContactUser | null>
  /**
   * An explicit pick of `contact` after `query` was typed (`@MMu`, an underlined alias, a written name, an unresolved chip): the host
   * may teach the directory contact the query as an alias. Fire and forget; the pick stands whatever it does.
   */
  learn?(query: string, contact: MarkdownContact): void
}
export type LiveContact = { state: 'ok'; contact: MarkdownContact } | { state: 'hidden' | 'trashed' | 'missing' }
export interface ContactsPluginOptions {
  directory?: ContactDirectoryService
  /** The live directory contact behind a snapshot's objectId (host: POST /api/contacts/live, batched + cached). */
  live?: (objectId: string, context: { signal: AbortSignal }) => Promise<LiveContact>
  /**
   * Links a snapshot without objectId to the directory; resolves with the snapshot to store (with objectId) or null to leave it. Editable documents only.
   * With `insert` the person is putting the contact in now (`@`, the contact form, „Přidat z adresáře“), also one that has objectId: the host may ask
   * what a directory match needs (anchored at `insert.anchor`) and say that the document has the contact already; null then means the person went
   * back and nothing goes in, and an unlinked snapshot goes in as it is.
   */
  link?: (contact: MarkdownContact, context: { signal: AbortSignal; insert?: { editor: Editor; anchor: () => DOMRect } }) => Promise<MarkdownContact | null>
  /** Where the directory contact behind an objectId is changed; the contact form of a linked contact edits only the page's own role and display and links there. */
  directoryUrl?: (objectId: string) => string
  /** The places a linked contact is used (host: GET /api/usage/{id}), for the read-only card: title, where, and the address to open. Not used without it. */
  readerUsage?: (objectId: string, context: { signal: AbortSignal }) => Promise<{ places: readonly { title: string; detail?: string; url: string }[]; more: number } | null>
  /**
   * Edits a contact that has objectId where the page shows it, in place of the role-and-display form: the host's editor of the directory
   * contact beside the page's own role, username and display, anchored at `anchor`. `save` stores the page's snapshot right after the
   * current event, into the node's current data (also after a re-render), to the contact with this objectId; call it once, when the editor
   * closes, because storing repaints the node. `signal` aborts with the node. Returns the editor's cleanup.
   */
  editLinked?: (contact: MarkdownContact, context: { editor: Editor; anchor: HTMLElement; signal: AbortSignal; save: (contact: MarkdownContact) => void }) => () => void
  /** Asked before the contact manager's "Odebrat" removes a contact that has objectId (the host may show the last-usage dialog); false keeps it. */
  remove?: (contact: MarkdownContact, context: { signal: AbortSignal }) => Promise<boolean>
  /** Opt-in typing suggestions backed by this same directory. */
  suggestions?: boolean | { debounceMs?: number; limit?: number }
  /**
   * The host's person picker for the slash menu (`/kontakt`, `/kontakt jan kovarik`): it opens with the typed text as its
   * query and resolves with the picked person(s), or null when cancelled. The editor inserts the result where the slash was
   * typed (a card on an empty line, in the text otherwise) and returns focus to the text. Without it, `/kontakt <text>`
   * continues as the `@` suggestion of `directory`.
   */
  pick?: (request: ContactPickRequest) => Promise<MarkdownContact | readonly MarkdownContact[] | null | undefined>
  /**
   * The people whose full names the editor recognises in the text (names.ts), asked once per editable editor: a typed name offers
   * itself („Jana Nováková?“, Tab), a written one gets a dotted underline that asks „Je to …?“. The host caches it; the editor adds
   * the contacts the document holds. Taking a person goes through `link` with `insert`, as every insert does. Without it nothing is recognised.
   * A person may carry the directory's learned `aliases` („MMu“, „Mára“): those of three letters or more are underlined and offered like
   * the name, never made a chip by themselves; the snapshot never stores them.
   */
  known?: (context: { signal: AbortSignal }) => Promise<readonly (MarkdownContact & { aliases?: readonly string[] })[]>
  /**
   * How much a directory contact is used here (the page's space) and in all, for ranking the people of one name (an unresolved
   * contact, a written name, the actions menu). Asked only when a name is several people's; null: unknown. `related`: the person is
   * named on pages close in meaning to this one (the host's optional AI boost); it ranks after the space's use, never before the page.
   */
  usage?: (objectId: string, context: { signal: AbortSignal }) => Promise<{ here: number; total: number; related?: boolean } | null>
}
export interface ContactPickRequest {
  /** The text typed after the command, trimmed; empty for a bare `/kontakt`. */
  readonly query: string
  /** Whether the result goes into the text (true) or becomes a contact card. */
  readonly inline: boolean
  /** Aborts when the editor goes away; the picker should close. */
  readonly signal: AbortSignal
  /** Where the slash was typed (or the selected name is), to anchor the picker. */
  readonly anchor: () => DOMRect
  /** The editor asking, for a picker the host opens in the editor's popover. */
  readonly editor: Editor
}

function text(value: unknown, label: string, required = false): string {
  if (value == null && !required) return ''
  if (typeof value !== 'string' || value.length > 4096 || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(value)) throw new TypeError(contactsStrings.invalidField(label))
  if (required && !value.trim()) throw new TypeError(contactsStrings.requiredField(label))
  return value.trim()
}
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu
export function normalizeContact(value: unknown): MarkdownContact {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError(contactsStrings.invalidContact)
  const v = value as Record<string, unknown>
  const result: MarkdownContact = { name: text(v.name, contactsStrings.nameFieldLabel, true) }
  if (v.kind !== undefined) {
    if (v.kind !== 'auto' && v.kind !== 'internal' && v.kind !== 'external') throw new TypeError(contactsStrings.invalidDisplayKind)
    result.kind = v.kind
  }
  if (v.internalUser !== undefined) result.internalUser = normalizeInternalContactUser(v.internalUser)
  for (const key of ['organization', 'department', 'title', 'address', 'directoryId'] as const) {
    const s = text(v[key], key); if (s) result[key] = s
  }
  const role = text(v.role, contactsStrings.roleFieldLabel)
  if (role) {
    if (role.length > 400 || /[\r\n]/u.test(role)) throw new TypeError(contactsStrings.invalidField(contactsStrings.roleFieldLabel))
    result.role = role
  }
  const username = text(v.username, contactsStrings.usernameFieldLabel)
  if (username) {
    if (username.length > 256 || /[\r\n]/u.test(username)) throw new TypeError(contactsStrings.invalidField(contactsStrings.usernameFieldLabel))
    result.username = username
  }
  for (const key of ['photo', 'website'] as const) {
    const s = text(v[key], key)
    if (s) { if (/[\r\n]/u.test(s)) throw new TypeError(contactsStrings.addressNoNewline); result[key] = markdownModuleUrl(s) }
  }
  // An invalid id is dropped, not an error: the snapshot is still a valid contact.
  if (typeof v.objectId === 'string' && GUID.test(v.objectId.trim())) result.objectId = v.objectId.trim().toLowerCase()
  // `unresolved=true` in the inline Markdown form is text; a picked person (objectId) is resolved.
  else if (v.unresolved === true || v.unresolved === 'true') result.unresolved = true
  for (const key of ['phones', 'emails'] as const) {
    if (v[key] == null) continue
    if (!Array.isArray(v[key]) || v[key].length > 20) throw new TypeError(contactsStrings.tooManyPhonesEmails)
    const values = v[key].map(item => text(item, key, true))
    if (values.some(s => key === 'phones' ? !/^\+?[\d\s().\-#*]{1,80}$/u.test(s) || /[\r\n]/u.test(s) : !/^[^\s@<>?,;:]+@[^\s@<>?,;:]+\.[^\s@<>?,;:]+$/u.test(s))) {
      throw new TypeError(key === 'phones' ? contactsStrings.invalidPhones : contactsStrings.invalidEmails)
    }
    result[key] = [...new Set(values)]
  }
  return result
}
/** A contact the person puts in now, linked by the host (`options.link` with `insert`); null: they went back. A failed link keeps the snapshot, which the render links later. */
export function linkInserted(options: ContactsPluginOptions, contact: MarkdownContact, insert: { editor: Editor; anchor: () => DOMRect }, signal: AbortSignal): Promise<MarkdownContact | null> {
  if (!options.link) return Promise.resolve(contact)
  return Promise.resolve().then(() => options.link!(contact, { signal, insert })).then(linked => linked && normalizeContact(linked), () => contact)
}
export function normalizeInternalContactUser(value: unknown): MarkdownInternalContactUser {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError(contactsStrings.invalidInternalUser)
  const raw = value as Record<string, unknown>
  if (typeof raw.id !== 'string' || !raw.id.trim() || raw.id.length > 256 || /[\u0000-\u001f\u007f]/u.test(raw.id)) throw new TypeError(contactsStrings.invalidInternalUserId)
  const user: MarkdownInternalContactUser = { id: raw.id.trim() }
  const name = text(raw.name, contactsStrings.internalUserNameFieldLabel); if (name) user.name = name
  const photo = text(raw.photo, contactsStrings.internalUserPhotoFieldLabel)
  if (photo) { if (/[\r\n]/u.test(photo)) throw new TypeError(contactsStrings.addressNoNewline); user.photo = markdownModuleUrl(photo) }
  return user
}
export function normalizeContacts(value: unknown): ContactsData {
  if (!value || typeof value !== 'object') throw new TypeError(contactsStrings.invalidContactsList)
  const v = value as Partial<ContactsData>
  if (v.version !== 1 || !Array.isArray(v.contacts) || !v.contacts.length || v.contacts.length > 100) throw new TypeError(contactsStrings.invalidContactsCount)
  return { version: 1, contacts: v.contacts.map(normalizeContact) }
}
export function contactText(contact: MarkdownContact): string {
  return [contact.name, contactRole(contact), contact.title, contact.organization, contact.department, contact.username, ...(contact.phones ?? []), ...(contact.emails ?? []), contact.website, contact.address].filter(Boolean).join(' · ')
}

function escapeText(value: string): string { return value.replace(/\\/g, '\\\\').replace(/\r\n|\r|\n/g, '\\n').replace(/;/g, '\\;').replace(/,/g, '\\,') }
/** RFC 6350 §3.2: 75 octets, including continuation space, without splitting UTF-8 characters. */
function fold(value: string): string {
  const encoder = new TextEncoder()
  let line = ''; let bytes = 0; const lines: string[] = []
  for (const char of value) {
    const length = encoder.encode(char).length
    if (bytes + length > 75) { lines.push(line); line = ' '; bytes = 1 }
    line += char; bytes += length
  }
  lines.push(line); return lines.join('\r\n')
}
export function serializeVCard(input: MarkdownContact | readonly MarkdownContact[]): string {
  const contacts = Array.isArray(input) ? input : [input]
  return contacts.map(value => {
    const c = normalizeContact(value)
    const lines = ['BEGIN:VCARD', 'VERSION:4.0', `FN:${escapeText(c.name)}`]
    if (c.organization || c.department) lines.push(`ORG:${escapeText(c.organization ?? '')};${escapeText(c.department ?? '')}`)
    if (c.title) lines.push(`TITLE:${escapeText(c.title)}`)
    if (c.address) lines.push(`ADR:;;${escapeText(c.address)};;;;`)
    for (const phone of c.phones ?? []) lines.push(`TEL;VALUE=text:${escapeText(phone)}`)
    for (const email of c.emails ?? []) lines.push(`EMAIL:${escapeText(email)}`)
    // vCard URI properties require absolute URLs; local image/file references remain document-only.
    if (c.website && /^https?:\/\//i.test(c.website)) lines.push(`URL:${new URL(c.website).href}`)
    if (c.photo && /^https?:\/\//i.test(c.photo)) lines.push(`PHOTO:${new URL(c.photo).href}`)
    lines.push('END:VCARD')
    return lines.map(fold).join('\r\n') + '\r\n'
  }).join('')
}
