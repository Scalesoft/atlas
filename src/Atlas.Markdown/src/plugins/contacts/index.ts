import { defineMarkdownBlockModule, markdownChipDeleteKeys, defineMarkdownCommand, defineMarkdownEntityKind, registerMarkdownChipText, registerMarkdownDialectForm, type MarkdownAction, type MarkdownPlugin, type MarkdownPluginCommand } from '../../core'
import { Extension, type Editor } from '@tiptap/core'
import { contactsStrings } from './strings'
import { contactRole, contactText, isContactPlace, normalizeContact, normalizeContacts, type ContactsData, type ContactsPluginOptions, type MarkdownContact } from './data'
import { contactSuggestions } from './suggestions'
import { contactIntentions, contactNames } from './namesLoader'
import { pickInto } from './pick'

// „Převést na text“ (core chipText) leaves the name as written, no contact data.
registerMarkdownChipText('contactInline', node => (node.attrs.data as ContactsData | null)?.contacts?.[0]?.name ?? '')

/**
 * About the drawn height of a card list (style.css), for its stand-in (J8.1): each card 22 px of padding and border around its 36 px avatar
 * or its lines (the name, then the role, the subtitle and the details when there are any), 6 px between cards and „Stáhnout vše“ under several.
 */
export function estimateContactsHeight(data: ContactsData): number {
  const card = (contact: MarkdownContact) => 22 + Math.max(36, 21 + (contactRole(contact) ? 18 : 0) + ([contact.title, contact.organization, contact.department].some(Boolean) ? 19 : 0)
    + ((contact.phones?.length || contact.emails?.length || contact.username || contact.website || contact.address) ? 20.5 : 0))
  const count = data.contacts.length
  return Math.round(4 + data.contacts.reduce((sum, contact) => sum + card(contact), 0) + 6 * Math.max(0, count - 1) + (count > 1 ? 37 : 0))
}

// The ```a-contact and [](a-contact:…) forms load with the Markdown dialect.
const form = registerMarkdownDialectForm(() => import('./markdown'))

const defaults: ContactsData = { version: 1, contacts: [{ get name() { return contactsStrings.newContactName } }] }
function definitions(options: ContactsPluginOptions = {}, shared?: { block: MarkdownPluginCommand<ContactsData>; inline: MarkdownPluginCommand<ContactsData> }) {
  const block = defineMarkdownBlockModule({ id: 'contacts', name: 'contactCard', stubHeight: estimateContactsHeight, dataValidator: 'contacts', get label() { return contactsStrings.label }, get description() { return contactsStrings.slashDescription }, icon: 'users', keywords: ['kontakt', 'osoba', 'vizitka', 'telefon', 'email', 'clovek'], defaults,
    insertCommand: shared?.block, normalize: normalizeContacts, text: data => data.contacts.map(contactText).join('\n'),
    // Editor dialect: core marks a ```a-contact fence; data failing validation stays a code block.
    dialect: { tag: 'div[data-md-fence="a-contact"]', write: data => form().contactsMarkdown(data), read: element => form().readContactsFence(element) },
    load: async () => (await import('./runtime')).createContactsRuntime(options),
  })
  const inline = defineMarkdownBlockModule({ id: 'contact-inline', name: 'contactInline', dataValidator: 'contact', get label() { return contactsStrings.inlineLabel }, get description() { return contactsStrings.inlineDescription }, icon: 'user', keywords: ['zminka', 'mention', 'osoba', 'kontakt'], defaults,
    insertCommand: shared?.inline, inline: true, normalize(value) { const data = normalizeContacts(value); if (data.contacts.length !== 1) throw new TypeError(contactsStrings.inlineSingleContactOnly); return data },
    text: data => contactText(data.contacts[0]!), stubText: data => data.contacts[0]!.name,
    // Editor dialect: core marks an `[…](a-contact:?…)` link; data failing validation stays literal text.
    dialect: { tag: 'span[data-md-link="a-contact"]', write: data => form().contactInlineMarkdown(data), read: element => form().readContactLink(element) },
    load: async () => (await import('./runtime')).createContactsRuntime(options, true),
  })
  return { block, inline }
}
const commands = definitions()
export const insertContact = (contact: MarkdownContact) => commands.block.insert({ version: 1, contacts: [normalizeContact(contact)] })
export const insertInlineContact = (contact: MarkdownContact) => commands.inline.insert({ version: 1, contacts: [normalizeContact(contact)] })
export const contactNode = (contact: MarkdownContact | MarkdownContact[]) => commands.block.node({ version: 1, contacts: Array.isArray(contact) ? contact : [contact] })
export const inlineContactNode = (contact: MarkdownContact) => commands.inline.node({ version: 1, contacts: [contact] })
const contactJson = (inline: boolean, contact: MarkdownContact) => (inline ? commands.inline : commands.block).node({ version: 1, contacts: [contact] })
/** `contacts.pick` (pick.ts `pickInto`): `{ query, inline }` from the menus, `{ selection: true }` for „Udělat kontakt“ over a selected name. */
const pickCommand = (options: ContactsPluginOptions) =>
  defineMarkdownCommand<{ query?: string; inline?: boolean; selection?: boolean }>({ id: 'contacts.pick', apply: args => pickInto(options, contactJson, args) })

