import { createIconElement, createMarkdownChip, createSuggestionField, downloadMarkdownFile, nameKey, openMarkdownContextPopover, openMarkdownModuleForm, type MarkdownChip, type MarkdownChipPart, type MarkdownModuleContext, type MarkdownModuleRuntime } from '../../core'
import { contactsStrings } from './runtimeStrings'
import { contactQr, contactRole, isContactPlace, linkInserted, normalizeContact, normalizeContacts, normalizeInternalContactUser, serializeVCard, type LiveContact, type ContactsData, type ContactsPluginOptions, type MarkdownContact, type MarkdownInternalContactUser } from './data'
import styles from './style.css?inline'
import { bindMarkdownChipKeys, bindMarkdownChipPreview, markdownUserColor } from '../../core'
import { openContactReader } from './readerPopover'
import { offerPeople, offerUnresolved, pickPerson, rankNow, unresolvedPeople, type RankedPerson } from './names'
import { knownOf } from './namesLoader'

/** A pointer resting this long on a person opens their card (the chip preview waits as long); leaving it and the card this long closes it. */
const HOVER_OPEN_MS = 350, HOVER_CLOSE_MS = 300

/**
 * An unresolved chip's people at the chip (names.ts offerUnresolved), the most likely first; picking one makes the chip that person.
 * Nobody of that name known (or the known people not loaded): the host's person picker with the name, so a click always answers.
 */
function chooseUnresolved(context: Context, options: ContactsPluginOptions, anchor: HTMLElement): () => void {
  const editor = context.editor, contact = context.data.contacts[0]
  if (!editor || !contact?.unresolved || !editable(context)) return () => {}
  const abort = new AbortController()
  const rect = () => (anchor.isConnected ? anchor : context.container).getBoundingClientRect()
  // A place keeps its role on the person picked into it.
  const place = isContactPlace(contact)
  const pick = (person: MarkdownContact) => { const position = context.getPos?.(); if (position !== undefined && editable(context)) pickPerson(editor, position, place ? { ...person, role: contact.role } : person, options) }
  const picker = () => {
    if (!options.pick) { editor.view.focus(); return }
    void Promise.resolve().then(() => options.pick!({ query: place ? '' : contact.name, inline: true, signal: abort.signal, anchor: rect, editor })).then(result => {
      const person = Array.isArray(result) ? result[0] : result
      if (person && !abort.signal.aborted) pick(person as MarkdownContact); else if (!editor.isDestroyed) editor.view.focus()
    }).catch(() => { if (!editor.isDestroyed) editor.view.focus() })
  }
  // A place offers the known people at once, those its role names first („PM“: the project managers; names.ts rankNow), and „Někdo jiný…“.
  const offered = place
    ? knownOf(options).then(known => !abort.signal.aborted && offerPeople(editor, { rect, pick, none: options.pick ? { label: contactsStrings.placeSomeoneElse, run: picker } : undefined,
      people: rankNow(known, { doc: editor.state.doc, text: contact.role!, role: true }).slice(0, PLACE_OFFER) }))
    : offerUnresolved(options, editor, { pos: context.getPos?.() ?? 0, name: contact.name, rect, signal: abort.signal, pick })
  void offered.catch(() => false).then(offered => {
    if (abort.signal.aborted || !editable(context)) return
    // The keys go to the list through the editor (namesLoader.ts): a click left the focus on the chip's button, whose Enter would reopen it.
    if (offered) editor.view.focus(); else picker()
  })
  return () => abort.abort()
}
/** How many known people a place offers before „Někdo jiný…“. */
const PLACE_OFFER = 8
/** „nejspíš Modelsoft · nebo Example“: the most likely person stands out, the others follow quieter. */
function unresolvedDetail(people: readonly RankedPerson[]): MarkdownChipPart[] | undefined {
  // One short word each (the organization, else the function, department or e-mail domain): the chip stays a chip in the sentence.
  // The name is no detail (every one of them has it); nothing that tells them apart leaves the chip's title to say how many there are.
  const who = people.map(({ contact }) => contact.organization || contact.title || contact.department || contact.emails?.[0]?.split('@')[1] || contactsStrings.unresolvedNoDetail)
  if (who.length > 1 && new Set(who).size < 2) return undefined
  return who.length ? [{ text: contactsStrings.unresolvedLikely(who[0]!), className: 'md-contact-likely' }, ...who.slice(1, 3).map(text => ({ text: contactsStrings.unresolvedOthers(text) }))] : undefined
}

