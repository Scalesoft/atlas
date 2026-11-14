import { markdownDialect, markdownStrings, type MarkdownDialectYaml, type MarkdownDialectYamlPath } from '../../core'
import type { ContactsData, MarkdownContact } from './data'

/**
 * Markdown dialect forms of contacts (dialect spec §4.3): the block ```a-contact (an a-YAML sequence of contacts) and
 * the inline `[Name](a-contact:?name=…&phone=…&email=…)`. Keys in writing order; the plugin's normalizers validate.
 * Loads with the dialect.
 */
const CONTACT_KEYS = ['name', 'role', 'title', 'organization', 'department', 'username', 'phones', 'emails', 'website', 'address', 'photo', 'kind', 'directoryId', 'objectId', 'internalUser', 'unresolved'] as const
const USER_KEYS = ['id', 'name', 'photo'] as const
const input = () => markdownStrings.input
/** Contact fields the schema types as strings; their plain scalars read as written (`+420777123456`, `2026`). */
const TEXT_KEYS = new Set<unknown>(['name', 'role', 'title', 'organization', 'department', 'username', 'website', 'address', 'photo', 'directoryId', 'objectId'])
const isContactText = (path: MarkdownDialectYamlPath) => typeof path[0] === 'number' && (path.length === 2 ? TEXT_KEYS.has(path[1])
  : path.length === 3 && ((path[1] === 'phones' || path[1] === 'emails') ? typeof path[2] === 'number' : path[1] === 'internalUser' && (USER_KEYS as readonly unknown[]).includes(path[2])))

function ordered<K extends string>(value: Partial<Record<K, unknown>>, keys: readonly K[]): { [key: string]: MarkdownDialectYaml } {
  return Object.fromEntries(keys.filter(key => value[key] !== undefined).map(key => [key, value[key] as MarkdownDialectYaml]))
}
function known(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError(input().dialectInvalidShape)
  const unknownKey = Object.keys(value).find(key => !keys.includes(key))
  if (unknownKey) throw new TypeError(input().dialectUnknownKey(unknownKey))
  // An empty optional value (`title:`) is the same as no value.
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null))
}

export function contactsMarkdown(data: ContactsData): string {
  const { markdownDialectFence, writeMarkdownDialectYaml } = markdownDialect()
  return markdownDialectFence('a-contact', writeMarkdownDialectYaml(data.contacts.map(contact => ordered({
    ...contact, internalUser: contact.internalUser && ordered(contact.internalUser, USER_KEYS),
  }, CONTACT_KEYS))))
}

export function readContactsFence(element: HTMLElement): ContactsData {
  const contacts = markdownDialect().parseMarkdownDialectYaml(element.getAttribute('data-md-source') ?? '', isContactText)
  if (!Array.isArray(contacts)) throw new TypeError(input().dialectInvalidShape)
  return { version: 1, contacts: contacts.map(item => {
    const contact = known(item, CONTACT_KEYS)
    return (contact.internalUser === undefined ? contact : { ...contact, internalUser: known(contact.internalUser, USER_KEYS) }) as unknown as MarkdownContact
  }) }
}

/** Query keys of the inline form: lists repeat their singular key, the internal user's fields are dotted. */
const LINK_KEYS: Record<string, string> = { phones: 'phone', emails: 'email' }
export function contactInlineMarkdown(data: ContactsData): string {
  const contact = data.contacts[0]!
  const params: [string, string][] = []
  for (const key of CONTACT_KEYS) {
    const value = contact[key]
    if (value === undefined) continue
    if (Array.isArray(value)) value.forEach(item => params.push([LINK_KEYS[key]!, item]))
    else if (typeof value === 'object') USER_KEYS.forEach(field => { if (value[field] !== undefined) params.push([`internalUser.${field}`, value[field]!]) })
    else params.push([key, String(value)])
  }
  const { markdownDialectLabel, writeMarkdownDialectLink } = markdownDialect()
  return `[${markdownDialectLabel(contact.name)}](${writeMarkdownDialectLink('a-contact', '', params)})`
}

export function readContactLink(element: HTMLElement): ContactsData {
  const link = markdownDialect().parseMarkdownDialectLink(element.getAttribute('data-md-target') ?? '')
  if (link.name !== 'a-contact' || link.path) throw new TypeError(input().dialectInvalidLink)
  const contact: Record<string, unknown> = {}
  const user: Record<string, string> = {}
  const lists = Object.fromEntries(Object.entries(LINK_KEYS).map(([list, key]) => [key, list]))
  for (const [key, value] of link.params) {
    const field = key.startsWith('internalUser.') ? key.slice('internalUser.'.length) : undefined
    const target = field !== undefined ? user : contact
    const name = field ?? key
    if (lists[key]) { ((contact[lists[key]!] ??= []) as string[]).push(value); continue }
    const allowed = field !== undefined ? (USER_KEYS as readonly string[]).includes(field)
      : (CONTACT_KEYS as readonly string[]).includes(key) && key !== 'internalUser' && !(key in LINK_KEYS)
    if (!allowed) throw new TypeError(input().dialectUnknownKey(key))
    if (name in target) throw new TypeError(input().dialectYamlDuplicateKey(key))
    target[name] = value
  }
  if (Object.keys(user).length) contact.internalUser = user
  return { version: 1, contacts: [contact as unknown as MarkdownContact] }
}