/**
 * Tab from the text to the next empty place of a template (data.ts isContactPlace), selected: Enter or F2 then picks its person. Lists
 * and tables keep their Tab (they run first); with no place after the caret Tab is not taken.
 */
const placeTab = Extension.create({ name: 'contactPlaceTab', priority: 50, addKeyboardShortcuts: () => ({ Tab: ({ editor }) => {
  if (!editor.isEditable) return false
  const { doc, selection } = editor.state
  let at = -1
  doc.nodesBetween(selection.to, doc.content.size, (node, pos) => {
    if (at < 0 && pos >= selection.to && node.type.name === 'contactInline' && isContactPlace((node.attrs.data as ContactsData | null)?.contacts?.[0])) at = pos
    return at < 0
  })
  return at >= 0 && editor.commands.setNodeSelection(at)
} }) })

export function createContactsPlugin(options: ContactsPluginOptions = {}): MarkdownPlugin {
  const { block, inline } = definitions(options, { block: commands.block.insert, inline: commands.inline.insert })
  const pick = pickCommand(options)
  const searchable = !!options.pick || !!(options.directory && options.suggestions)
  // With a picker every insert asks for the person; without one the text insert and a typed name go to the `@` search.
  // Only the card action takes the typed name (one row for `/kontakt jan`); where it lands follows the line.
  const picking = (action: MarkdownAction, withParameter: boolean): MarkdownAction => {
    if (!searchable) return action
    const inText = action.id === 'contact-inline.insert' ? true : undefined
    return { ...action, ...(options.pick || inText ? { command: pick({ inline: inText }) } : {}),
      ...(withParameter && !inText ? { parameter: { label: contactsStrings.pickLabel, command: (text: string) => pick({ query: text, inline: inText }) } } : {}) }
  }
  return { id: 'contacts', apiVersion: 1, entities: [defineMarkdownEntityKind({ kind: 'person', inline: 'contactInline' })], schemaVersion: 2, migrations: [{ from: 1, to: 2, migrate: document => ({ ...document }) }], profiles: ['rich'], create(context) {
    const instances = [block.createPlugin().create(context), inline.createPlugin().create(context)]
    const pickSelection = searchable ? (editor: Editor) => editor.commands.command(pickInto(options, contactJson, { selection: true })) : undefined
    return {
      extensions: [...instances.flatMap(instance => instance.extensions ?? []), ...(options.suggestions && options.directory ? [contactSuggestions(options)] : []),
        // The people's names in the text (core recognition, namesLoader.ts): from the host's known people or the place's lexicon.
        contactNames({ options }), placeTab, markdownChipDeleteKeys('contacts', ['contactInline'])],
      // The contact rows of the actions menu (core intentions: Ctrl+Space, Ctrl+., right button): the people of a name, an unresolved chip's people, „Udělat kontakt“.
      intentions: contactIntentions(options, pickSelection),
      commands: [...instances.flatMap(instance => instance.commands ?? []), ...(searchable ? [pick] : [])],
      actions: { slash: instances.flatMap(instance => instance.actions?.slash ?? []).map(action => picking(action, true)),
        toolbar: instances.flatMap(instance => instance.actions?.toolbar ?? []).map(action => picking(action, false)),
        // „Udělat kontakt“ in the bubble over a selected name: the picker with that name, the person in its place.
        ...(searchable && { selection: [{ id: 'contacts.from-selection', label: contactsStrings.selectionLabel, icon: 'svg:user', hideWhenDisabled: true, command: pick({ selection: true }) }] }) },
      markdownLossy: document => instances.some(instance => instance.markdownLossy?.(document)),
      onRender(context) { const cleanups = instances.map(instance => instance.onRender?.(context)); return () => cleanups.reverse().forEach(cleanup => cleanup?.()) },
    }
  } }
}
export { contactPlace, isContactPlace, normalizeContact, normalizeContacts, normalizeInternalContactUser, serializeVCard } from './data'
export type { ContactsData, MarkdownContact, MarkdownInternalContactUser, ContactsPluginOptions, ContactDirectoryService, ContactPickRequest, LiveContact } from './data'