type Context = MarkdownModuleContext<ContactsData>
const kindLabels: Record<'auto' | 'internal' | 'external', string> = {
  get auto() { return contactsStrings.kindAuto }, get internal() { return contactsStrings.kindInternal }, get external() { return contactsStrings.kindExternal },
}
function editable(context: Context): boolean { return !!context.update && !!context.editor?.isEditable && !context.signal.aborted }
function button(parent: HTMLElement, label: string, run: () => void): HTMLButtonElement {
  const node = parent.ownerDocument.createElement('button'); node.type = 'button'; node.textContent = label; node.onclick = run; parent.append(node); return node
}
function avatar(doc: Document, name: string, photo?: string): HTMLElement {
  const element = doc.createElement('span'); element.className = 'md-contact-avatar'; element.setAttribute('aria-hidden', 'true')
  const parts = name.trim().split(/\s+/).filter(Boolean)
  element.textContent = (parts.length > 1 ? `${parts[0]![0]}${parts[parts.length - 1]![0]}` : (parts[0] ?? '?').slice(0, 2)).toLocaleUpperCase()
  // A company's logo or a person's photo (the host keeps it a small square): one lazy image over the initials, which stay when it fails.
  if (photo) { const img = doc.createElement('img'); img.src = photo; img.alt = ''; img.loading = 'lazy'; img.decoding = 'async'; img.className = 'md-contact-photo'; element.append(img); img.onerror = () => img.remove() }
  return element
}
/** P8 (same names told apart): the names of the document that more than one person (objectId) has, folded; once per document snapshot. */
const twinsByDocument = new WeakMap<object, Set<string>>()
function twinNames(document: object): Set<string> {
  let twins = twinsByDocument.get(document)
  if (twins) return twins
  const people = new Map<string, Set<string>>()
  const visit = (node: { type?: string; attrs?: { data?: Partial<ContactsData> | null }; content?: readonly unknown[] }) => {
    if (node.type === 'contactInline' || node.type === 'contactCard') {
      for (const { name, objectId } of node.attrs?.data?.contacts ?? []) if (name && objectId) { const key = nameKey(name); people.set(key, (people.get(key) ?? new Set()).add(objectId)) }
    }
    node.content?.forEach(child => visit(child as typeof node))
  }
  visit(document as Parameters<typeof visit>[0])
  twins = new Set([...people].filter(([, ids]) => ids.size > 1).map(([key]) => key))
  twinsByDocument.set(document, twins)
  return twins
}
/** What tells a person apart from another of the same name in the document: one short word, as an unresolved chip's candidates say it. */
function twinDetail(context: Context, contact: MarkdownContact): MarkdownChipPart[] | undefined {
  if (!contact.objectId || !twinNames(context.document).has(nameKey(contact.name))) return undefined
  const which = contact.organization || contact.title || contact.department || contact.emails?.[0]
  return which ? [{ text: which }] : undefined
}
/** Labelled, because a bare login next to the e-mails reads as one more address. */
function appendUsername(details: HTMLElement, contact: MarkdownContact): void {
  if (!contact.username) return
  const doc = details.ownerDocument, element = doc.createElement('span'); element.className = 'md-contact-username'
  const label = doc.createElement('span'); label.className = 'md-contact-username-label'; label.textContent = `${contactsStrings.fieldUsername}:`
  const value = doc.createElement('span'); value.className = 'md-contact-username-value'; value.textContent = contact.username
  element.append(label, ' ', value); details.append(element)
}
/** What is shown for a snapshot: the live directory fields (the page keeps its own role, kind and internal identity), the snapshot when hidden, or just the name when the contact is gone. */
function shown(snapshot: MarkdownContact, live: LiveContact): { contact: MarkdownContact; unavailable: boolean } {
  const local = { role: snapshot.role, kind: snapshot.kind, internalUser: snapshot.internalUser, objectId: snapshot.objectId }
  if (live.state === 'ok') return { contact: { ...live.contact, username: live.contact.username ?? snapshot.username, directoryId: snapshot.directoryId, ...local }, unavailable: false }
  if (live.state === 'hidden') return { contact: snapshot, unavailable: false }
  return { contact: { name: snapshot.name, ...local }, unavailable: true }
}
/** Asks the host for the live contact; errors and aborts keep the snapshot on screen. */
function fetchLive(context: Context, options: ContactsPluginOptions, snapshot: MarkdownContact, signal: AbortSignal, apply: (view: ReturnType<typeof shown>) => void): void {
  if (!snapshot.objectId || !options.live || signal.aborted) return
  void Promise.resolve().then(() => options.live!(snapshot.objectId!, { signal })).then(live => {
    if (signal.aborted || !live) return
    apply(shown(snapshot, live))
    if (live.state === 'ok' && live.contact.objectId) followMerge(context, snapshot.objectId!, live.contact.objectId)
  }).catch(() => { /* Keep the snapshot. */ })
}
/**
 * A merged contact answers with its target: an editor writes the target's objectId into the node, outside undo (nobody's edit),
 * also in a live session, so the page stops naming the merged contact and the merged one can be purged.
 */
function followMerge(context: Context, from: string, to: string): void {
  const editor = context.editor, position = context.getPos?.()
  if (to.toLowerCase() === from.toLowerCase() || !editor || position === undefined || !editable(context)) return
  const node = editor.state.doc.nodeAt(position), data = node?.attrs.data as ContactsData | undefined
  if (!node || !data?.contacts?.some(contact => contact.objectId === from)) return
  const contacts = data.contacts.map(contact => (contact.objectId === from ? { ...contact, objectId: to } : contact))
  editor.view.dispatch(editor.state.tr.setNodeMarkup(position, undefined, { ...node.attrs, data: normalizeContacts({ version: 1, contacts }) }).setMeta('addToHistory', false))
}
function appendRole(parent: HTMLElement, contact: MarkdownContact, unavailable: boolean, tag: 'span' | 'p' = 'span'): void {
  const doc = parent.ownerDocument
  const shown = contactRole(contact)
  if (shown) { const role = doc.createElement(tag); role.className = 'md-contact-role'; role.textContent = shown; parent.append(role) }
  if (unavailable) { const note = doc.createElement(tag); note.className = 'md-contact-unavailable'; note.textContent = contactsStrings.unavailable; parent.append(note) }
}
/**
 * The card of a contact (a reader's click on a chip or a card, anyone's resting pointer on a chip): the person with their photo, what they
 * do and where, mail and call at hand, each phone, e-mail, website and address on its own row with a copy button, where it is used, the
 * business card (the .vcf to download and, with the host's `qrCode`, its QR code unfolded in the card) and the directory.
 */
function fillContactReader(panel: HTMLElement, contact: MarkdownContact, identity: MarkdownInternalContactUser | undefined, unavailable: boolean, options: ContactsPluginOptions, download: (contacts: readonly MarkdownContact[]) => void, signal: AbortSignal, rate?: () => void): void {
  const doc = panel.ownerDocument
  const header = doc.createElement('div'); header.className = 'md-contact-reader-profile'
  header.append(avatar(doc, contact.name, contact.photo ?? identity?.photo))
  const info = doc.createElement('div'), name = doc.createElement('strong'); name.textContent = contact.name; info.append(name)
  const line = (text: string | undefined, className: string) => { if (!text) return; const element = doc.createElement('p'); element.className = className; element.textContent = text; info.append(element) }
  line(contact.title, 'md-contact-reader-title'); line([contact.organization, contact.department].filter(Boolean).join(' · '), 'md-contact-reader-org')
  appendRole(info, contact, unavailable, 'p'); header.append(info); panel.append(header)
  if (!unavailable && contact.objectId && options.readerSkills) skillsOf(panel, contact.objectId, options, signal, rate)
  const email = contact.emails?.[0], phone = contact.phones?.[0]
  const tel = (value: string) => `tel:${value.replace(/[\s().-]/g, '').replace(/#/g, '%23')}`
  if (!unavailable && (email || phone)) {
    const quick = doc.createElement('div'); quick.className = 'md-contact-quick'; panel.append(quick)
    const action = (icon: 'mail' | 'phone', label: string, url: string) => { const element = doc.createElement('a'); element.href = url; element.append(createIconElement(doc, icon, 15), label); quick.append(element) }
    if (email) action('mail', contactsStrings.sendEmail, `mailto:${encodeURIComponent(email)}`)
    if (phone) action('phone', contactsStrings.call, tel(phone))
  }
  const details = doc.createElement('div'); details.className = 'md-contact-details'; panel.append(details)
  const link = (label: string, url: string, parent: HTMLElement = details) => { const element = doc.createElement('a'); element.textContent = label; element.href = url; parent.append(element); return element }
  // A value, its icon and its copy button share a row.
  const row = (icon: 'phone' | 'mail' | 'globe' | 'map-pin') => { const element = doc.createElement('span'); element.className = 'md-contact-row'; element.append(createIconElement(doc, icon, 15)); details.append(element); return element }
  // Each value can be copied where it is read (a reader's card has nothing else to do with it).
  const copy = (value: string, parent: HTMLElement) => {
    // The label stays; the title says it was copied for a moment.
    const copyButton = button(parent, '', () => { void doc.defaultView?.navigator.clipboard?.writeText(value).then(() => { copyButton.title = contactsStrings.copied; setTimeout(() => { copyButton.title = contactsStrings.copyValue(value) }, 1500) }).catch(() => { copyButton.title = contactsStrings.copyFailed }) })
    copyButton.className = 'md-contact-copy'; copyButton.title = contactsStrings.copyValue(value); copyButton.setAttribute('aria-label', contactsStrings.copyValue(value)); copyButton.append(createIconElement(doc, 'copy', 14))
  }
  for (const value of contact.phones ?? []) { const element = row('phone'); link(value, tel(value), element); copy(value, element) }
  for (const value of contact.emails ?? []) { const element = row('mail'); link(value, `mailto:${encodeURIComponent(value)}`, element); copy(value, element) }
  appendUsername(details, contact)
  if (contact.website) link(contact.website.replace(/^https?:\/\//i, '').replace(/\/$/, ''), contact.website, row('globe')).setAttribute('aria-label', `${contactsStrings.web}: ${contact.website}`)
  if (contact.address) { const element = row('map-pin'), address = doc.createElement('span'); address.className = 'md-contact-address'; address.textContent = contact.address; element.append(address); copy(contact.address, element) }
  if (unavailable) return
  if (contact.objectId && options.readerUsage) usedOn(panel, contact.objectId, options, signal)
  businessCard(panel, contact, options, download)
  if (contact.objectId && options.directoryUrl) { const footer = doc.createElement('div'); footer.className = 'md-contact-reader-footer'; panel.append(footer); link(contactsStrings.openInDirectory, options.directoryUrl(contact.objectId), footer) }
}
/**
 * „Vizitka“: the contact for a phone, two ways side by side: the .vcf to download, or its QR code unfolded right in the card (drawn on the
 * first unfolding by the host's `qrCode`; contactQr keeps it readable). The code is black on white in any theme: a phone reads nothing else.
 */
function businessCard(panel: HTMLElement, contact: MarkdownContact, options: ContactsPluginOptions, download: (contacts: readonly MarkdownContact[]) => void): void {
  const doc = panel.ownerDocument, actions = doc.createElement('div'); actions.className = 'md-contact-share'; panel.append(actions)
  const save = button(actions, '', () => download([contact])); save.className = 'md-contact-download'; save.append(createIconElement(doc, 'download', 15), contactsStrings.downloadCard)
  save.setAttribute('aria-label', contactsStrings.downloadCardAriaLabel(contact.name)); save.title = contactsStrings.downloadCardTitle
  if (!options.qrCode) return
  const region = doc.createElement('div'); region.className = 'md-contact-qr'; region.id = `md-contact-qr-${Math.random().toString(36).slice(2, 8)}`; region.inert = true
  const inner = doc.createElement('div'); inner.className = 'md-contact-qr-inner'; region.append(inner); panel.append(region)
  const toggle = button(actions, '', () => {
    const open = toggle.getAttribute('aria-expanded') !== 'true'
    toggle.setAttribute('aria-expanded', String(open)); region.dataset.open = String(open); region.inert = !open
    // The unfolded code is brought into the card's view once it has its height (a card at the bottom of the screen scrolls).
    if (open) region.addEventListener('transitionend', () => inner.scrollIntoView({ block: 'nearest' }), { once: true })
    if (!open || inner.childElementCount) return
    const code = doc.createElement('div'); code.className = 'md-contact-qr-code'; code.setAttribute('role', 'img'); code.setAttribute('aria-label', contactsStrings.qrCodeLabel(contact.name))
    const hint = doc.createElement('p'); hint.className = 'md-contact-qr-hint'; hint.textContent = contactsStrings.qrHint
    inner.append(code, hint)
    void contactQr(contact, options.qrCode!).then(drawn => {
      // The host's own drawing of the code (rects and a path, no text of the contact in it).
      code.innerHTML = drawn.svg
      if (drawn.omitted.length) hint.textContent = `${contactsStrings.qrHint} ${contactsStrings.qrOmitted(drawn.omitted.map(field => (field === 'address' ? contactsStrings.fieldAddress : contactsStrings.web)).join(', '))}`
    }).catch(() => { hint.textContent = contactsStrings.qrFailed })
  })
  toggle.className = 'md-contact-download md-contact-qr-toggle'; toggle.append(createIconElement(doc, 'qr-code', 15), contactsStrings.qrCode)
  toggle.setAttribute('aria-expanded', 'false'); toggle.setAttribute('aria-controls', region.id)
}
/** „Umí“ in the reader card, right under the name, filled when the host answers; hidden until then, nothing for a person without topics. */
function skillsOf(panel: HTMLElement, objectId: string, options: ContactsPluginOptions, signal: AbortSignal, rate?: () => void): void {
  const doc = panel.ownerDocument, section = doc.createElement('div'); section.className = 'md-contact-skills'; section.hidden = true; panel.append(section)
  void Promise.resolve().then(() => options.readerSkills!(objectId, { signal })).then(answer => {
    if (signal.aborted || !answer || !section.isConnected || !answer.skills.length && !rate) return
    const head = doc.createElement('div'); head.className = 'md-contact-skills-head'
    const heading = doc.createElement('strong'); heading.textContent = contactsStrings.skills; head.append(heading)
    if (rate) {
      const open = button(head, answer.skills.length ? contactsStrings.skillsRate : contactsStrings.skillsAdd, rate)
      open.className = 'md-contact-skills-rate'; open.setAttribute('aria-haspopup', 'dialog')
    }
    const list = doc.createElement('div'); list.className = 'md-contact-skill-list'
    for (const skill of answer.skills) {
      const chip = doc.createElement('a'); chip.className = 'md-contact-skill'; chip.href = skill.url
      const level = Math.max(0, Math.min(5, Math.round(skill.level ?? 0)))
      chip.setAttribute('aria-label', skill.level === null ? skill.name : contactsStrings.skillLevel(skill.name, level))
      if (skill.detail) chip.title = skill.detail
      const name = doc.createElement('span'); name.textContent = skill.name; chip.append(name)
      if (skill.level !== null) {
        const dots = doc.createElement('span'); dots.className = 'md-contact-skill-level'; dots.setAttribute('aria-hidden', 'true')
        for (let index = 1; index <= 5; index++) { const dot = doc.createElement('i'); if (index <= level) dot.className = 'on'; dots.append(dot) }
        chip.append(dots)
      }
      list.append(chip)
    }
    if (answer.more > 0) {
      const more = doc.createElement(answer.url ? 'a' : 'span'); more.className = 'md-contact-skill-more'; more.textContent = contactsStrings.skillsMore(answer.more)
      if (answer.url) (more as HTMLAnchorElement).href = answer.url
      list.append(more)
    }
    section.append(head)
    if (list.childElementCount) section.append(list)
    section.hidden = false
  }).catch(() => { /* The card stays without the topics. */ })
}
/** „Použito na“ in the reader card: the places that use the contact, filled when the host answers; nothing is shown for an unknown or unreadable one. */
function usedOn(panel: HTMLElement, objectId: string, options: ContactsPluginOptions, signal: AbortSignal): void {
  const doc = panel.ownerDocument, section = doc.createElement('div'); section.className = 'md-contact-used'; section.hidden = true; panel.append(section)
  void Promise.resolve().then(() => options.readerUsage!(objectId, { signal })).then(answer => {
    if (signal.aborted || !answer || !answer.places.length || !section.isConnected) return
    const heading = doc.createElement('strong'); heading.textContent = contactsStrings.usedOn; section.append(heading)
    for (const place of answer.places) { const row = doc.createElement('a'); row.href = place.url; row.textContent = place.detail ? `${place.title} · ${place.detail}` : place.title; section.append(row) }
    if (answer.more > 0) { const more = doc.createElement('span'); more.textContent = contactsStrings.usedOnMore(answer.more); section.append(more) }
    section.hidden = false
  }).catch(() => { /* The card stays without the list. */ })
}
function renderInlineContact(context: Context, snapshot: MarkdownContact, parent: HTMLElement, options: ContactsPluginOptions, download: (contacts: readonly MarkdownContact[]) => void): () => void {
  const doc = parent.ownerDocument, card = doc.createElement('span'); card.className = 'md-contact-card'; parent.append(card)
  let contact = snapshot, unavailable = false, identity = contact.internalUser, stopped = false, closeReader: (() => void) | undefined
  // The hover card (Teams-like): a fine pointer resting on the chip opens the person's card without taking the focus; it stays while the
  // pointer is on the chip or the card and goes a moment after it leaves both. Using it (a press or the focus inside) or a reader's click
  // keeps it like a clicked one, until Escape or a click outside; a writer's click closes it and edits. A touch has no hover: a tap opens.
  let hovered = false, hoverTimer: ReturnType<typeof setTimeout> | undefined
  const readerCard = (hover: boolean) => openContactReader(context, trigger, contactsStrings.readerTitle(contact.name), panel => {
    if (hover) {
      const surface = panel.parentElement!
      surface.addEventListener('pointerenter', () => clearTimeout(hoverTimer))
      surface.addEventListener('pointerleave', leave)
      surface.addEventListener('pointerdown', () => { hovered = false }, true)
      surface.addEventListener('focusin', () => { hovered = false })
    }
    fillContactReader(panel, contact, identity, unavailable, options, download, context.signal,
      options.rateSkills && contact.objectId ? () => { closeReader?.(); options.rateSkills!(contact.objectId!, { anchor: trigger, signal: context.signal }) } : undefined)
    return () => { hovered = false }
  }, hover)
  const enter = (event: PointerEvent) => {
    clearTimeout(hoverTimer)
    if (hovered || (event.pointerType !== 'mouse' && event.pointerType !== 'pen') || contact.unresolved || isContactPlace(contact)) return
    // Never over a panel that is open (an edit form, another card): one panel at a time.
    hoverTimer = setTimeout(() => {
      if (!trigger.isConnected || stopped || trigger.getAttribute('aria-expanded') === 'true' || doc.querySelector('.md-context-popover')) return
      hovered = true; closeReader = readerCard(true)
    }, HOVER_OPEN_MS)
  }
  function leave() { clearTimeout(hoverTimer); if (hovered) hoverTimer = setTimeout(() => { if (hovered) closeReader?.() }, HOVER_CLOSE_MS) }
  const trigger = button(card, '', () => {
    if (hovered) {
      hovered = false; clearTimeout(hoverTimer)
      if (!editable(context) || contact.unresolved) return
      closeReader?.()
    }
    // A second click closes what it opened. The edit form's goes through the same open again: the shared toggle applies a changed form (D3).
    if (trigger.getAttribute('aria-expanded') === 'true' && !(editable(context) && !contact.unresolved)) { closeReader?.(); return }
    if (context.editor?.isEditable && context.getPos) { const position = context.getPos(); if (position !== undefined) context.editor.commands.setNodeSelection(position) }
    // A writer's click edits the contact (picks the person of an unresolved one); the reader's panel is for reading.
    if (editable(context) && contact.unresolved) { closeReader = chooseUnresolved(context, options, trigger); return }
    if (editable(context)) { closeReader = editContact({ ...context, anchor: trigger }, 0, options); return }
    closeReader = readerCard(false)
  })
  trigger.addEventListener('pointerenter', enter); trigger.addEventListener('pointerleave', leave)
  trigger.className = 'md-contact-name'; trigger.setAttribute('aria-expanded', 'false'); trigger.setAttribute('aria-haspopup', 'dialog'); trigger.setAttribute('aria-label', contactsStrings.contactDetailsAriaLabel(contact.name))
  if (context.editor && context.getPos) context.signal.addEventListener('abort', bindMarkdownChipKeys(trigger, context.editor, context.getPos), { once: true })
  context.signal.addEventListener('abort', bindMarkdownChipPreview(trigger, () => ({ title: contact.name, lines: [[contact.title, contact.organization].filter(Boolean).join(' · '), contactRole(contact), unavailable ? contactsStrings.unavailable : ''].filter((line): line is string => !!line) }), context.editor && context.getPos ? { editor: context.editor, getPos: context.getPos } : undefined, { pointer: false }), { once: true })
  let chip: MarkdownChip | undefined, detail: MarkdownChipPart[] | undefined, people = 0
  const paint = () => {
    if (contact.objectId) { card.dataset.objectId = contact.objectId; card.dataset.contactName = contact.name }
    const unresolved = !!contact.unresolved, place = isContactPlace(contact)
    const internal = !unresolved && (contact.kind === 'internal' || (contact.kind !== 'external' && !!identity))
    card.classList.toggle('md-contact-internal', internal || unresolved)
    card.dataset.contactKind = unresolved ? 'unresolved' : internal ? 'internal' : 'external'
    const displayedName = internal ? identity?.name ?? contact.name : contact.name
    const picture = avatar(doc, displayedName, internal ? identity?.photo ?? contact.photo : contact.photo)
    if (internal) picture.style.backgroundColor = (options.userColor ?? markdownUserColor)(identity?.id ?? contact.name)
    // Every person is the core chip in its mention look (chip.css takes its values from it): internal on the user's colour, external
    // on the plain initials or photo, unresolved dashed with „?“.
    const look = { variant: 'mention' as const, tag: 'button' as const, media: picture, label: displayedName, className: 'md-contact-name', editable: !!context.editor?.isEditable,
      state: unresolved ? 'unresolved' as const : undefined, detail: unresolved ? detail : twinDetail(context, contact),
      title: place ? contactsStrings.placeTitle(contact.name) : unresolved ? contactsStrings.unresolvedTitle(people) : undefined,
      ariaLabel: place ? contactsStrings.placeAriaLabel(contact.name) : unresolved ? contactsStrings.unresolvedAriaLabel(displayedName) : contactsStrings.contactDetailsAriaLabel(displayedName) }
    if (chip) chip.update(look); else chip = createMarkdownChip(doc, look, trigger)
    card.classList.toggle('md-contact-gone', unavailable)
    if (unavailable) { const note = doc.createElement('span'); note.className = 'md-contact-unavailable'; note.textContent = contactsStrings.unavailable; trigger.append(note) }
  }
  paint()
  const abort = new AbortController()
  const stop = () => { stopped = true; abort.abort(); closeReader?.(); context.signal.removeEventListener('abort', stop) }
  context.signal.addEventListener('abort', stop, { once: true })
  if (context.signal.aborted) stop()
  else {
    fetchLive(context, options, snapshot, abort.signal, view => { if (!stopped && card.isConnected) { ({ contact, unavailable } = view); paint() } })
    // An unresolved chip says who it most likely is, from the page's context (editors only: readers see the name and „?“).
    if (contact.unresolved && !isContactPlace(contact) && editable(context) && context.editor) void unresolvedPeople(options, context.editor, context.getPos?.() ?? 0, contact.name, abort.signal).then(ranked => {
      if (stopped || !card.isConnected || !ranked.length) return
      detail = unresolvedDetail(ranked); people = ranked.length; paint()
    }).catch(() => {})
    if ((contact.kind ?? 'auto') === 'auto' && options.directory?.resolveInternal) {
      void Promise.resolve().then(() => options.directory!.resolveInternal!(normalizeContact(contact), { signal: abort.signal })).then(user => {
        if (stopped || abort.signal.aborted || !card.isConnected) return
        identity = user === null ? undefined : normalizeInternalContactUser(user); paint()
      }).catch(() => { /* Preserve the stored presentation when the host cannot resolve an identity. */ })
    }
  }
  return stop
}
/**
 * A new contact goes in as its own block right after this one: one contact per block, so text and headings can go between them.
 * In a host whose block takes no sibling (`data-md-single-block` around the editor: a board's block item) it joins this block's list.
 */
function insertAfter(context: Context, contact: MarkdownContact): void {
  const editor = context.editor, position = context.getPos?.()
  const node = position === undefined ? null : editor?.state.doc.nodeAt(position)
  if (!editor || !node || !editable(context)) return
  if (editor.view.dom.closest('[data-md-single-block]')) { context.update!(normalizeContacts({ version: 1, contacts: [...context.data.contacts, contact] })); return }
  editor.chain().focus().insertContentAt(position! + node.nodeSize, { type: node.type.name, attrs: { data: normalizeContacts({ version: 1, contacts: [contact] }) } }).run()
}
function editContact(context: Context, index: number, options: ContactsPluginOptions, draft?: MarkdownContact): () => void {
  if (!context.editor || !editable(context)) return () => {}
  const editor = context.editor
  const contact = draft ?? context.data.contacts[index] ?? { name: '' }
  const title = index < context.data.contacts.length ? contactsStrings.editContactTitle : contactsStrings.addContactTitle
  const kindOf = (label: string | undefined) => (Object.keys(kindLabels) as (keyof typeof kindLabels)[]).find(kind => kindLabels[kind] === label)
  const store = (updated: MarkdownContact) => {
    if (!editable(context)) return
    if (index >= context.data.contacts.length) { insertAfter(context, updated); return }
    const contacts = [...context.data.contacts]; contacts[index] = updated
    context.update!(normalizeContacts({ version: 1, contacts }))
  }
  const kindField = { key: 'kind', label: contactsStrings.fieldDisplayKind, type: 'select' as const, options: Object.values(kindLabels), wide: true }
  if (contact.objectId && options.editLinked) {
    // The panel outlives this render (a colleague's edit, a link run repaint the card): its snapshot goes through the node's context
    // into the node's current contacts, to the same contact by objectId. After the closing click, which re-renders the toolbar under it.
    const node = context.nodeContext?.()
    const save = (updated: MarkdownContact) => { setTimeout(() => {
      const write = node?.update ?? (editable(context) ? context.update : undefined)
      if (!write || !editor.isEditable) return
      const position = node?.getPos?.(), current = position === undefined ? undefined : editor.state.doc.nodeAt(position)?.attrs.data as ContactsData | undefined
      const contacts = current?.contacts ?? context.data.contacts
      const at = contacts[index]?.objectId === contact.objectId ? index : contacts.findIndex(item => item.objectId === contact.objectId)
      if (at < 0) return
      const next = [...contacts]; next[at] = normalizeContact(updated)
      write(normalizeContacts({ version: 1, contacts: next }))
    }) }
    return options.editLinked(contact, { editor, anchor: context.anchor ?? context.container, signal: node?.signal ?? context.signal, save })
  }
  if (contact.objectId) {
    // A reference to a directory contact (spec §4): the person's data changes only in the directory; the page keeps its role and display.
    const note = editor.view.dom.ownerDocument.createElement('p'); note.className = 'md-contact-form-note'; note.textContent = contactsStrings.directoryFieldsNote
    if (options.directoryUrl) {
      const open = note.ownerDocument.createElement('a'); open.href = options.directoryUrl(contact.objectId); open.target = '_blank'; open.rel = 'noopener'
      open.textContent = contactsStrings.openInDirectory; note.append(' ', open)
    }
    return openMarkdownModuleForm({ editor, anchor: context.anchor, signal: context.signal, title, note,
      fields: [{ key: 'role', label: contactsStrings.fieldRole, wide: true }, kindField],
      values: { role: contact.role ?? '', kind: kindLabels[contact.kind ?? 'auto'] },
      save(values) {
        const kind = kindOf(values.kind)
        store(normalizeContact({ ...contact, role: values.role, kind: kind === 'auto' && contact.kind === undefined ? undefined : kind }))
      },
    })
  }
  return openMarkdownModuleForm({ editor, anchor: context.anchor, signal: context.signal, title, draft: !!draft,
    fields: [
      { key: 'name', label: contactsStrings.fieldFullName, wide: true },
      { key: 'role', label: contactsStrings.fieldRole, wide: true },
      { key: 'username', label: contactsStrings.fieldUsername, wide: true },
      { key: 'organization', label: contactsStrings.fieldOrganization, wide: true },
      { key: 'title', label: contactsStrings.fieldTitle, wide: false }, { key: 'department', label: contactsStrings.fieldDepartment, wide: false },
      { key: 'emails', label: contactsStrings.fieldEmails, type: 'textarea', rows: 3, wide: true },
      { key: 'phones', label: contactsStrings.fieldPhones, type: 'textarea', rows: 3, wide: true },
      kindField,
      { key: 'website', label: contactsStrings.web, section: contactsStrings.moreDetailsSection }, { key: 'photo', label: contactsStrings.fieldPhotoUrl, section: contactsStrings.moreDetailsSection },
      { key: 'address', label: contactsStrings.fieldAddress, type: 'textarea', rows: 2, section: contactsStrings.moreDetailsSection },
    ], values: { ...contact, kind: kindLabels[contact.kind ?? 'auto'], phones: contact.phones?.join('\n') ?? '', emails: contact.emails?.join('\n') ?? '' },
    save(values) {
      if (!editable(context)) return
      const split = (key: string) => (values[key] ?? '').split(/\r?\n/).map(value => value.trim()).filter(Boolean)
      const kind = kindOf(values.kind)
      const updated = normalizeContact({ ...values, kind: kind === 'auto' && contact.kind === undefined ? undefined : kind, internalUser: contact.internalUser, directoryId: contact.directoryId, phones: split('phones'), emails: split('emails') })
      if (!options.link) { store(updated); return }
      // Asked like every insert (a match, a contact the page has); going back reopens the form with what was typed.
      const anchor = () => (context.anchor?.isConnected ? context.anchor : context.container).getBoundingClientRect()
      void linkInserted(options, updated, { editor, anchor }, context.signal).then(linked => { if (linked) store(linked); else if (editable(context)) editContact(context, index, options, updated) })
    },
  })
}
function manageContacts(context: Context, options: ContactsPluginOptions): () => void {
  if (!context.editor || !editable(context)) return () => {}
  if (context.data.contacts.length === 1) return editContact(context, 0, options)
  let child: (() => void) | undefined
  const cleanup = openMarkdownContextPopover({ editor: context.editor, anchor: context.anchor ?? context.container, signal: context.signal, title: contactsStrings.label, className: 'md-contact-manager',
    render(panel, close) {
      const list = panel.ownerDocument.createElement('div'); list.className = 'md-contact-manager-list'; panel.append(list)
      context.data.contacts.forEach((contact, index) => {
        const row = panel.ownerDocument.createElement('div'); row.className = 'md-contact-manager-row'; list.append(row)
        const name = panel.ownerDocument.createElement('span'); name.textContent = contact.name; row.append(name)
        button(row, contactsStrings.manageEdit, () => { close(); child = editContact(context, index, options) }).setAttribute('aria-label', contactsStrings.manageEditAriaLabel(contact.name))
        button(row, contactsStrings.manageRemove, () => {
          if (!editable(context)) return
          const removeIt = () => { if (!editable(context)) return; context.update!(normalizeContacts({ version: 1, contacts: context.data.contacts.filter((_, i) => i !== index) })); close() }
          if (!contact.objectId || !options.remove) { removeIt(); return }
          // The host may ask about the last usage; only an explicit yes removes the reference.
          void Promise.resolve().then(() => options.remove!(contact, { signal: context.signal })).then(ok => { if (ok === true) removeIt() }).catch(() => {})
        }).setAttribute('aria-label', contactsStrings.manageRemoveAriaLabel(contact.name))
      })
      button(panel, contactsStrings.addContactTitle, () => { close(); child = editContact(context, context.data.contacts.length, options) })
    },
  })
  return () => { cleanup(); child?.() }
}
function searchDirectory(context: Context, options: ContactsPluginOptions, inline: boolean, anchor: HTMLElement): () => void {
  if (!context.editor || !options.directory || !editable(context)) return () => {}
  return openMarkdownContextPopover({ editor: context.editor, anchor, signal: context.signal, title: contactsStrings.addFromDirectory, className: 'md-contact-directory',
    render(panel, close) {
      const doc = panel.ownerDocument
      const label = doc.createElement('label'); label.className = 'md-contact-search'; label.textContent = contactsStrings.searchFieldLabel
      const input = doc.createElement('input'); input.type = 'search'; input.placeholder = contactsStrings.searchPlaceholder; label.append(input); panel.append(label)
      // The editor's shared search field (P8, P3): results as one types, the first preselected, Enter or Tab takes it, Esc clears first.
      const field = createSuggestionField<MarkdownContact>({ input, label: contactsStrings.searchFieldLabel, testId: 'contact-directory-results',
        emptyText: contactsStrings.noneFound, errorText: contactsStrings.searchFailed,
        search: async (query, { signal }) => (await options.directory!.search(query, { signal })).slice(0, 30).map(normalizeContact),
        item: (contact, index) => ({ id: String(index), title: contact.name, description: [contact.organization, contact.title, contact.emails?.[0]].filter(Boolean).join(' · ') || undefined,
          icon: 'svg:user', avatar: contact.internalUser?.photo ?? contact.photo }),
        pick: contact => {
          if (!editable(context)) return
          const placeholder = context.data.contacts.length === 1 && context.data.contacts[0]!.name === contactsStrings.newContactName && Object.keys(context.data.contacts[0]!).length === 1
          close()
          const at = () => (anchor.isConnected ? anchor : context.container).getBoundingClientRect()
          void linkInserted(options, contact, { editor: context.editor!, anchor: at }, context.signal).then(linked => {
            if (!linked || !editable(context)) return
            if (inline || placeholder) context.update!(normalizeContacts({ version: 1, contacts: [linked] })); else insertAfter(context, linked)
          })
        } })
      panel.append(field.element)
      return () => field.destroy()
    },
  })
}
export function createContactsRuntime(options: ContactsPluginOptions, inline = false): MarkdownModuleRuntime<ContactsData> {
  return {
    // Enter or F2 on a selected unresolved chip picks its person, as a click does.
    edit: context => (inline && context.data.contacts[0]?.unresolved ? chooseUnresolved(context, options, context.anchor ?? context.container) : manageContacts(context, options)),
    render(context) {
      const { container, signal } = context; const doc = container.ownerDocument
      const cleanups: (() => void)[] = []
      const root = doc.createElement(inline ? 'span' : 'div'); root.className = inline ? 'md-contact-inline' : 'md-contact-list'
      const style = doc.createElement('style'); style.textContent = styles; container.append(style, root)
      const status = doc.createElement(inline ? 'span' : 'p'); status.setAttribute('role', 'status'); status.className = 'md-contact-status'
      const download = (contacts: readonly MarkdownContact[]) => downloadMarkdownFile(doc, contacts.length === 1 ? `${contacts[0]!.name.replace(/[^\p{L}\p{N}_-]/gu, '_')}.vcf` : 'kontakty.vcf', new Blob([serializeVCard(contacts)], { type: 'text/vcard;charset=utf-8' }))
      const view: MarkdownContact[] = [...context.data.contacts]
      const fill = (card: HTMLElement, contact: MarkdownContact, unavailable: boolean) => {
        // The host drags a card by its contact also where no editor holds the document (a read view).
        if (contact.objectId) { card.dataset.objectId = contact.objectId; card.dataset.contactName = contact.name }
        card.classList.toggle('md-contact-gone', unavailable)
        card.replaceChildren(avatar(doc, contact.name, contact.photo))
        const body = doc.createElement('span'); body.className = 'md-contact-body'; card.append(body)
        const name = doc.createElement('strong'); name.textContent = contact.name; body.append(name)
        appendRole(body, contact, unavailable)
        const subtitle = doc.createElement('span'); subtitle.className = 'md-contact-subtitle'; subtitle.textContent = [contact.title, contact.organization, contact.department].filter(Boolean).join(' · '); body.append(subtitle)
        const detail = doc.createElement('span'); detail.className = 'md-contact-details'; body.append(detail)
        const link = (label: string, url: string) => { const a = doc.createElement('a'); a.textContent = label; a.href = url; detail.append(a) }
        for (const phone of contact.phones ?? []) link(phone, `tel:${phone.replace(/[\s().-]/g, '').replace(/#/g, '%23')}`)
        for (const email of contact.emails ?? []) link(email, `mailto:${encodeURIComponent(email)}`)
        appendUsername(detail, contact)
        if (contact.website) link(contactsStrings.web, contact.website)
        if (contact.address) { const address = doc.createElement('span'); address.textContent = contact.address; detail.append(address) }
        if (unavailable) return
        const save = button(card, '', () => download([contact])); save.className = 'md-contact-download'; save.setAttribute('aria-label', contactsStrings.downloadCardAriaLabel(contact.name)); save.title = contactsStrings.downloadCardTitle
        const label = doc.createElement('span'); label.className = 'md-contact-download-label'; label.textContent = contactsStrings.downloadCard
        save.append(createIconElement(doc, 'download', 18), label)
      }
      context.data.contacts.forEach((contact, index) => {
        if (inline) { cleanups.push(renderInlineContact(context, contact, root, options, download)); return }
        const card = doc.createElement('article'); card.className = 'md-contact-card'; root.append(card)
        fill(card, contact, false)
        if (editable(context)) {
          // A click on a card edits that contact (its links and download keep their own action). The card selects its block itself, so the
          // toolbar's select does not pull the focus back into the text from what opens.
          card.setAttribute('data-md-editable', ''); card.setAttribute('data-md-no-select', '')
          card.addEventListener('click', event => {
            // A mouse selection of a name or number in the card, to copy it, is no click to edit.
            const selection = doc.getSelection()
            if ((event.target as Element).closest('a, button') || (selection && !selection.isCollapsed && card.contains(selection.anchorNode)) || !editable(context)) return
            event.preventDefault()
            const position = context.getPos?.(); if (position !== undefined) context.editor!.commands.setNodeSelection(position)
            cleanups.push(editContact({ ...context, anchor: card }, index, options))
          })
        }
        else if (contact.objectId) {
          // A reader's click on a card opens its read-only card (copy, directory, where it is used); links and buttons keep their own action.
          card.tabIndex = 0; card.setAttribute('role', 'button'); card.setAttribute('aria-haspopup', 'dialog'); card.setAttribute('aria-label', contactsStrings.contactDetailsAriaLabel(contact.name))
          let closeCard: (() => void) | undefined
          const openCard = () => { closeCard?.(); closeCard = openContactReader(context, card, contactsStrings.readerTitle(view[index]!.name), panel => fillContactReader(panel, view[index]!, undefined, false, options, download, signal,
            options.rateSkills && view[index]!.objectId ? () => { closeCard?.(); options.rateSkills!(view[index]!.objectId!, { anchor: card, signal }) } : undefined)) }
          card.addEventListener('click', event => { const selection = doc.getSelection(); if (!(event.target as Element).closest('a, button') && !(selection && !selection.isCollapsed && card.contains(selection.anchorNode))) openCard() })
          card.addEventListener('keydown', event => { if (event.target === card && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); openCard() } })
          cleanups.push(() => closeCard?.())
        }
        fetchLive(context, options, contact, signal, next => {
          view[index] = next.contact
          // The live answer usually equals the snapshot: repaint only what changed, fading in, so the card never jumps.
          const draft = doc.createElement('article'); draft.className = card.className; fill(draft, next.contact, next.unavailable)
          if (draft.className === card.className && draft.innerHTML === card.innerHTML) return
          fill(card, next.contact, next.unavailable); card.classList.add('md-contact-refreshed')
        })
      })
      if (!inline && context.data.contacts.length > 1) { const all = button(root, contactsStrings.downloadAll, () => download(view)); all.className = 'md-contact-download-all' }
      if (editable(context) && context.toolbar) {
        if (!inline) context.toolbar.addButton({ label: contactsStrings.addContactTitle, icon: 'plus', onClick: anchor => cleanups.push(editContact({ ...context, anchor }, context.data.contacts.length, options)) })
        if (options.directory) {
          context.toolbar.addButton({ label: contactsStrings.addFromDirectory, icon: 'search', onClick: anchor => cleanups.push(searchDirectory(context, options, inline, anchor)) })
          if (context.data.contacts.some(contact => contact.directoryId)) context.toolbar.addButton({ label: contactsStrings.refreshFromDirectory, icon: 'reset', onClick: refresh => {
            if (!editable(context)) return
            const abort = new AbortController(); const cancel = () => abort.abort(); signal.addEventListener('abort', cancel, { once: true }); cleanups.push(() => { cancel(); signal.removeEventListener('abort', cancel) })
            refresh.disabled = true; status.textContent = contactsStrings.refreshing
            let missing = false
            void Promise.all(context.data.contacts.map(async contact => {
              if (!contact.directoryId) return contact
              const result = await options.directory!.get(contact.directoryId, { signal: abort.signal })
              if (!result) { missing = true; return contact }
              if (result.directoryId && result.directoryId !== contact.directoryId) throw new Error('Adresář vrátil jiný kontakt.')
              return normalizeContact({ ...result, kind: contact.kind ?? result.kind, internalUser: result.internalUser ?? contact.internalUser, directoryId: contact.directoryId })
            })).then(contacts => {
              if (abort.signal.aborted || !editable(context)) return
              if (missing) { status.textContent = contactsStrings.someMissingFromDirectory; return }
              context.update!({ version: 1, contacts }); status.textContent = ''
            }).catch(() => { if (!abort.signal.aborted) status.textContent = contactsStrings.refreshFailed }).finally(() => { if (!abort.signal.aborted) refresh.disabled = false })
          } })
        }
      }
      root.append(status)
      if (editable(context) && options.link) {
        const pending = context.data.contacts.map((contact, index) => ({ contact, index })).filter(item => !item.contact.objectId && !item.contact.unresolved)
        let failed = false
        if (pending.length) void Promise.all(pending.map(({ contact }) => Promise.resolve().then(() => options.link!(contact, { signal })).catch(() => { failed = true; return null }))).then(linked => {
          // Quietly: the snapshot stays and the next render links it again.
          if (failed && !signal.aborted) { status.textContent = contactsStrings.linkFailed; if (inline) root.title = contactsStrings.linkFailed }
          if (signal.aborted || !editable(context) || !linked.some(Boolean)) return
          const contacts = [...context.data.contacts]
          pending.forEach(({ contact, index }, i) => { if (linked[i]) contacts[index] = normalizeContact({ ...linked[i], role: contact.role ?? linked[i]!.role }) })
          context.update!(normalizeContacts({ version: 1, contacts }))
        })
      }
      return () => cleanups.splice(0).reverse().forEach(cleanup => cleanup())
    },
  }
}
