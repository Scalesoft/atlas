import { Node, type Editor } from '@tiptap/core'
import {
  closeMarkdownHistory, createIconElement, createMarkdownChip, createMarkdownContextToolbar, defineMarkdownAttributeValidator, defineMarkdownCommand, dispatchMarkdownHistoryOperation, markdownConcealMotion, markdownStrings,
  openMarkdownContextPopover, openMarkdownReaderPopover, type MarkdownContextToolbar, type MarkdownIconName, type MarkdownPlugin, type MarkdownReadonlyJSONContent,
} from '../../core'
import { secretsStrings } from './strings'
import { createUndoFromControls, markdownChipDeleteKeys, defineMarkdownEntityKind, trackMarkdownApplyForm } from '../../core'
import type { ConcealedContent, ConcealedContentOptions } from '../../concealment'
import { NodeSelection, type Transaction } from '@tiptap/pm/state'
import './style.css'
import {
  DEFAULT_AUTO_HIDE_SECONDS, FIELD_ORDER, MAX_AUTO_HIDE_SECONDS, MIN_AUTO_HIDE_SECONDS, presentation, reference, SECRET_ID_PATTERN,
  validateAutoHide, validateBoolean, validateField, validateFields, validateId,
  type ReferenceAttrs, type SecretField, type SecretReference, type SecretReferencePresentation,
} from './model'
import { installSecretMarkdown, secretClipboardParseRule, secretClipboardSlot, secretMarkdownLossy, secretMarkdownParseRule, secretMarkdownSerializer } from './markdown'

export type { SecretField, SecretReference, SecretReferencePresentation } from './model'
export { SECRET_DIRECTIVE, serializeSecretDirective, parseSecretDirectiveAttributes } from './markdown'

/** The invisible ink loads with the first discovered reference, alongside discovery, not with the page. */
let concealment: Promise<typeof import('../../concealment')> | undefined
const loadConcealment = () => concealment ??= import('../../concealment')

export interface SecretOperationContext { readonly secretId: string; readonly signal: AbortSignal }
export interface SecretRevealContext extends SecretOperationContext {
  /** Every field the surface shows; one authorized release uncovers them together. */
  readonly fields: readonly SecretField[]
  readonly ownerDocument: Document
}
export interface SecretRelease {
  /** One host-created element per released field; omit a field the entry does not have. Plain strings are rejected. */
  readonly values: Readonly<Partial<Record<SecretField, HTMLElement>>>
  /** Clears the host's values. Called on hide, timeout, abort, revocation and unmount. */
  readonly dispose?: () => void
}
export interface SecretCopyContext extends SecretOperationContext { readonly field: SecretField }
/** An access request. `reason` is what the reader typed in the request panel, trimmed; empty when none was given. */
export interface SecretRequestContext extends SecretOperationContext { readonly reason: string }
export type SecretDiscovery = 'hidden' | 'requestable' | 'available'
  | {
    readonly access: 'requestable'; readonly requestPending: boolean
    /** Optional: the reader's last request was not approved (rejected, cancelled, expired). The reference then says
     *  so beside a new request, also after a reload. Without it, a pending request that a refresh finds settled
     *  without access shows the same until the next remount. */
    readonly requestRefused?: boolean
  }
export interface SecretsServices {
  /** Discovery must never return a value or private metadata. */
  discover(context: SecretOperationContext): Promise<SecretDiscovery>
  /** Sends an access request. A host written for 0.5 that ignores `reason` keeps working unchanged. */
  requestAccess(context: SecretRequestContext): Promise<void>
  /** Explicit release for display. The host authorizes, asks for a second factor and audits before it resolves;
   *  values live only in the returned elements, never in the document, history or plugin state. */
  reveal(context: SecretRevealContext): Promise<SecretRelease>
  /** Explicit release for copying. The host writes its own value to the clipboard; nothing passes the plugin. */
  copy(context: SecretCopyContext): Promise<void>
  /** Host forwards access revocations/logout; return the subscription cleanup. */
  subscribeRevocation(secretId: string, revoke: () => void): () => void
}
export interface SecretsLabels {
  readonly card: string; readonly reveal: string; readonly hide: string; readonly copy: string; readonly copied: string
  readonly copyFailed: string; readonly details: string; readonly collapse: string; readonly missing: string
  readonly request: string; readonly pending: string; readonly sending: string; readonly hold: string; readonly denied: string
  readonly requestHint: string; readonly refused: string; readonly requestFailed: string
}
export interface SecretsPluginOptions {
  readonly services: SecretsServices
  /** @deprecated Secret surfaces always require an explicit Reveal control. */
  readonly showRevealControl?: boolean
  /** Field names shown beside values; never secret metadata. */
  readonly fieldLabels?: Partial<Record<SecretField, string>>
  readonly labels?: Partial<SecretsLabels>
  /** How long a release may take, including a second factor, 1000–600000 ms (default 180000). */
  readonly revealTimeoutMs?: number
  /**
   * The request panel's reason: `'optional'` (default) asks for one, `'required'` does not send without one,
   * `'off'` sends the request directly without a panel (the 0.5 behavior; `reason` is then empty).
   */
  readonly requestReason?: 'optional' | 'required' | 'off'
  /**
   * „Vyměnit heslo“: opens the host's picker and resolves the chosen secret id, or null when the author closed it. The
   * plugin writes only that id into the reference (one undo step) and keeps its display settings. Without it the
   * editor offers no swap.
   */
  readonly pickReplacement?: (context: SecretReplacementContext) => Promise<string | null>
}
/** What the host picker is told about the reference being swapped: its current id (to preselect it) and its layout. */
export interface SecretReplacementContext { readonly secretId: string; readonly layout: 'inline' | 'block'; readonly signal: AbortSignal }
/** Host controls over the references this plugin instance shows, in every editor and renderer it is registered in. */
export interface SecretsPluginHandle {
  /**
   * Covers released values again; the references stay visible and interactive, and nothing is revoked. Without an
   * id every reference is covered. `immediate` skips the gentle roll-back (security events). Returns how many
   * shown references matched.
   */
  conceal(secretId?: string, options?: { readonly immediate?: boolean }): number
  /**
   * Runs discovery again without a remount, for example after a 403 lowered the access level or after an access
   * request was approved. An unchanged answer keeps released values; a changed one repaints (and disposes them).
   * A revoked reference is discovered again too. Without an id every reference is refreshed. Returns how many
   * shown references matched.
   */
  refresh(secretId?: string): number
}
export interface SecretsPlugin extends MarkdownPlugin, SecretsPluginHandle {}
interface LiveReference { readonly secretId: string; conceal(immediate: boolean): void; refresh(): void }
const MAX_REQUEST_REASON_LENGTH = 1000

/** Built at plugin-factory time (not at import time) so a host's `setMarkdownStrings` call takes effect. */
function defaultFieldLabels(): Record<SecretField, string> { return { ...secretsStrings.fieldLabels } }
function defaultLabels(): SecretsLabels {
  const strings = secretsStrings
  return {
    card: strings.card, reveal: strings.reveal, hide: strings.hide, copy: strings.copy, copied: strings.copied, copyFailed: strings.copyFailed,
    details: strings.details, collapse: strings.collapse, missing: strings.missing, request: strings.request, pending: strings.pending,
    sending: strings.sending, hold: strings.hold, denied: strings.denied,
    requestHint: strings.requestHint, refused: strings.refused, requestFailed: strings.requestFailed,
  }
}
const FIELD_ICONS: Record<SecretField, MarkdownIconName> = { title: 'type', path: 'folder', username: 'user', password: 'key-round', url: 'link', notes: 'file-text' }
const DEFAULT_REVEAL_TIMEOUT_MS = 180_000
type Layout = 'inline' | 'block'
export const insertSecretReference = defineMarkdownCommand<SecretReference>({
  id: 'secrets.insert',
  apply: input => ({ commands, tr, dispatch }) => {
    try {
      const attrs = reference(input)
      if (dispatch) closeMarkdownHistory(tr)
      return commands.insertContent({ type: 'secretReference', attrs })
    }
    catch { return false }
  },
})
export const insertInlineSecretReference = defineMarkdownCommand<SecretReference>({
  id: 'secrets.insert-inline',
  apply: input => ({ commands, tr, dispatch }) => {
    try {
      const attrs = reference(input)
      if (dispatch) closeMarkdownHistory(tr)
      return commands.insertContent({ type: 'secretInlineReference', attrs })
    }
    catch { return false }
  },
})

export const updateSecretReferencePresentation = defineMarkdownCommand<SecretReferencePresentation>({
  id: 'secrets.update-presentation',
  apply: input => ({ tr, dispatch }) => {
    try {
      const display = presentation(input)
      const selection = tr.selection
      if (!(selection instanceof NodeSelection) || !['secretReference', 'secretInlineReference'].includes(selection.node.type.name)) return false
      const attrs = reference({ ...selection.node.attrs, ...display })
      if (dispatch) {
        closeMarkdownHistory(tr)
        tr.setNodeMarkup(selection.from, undefined, attrs)
        tr.setSelection(NodeSelection.create(tr.doc, selection.from))
      }
      return true
    } catch { return false }
  },
})

/** Writes `change` into the reference at `at` as one undo step and keeps it selected; false when it is gone or invalid. */
function writeReference(editor: Editor, at: number, type: string, change: Partial<ReferenceAttrs>) {
  const target = editor.state.doc.nodeAt(at)
  if (!target || target.type.name !== type || !editor.isEditable) return false
  let attrs: ReferenceAttrs
  try { attrs = reference({ ...target.attrs, ...change }) } catch { return false }
  const tr = editor.state.tr.setNodeMarkup(at, undefined, attrs)
  tr.setSelection(NodeSelection.create(tr.doc, at))
  dispatchMarkdownHistoryOperation(editor, tr)
  return true
}

/** „Vyměnit heslo“: the reference is followed through edits made while the picker is open; the swap is dropped when it
 *  was removed or pointed elsewhere meanwhile. Only `secretId` changes, never a value. */
async function swapReference(editor: Editor, at: number, pick: NonNullable<SecretsPluginOptions['pickReplacement']>, signal: AbortSignal) {
  const node = editor.state.doc.nodeAt(at)
  if (!node || !editor.isEditable || signal.aborted) return false
  const { name: type } = node.type
  const secretId: unknown = node.attrs.secretId
  if (typeof secretId !== 'string') return false
  let position: number | null = at
  const follow = ({ transaction }: { transaction: Transaction }) => {
    if (position === null) return
    const mapped = transaction.mapping.mapResult(position)
    position = mapped.deletedAfter ? null : mapped.pos
  }
  editor.on('transaction', follow)
  let chosen: unknown
  try { chosen = await pick({ secretId, layout: type === 'secretInlineReference' ? 'inline' : 'block', signal }) }
  catch { return false }
  finally { editor.off('transaction', follow) }
  if (signal.aborted || editor.isDestroyed) return false
  // The picker's dialog closes on its opener, which a swap replaces: the keyboard returns to the reference itself (as after Esc).
  const done = (result: boolean) => {
    const target = position ?? at
    // view.focus() at once: tiptap's focus() waits a frame, and the keyboard sat on <body> meanwhile (P7, as the table of contents).
    if (editor.state.doc.nodeAt(target)?.type.name === type) editor.commands.setNodeSelection(target)
    editor.view.focus()
    return result
  }
  if (position === null || typeof chosen !== 'string' || chosen === secretId) return done(false)
  if (editor.state.doc.nodeAt(position)?.attrs.secretId !== secretId) return done(false)
  return done(writeReference(editor, position, type, { secretId: chosen }))
}

/** The chip a card toggle came from, so the card painted after the change gets focus back there. */
const pendingFocus = new WeakMap<Editor, { readonly at: number; readonly chip: string }>()

/** Card in edit mode: the swap and the common display settings as chips on the card itself; each change is one undo
 *  step. The popover keeps the rest (actions, seconds). */
function editStrip(editor: Editor, root: HTMLElement, attrs: ReferenceAttrs, getPos: () => number | undefined, options: { swap?: () => void; more: (anchor: HTMLElement) => void }) {
  const doc = root.ownerDocument
  const strip = doc.createElement('div'); strip.className = 'md-secret-edit'; strip.dataset.mdNoSelect = ''
  strip.setAttribute('role', 'toolbar'); strip.setAttribute('aria-label', secretsStrings.cardEditLabel)
  const chip = (key: string, label: string, icon: MarkdownIconName, run: () => void, pressed?: boolean) => {
    const button = doc.createElement('button'); button.type = 'button'; button.className = 'md-secret-edit__chip'
    button.dataset.secretChip = key
    if (pressed !== undefined) button.setAttribute('aria-pressed', String(pressed))
    const text = doc.createElement('span'); text.textContent = label
    button.append(createIconElement(doc, icon, 13), text)
    button.onclick = () => { if (editor.isEditable && button.getAttribute('aria-disabled') !== 'true') run() }
    strip.append(button)
    return button
  }
  const write = (key: string, change: Partial<ReferenceAttrs>) => {
    const at = getPos()
    if (at === undefined) return
    pendingFocus.set(editor, { at, chip: key })
    if (!writeReference(editor, at, 'secretReference', change)) pendingFocus.delete(editor)
  }
  if (options.swap) chip('swap', secretsStrings.swapLabel, 'replace', options.swap).classList.add('md-secret-edit__swap')
  const shown = attrs.fields ?? [attrs.field]
  for (const field of FIELD_ORDER) {
    const on = shown.includes(field)
    const button = chip(`field:${field}`, secretsStrings.fieldLabels[field], FIELD_ICONS[field],
      () => write(`field:${field}`, { fields: on ? shown.filter(item => item !== field) : [...shown, field] }), on)
    // The card shows at least one field: its last chip stays pressed.
    if (on && shown.length === 1) { button.setAttribute('aria-disabled', 'true'); button.title = secretsStrings.noFieldsError }
  }
  const seconds = attrs.autoHideSeconds
  // A custom delay is not toggled off (on again would give the default): the chip opens the settings with its seconds.
  const autoHide = chip('autoHide', secretsStrings.autoHideChip(seconds ?? DEFAULT_AUTO_HIDE_SECONDS), 'clock', () => seconds !== null && seconds !== DEFAULT_AUTO_HIDE_SECONDS
    ? options.more(autoHide) : write('autoHide', { autoHideSeconds: seconds === null ? DEFAULT_AUTO_HIDE_SECONDS : null }), seconds !== null)
  const more = chip('more', secretsStrings.moreSettingsText, 'sliders-horizontal', () => options.more(more))
  more.title = secretsStrings.cardSettingsTitle
  root.append(strip)
  const focus = pendingFocus.get(editor)
  if (focus && focus.at === getPos()) {
    pendingFocus.delete(editor)
    strip.querySelector<HTMLElement>(`[data-secret-chip="${focus.chip}"]`)?.focus()
  }
}

/** Anchored settings panel in the style of the chart picker: icon tiles for fields and actions, plus
 * automatic hiding. Changes stay a draft until Použít writes them as one undo step. */
function openSettings(editor: Editor, anchor: HTMLElement, getPos: () => number | undefined, layout: Layout, signal: AbortSignal, interactionRoots?: readonly HTMLElement[]) {
  return openMarkdownContextPopover({
    editor, anchor, title: layout === 'inline' ? secretsStrings.inlineSettingsTitle : secretsStrings.cardSettingsTitle, signal, className: 'md-secret-settings', interactionRoots,
    render(container, close) {
      const doc = container.ownerDocument
      const position = getPos()
      const node = position === undefined ? null : editor.state.doc.nodeAt(position)
      if (!node) { close(); return }
      const current = reference(node.attrs)
      const draft = {
        fields: new Set<SecretField>(current.fields ?? [current.field]),
        showCopy: current.showCopy, showDetails: current.showDetails,
        autoHide: current.autoHideSeconds !== null, seconds: current.autoHideSeconds ?? DEFAULT_AUTO_HIDE_SECONDS,
      }
      const form = doc.createElement('form'); form.className = 'md-secret-settings__form'
      // A changed draft applies on a click outside as on „Použít“; an invalid one keeps the panel open with its error (D3).
      const changed = trackMarkdownApplyForm(form)
      const refreshers: Array<() => void> = []
      const refresh = () => refreshers.forEach(update => update())
      const section = (title: string, radio = false) => {
        const heading = doc.createElement('p'); heading.className = 'md-secret-settings__heading'; heading.textContent = title
        const tiles = doc.createElement('div'); tiles.className = 'md-secret-settings__tiles'
        tiles.setAttribute('role', radio ? 'radiogroup' : 'group'); tiles.setAttribute('aria-label', title)
        form.append(heading, tiles)
        return tiles
      }
      const tile = (parent: HTMLElement, options: { icon: MarkdownIconName; label: string; hint: string; data: Record<string, string>; radio?: boolean; selected: () => boolean; toggle: () => void }) => {
        const button = doc.createElement('button'); button.type = 'button'; button.className = 'md-secret-settings__tile'
        Object.assign(button.dataset, options.data)
        if (options.radio) button.setAttribute('role', 'radio')
        const preview = doc.createElement('span'); preview.className = 'md-secret-settings__preview'; preview.append(createIconElement(doc, options.icon, 20))
        const label = doc.createElement('span'); label.className = 'md-secret-settings__label'; label.textContent = options.label
        const hint = doc.createElement('span'); hint.className = 'md-secret-settings__hint'; hint.textContent = options.hint
        const check = createIconElement(doc, 'check', 14); check.classList.add('md-secret-settings__check')
        button.append(preview, label, hint, check)
        button.onclick = () => { error.textContent = ''; options.toggle(); changed(); refresh() }
        refreshers.push(() => {
          const on = options.selected()
          if (options.radio) button.setAttribute('aria-checked', String(on)); else button.setAttribute('aria-pressed', String(on))
        })
        parent.append(button)
        return button
      }
      // Inline and card references both show any combination of fields; one Reveal uncovers them together.
      const fields = section(layout === 'inline' ? secretsStrings.inlineFieldsHeading : secretsStrings.cardFieldsHeading)
      for (const field of FIELD_ORDER) {
        tile(fields, {
          icon: FIELD_ICONS[field], label: secretsStrings.settingsFieldLabels[field], hint: secretsStrings.fieldHints[field], data: { field },
          selected: () => draft.fields.has(field),
          toggle: () => { if (!draft.fields.delete(field)) draft.fields.add(field) },
        })
      }
      const actions = section(secretsStrings.actionsHeading)
      tile(actions, { icon: 'copy', label: secretsStrings.actionCopyLabel, hint: secretsStrings.actionCopyHint, data: { option: 'showCopy' }, selected: () => draft.showCopy, toggle: () => { draft.showCopy = !draft.showCopy } })
      tile(actions, { icon: 'maximize-2', label: secretsStrings.actionDetailsLabel, hint: secretsStrings.actionDetailsHint, data: { option: 'showDetails' }, selected: () => draft.showDetails, toggle: () => { draft.showDetails = !draft.showDetails } })
      const hiding = section(secretsStrings.autoHideHeading, true)
      tile(hiding, { icon: 'clock', label: secretsStrings.autoHideTimedLabel, hint: secretsStrings.autoHideTimedHint, data: { autoHide: 'timed' }, radio: true, selected: () => draft.autoHide, toggle: () => { draft.autoHide = true } })
      tile(hiding, { icon: 'eye', label: secretsStrings.autoHideNeverLabel, hint: secretsStrings.autoHideNeverHint, data: { autoHide: 'never' }, radio: true, selected: () => !draft.autoHide, toggle: () => { draft.autoHide = false } })
      const delay = doc.createElement('label'); delay.className = 'md-secret-settings__delay'
      const delayText = doc.createElement('span'); delayText.textContent = secretsStrings.delayLabel
      const seconds = doc.createElement('input'); seconds.type = 'number'; seconds.name = 'autoHideSeconds'
      seconds.min = String(MIN_AUTO_HIDE_SECONDS); seconds.max = String(MAX_AUTO_HIDE_SECONDS); seconds.step = '1'; seconds.value = String(draft.seconds)
      seconds.oninput = () => { error.textContent = '' }
      delay.append(delayText, seconds); form.append(delay)
      refreshers.push(() => { seconds.disabled = !draft.autoHide; delay.dataset.disabled = String(!draft.autoHide) })
      const error = doc.createElement('p'); error.className = 'md-secret-settings__error'; error.setAttribute('role', 'alert')
      const footer = doc.createElement('div'); footer.className = 'md-secret-settings__footer'
      const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.textContent = markdownStrings.cancel; cancel.onclick = close
      const apply = doc.createElement('button'); apply.type = 'submit'; apply.className = 'md-secret-settings__apply'; apply.textContent = markdownStrings.apply
      footer.append(cancel, apply)
      form.append(error, footer)
      refresh()
      form.onsubmit = event => {
        event.preventDefault()
        const chosen = FIELD_ORDER.filter(field => draft.fields.has(field))
        if (!chosen.length) { error.textContent = secretsStrings.noFieldsError; return }
        const delaySeconds = Number(seconds.value)
        if (draft.autoHide && (!Number.isInteger(delaySeconds) || delaySeconds < MIN_AUTO_HIDE_SECONDS || delaySeconds > MAX_AUTO_HIDE_SECONDS)) {
          error.textContent = secretsStrings.secondsRangeError(MIN_AUTO_HIDE_SECONDS, MAX_AUTO_HIDE_SECONDS); return
        }
        const at = getPos()
        const target = at === undefined ? null : editor.state.doc.nodeAt(at)
        if (at === undefined || !target || target.type !== node.type || !editor.isEditable) { close(); return }
        const display = { fields: chosen, showCopy: draft.showCopy, showDetails: draft.showDetails, autoHideSeconds: draft.autoHide ? delaySeconds : null }
        let attrs: ReferenceAttrs
        try { attrs = reference({ ...target.attrs, ...display }) } catch { error.textContent = secretsStrings.applyFailedError; return }
        // Draft changes apply only here, as one undo step; Cancel or Escape discards them.
        const tr = editor.state.tr.setNodeMarkup(at, undefined, attrs)
        tr.setSelection(NodeSelection.create(tr.doc, at))
        dispatchMarkdownHistoryOperation(editor, tr)
        close()
      }
      container.append(form)
    },
  })
}

/** Values are released only into host-created elements placed under the shared invisible ink. */
export function createSecretsPlugin({
  services, fieldLabels, labels: customLabels, revealTimeoutMs = DEFAULT_REVEAL_TIMEOUT_MS, requestReason = 'optional', pickReplacement,
}: SecretsPluginOptions): SecretsPlugin {
  if (typeof revealTimeoutMs !== 'number' || !Number.isFinite(revealTimeoutMs) || revealTimeoutMs < 1000 || revealTimeoutMs > 600000)
    throw new RangeError('revealTimeoutMs must be a finite number from 1000 to 600000.')
  if (requestReason !== 'optional' && requestReason !== 'required' && requestReason !== 'off')
    throw new RangeError("requestReason must be 'optional', 'required' or 'off'.")
  if (pickReplacement !== undefined && typeof pickReplacement !== 'function') throw new TypeError('pickReplacement must be a function.')
  /** Every reference this instance currently shows, for the host's conceal/refresh. */
  const live = new Set<LiveReference>()
  const matching = (secretId: string | undefined) => {
    if (secretId !== undefined && typeof secretId !== 'string') throw new TypeError('secretId must be a string.')
    return [...live].filter(item => secretId === undefined || item.secretId === secretId)
  }
  const names = { ...defaultFieldLabels(), ...fieldLabels }
  const labels: SecretsLabels = { ...defaultLabels(), ...customLabels }
  const concealedLabels: ConcealedContentOptions['labels'] = {
    reveal: labels.reveal, hide: labels.hide, copy: labels.copy, copied: labels.copied, request: labels.request,
    pending: labels.pending, sending: labels.sending, hold: labels.hold, denied: labels.denied,
    requestHint: labels.requestHint, refused: labels.refused, requestFailed: labels.requestFailed,
  }
  function mount(container: HTMLElement, attrs: ReferenceAttrs, parentSignal: AbortSignal, layout: Layout, owner: { readonly editor?: Editor; readonly root: HTMLElement; readonly decorate?: (card: HTMLElement) => void }) {
    const { secretId } = attrs
    const shown: readonly SecretField[] = attrs.fields ?? [attrs.field]
    let lifetime = new AbortController()
    const doc = container.ownerDocument
    const win = doc.defaultView
    const tag = layout === 'inline' ? 'span' : 'div'
    let state: 'hidden' | 'requestable' | 'available' = 'hidden'
    let createSurface: typeof import('../../concealment').createConcealedContent | undefined
    let requestPending = false
    /** The last request ended without access: told by the host, or seen here as a pending request that settled. */
    let requestRefused = false
    let requesting: AbortController | undefined
    let unsubscribe: (() => void) | undefined
    let expanded = false
    let closeDetail: ((immediate: boolean) => void) | undefined
    const entries = new Set<Entry>()
    const reduced = () => win?.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false
    /** The vault's own timing (`--md-conceal-*`, O6): read per animation, so a host's remap and reduced motion apply. */
    const conceal = (source: Element) => markdownConcealMotion(source, reduced)
    const nextFrame = (run: () => void) => { if (win?.requestAnimationFrame) win.requestAnimationFrame(() => run()); else setTimeout(run, 16) }
    const element = <K extends keyof HTMLElementTagNameMap>(name: K, className: string) => {
      const created = doc.createElement(name); created.className = className; return created
    }
    const hiddenText = (text: string) => { const span = element('span', 'md-secret-sr'); span.textContent = text; return span }

    /** The same toggle, copy and check morph as the invisible ink rail, sized for values. */
    const copyButton = (field: SecretField, signal: AbortSignal) => {
      const button = element('button', 'md-secret-copy'); button.type = 'button'; button.dataset.secretCopy = field
      const name = `${labels.copy}: ${names[field]}`
      const rename = (text: string) => { button.title = text; button.setAttribute('aria-label', text) }
      rename(name)
      const icons = element('span', 'md-concealed__icons')
      for (const [icon, role] of [['copy', 'copy'], ['check', 'done']] as const) {
        const svg = createIconElement(doc, icon, 14); svg.dataset.concealedIcon = role; icons.append(svg)
      }
      button.append(icons)
      let busy = false
      let timer: ReturnType<typeof setTimeout> | undefined
      signal.addEventListener('abort', () => clearTimeout(timer), { once: true })
      button.onclick = () => {
        if (busy || signal.aborted) return
        busy = true; delete button.dataset.failed
        Promise.resolve().then(() => services.copy({ secretId, field, signal })).then(() => {
          if (signal.aborted) return
          button.dataset.done = ''; rename(labels.copied)
          clearTimeout(timer); timer = setTimeout(() => { delete button.dataset.done; rename(name) }, 1600)
        }, () => { if (!signal.aborted) { button.dataset.failed = ''; rename(labels.copyFailed) } })
          .finally(() => { busy = false })
      }
      return button
    }
    /** Refuses plaintext, foreign nodes and malformed releases; the host clears whatever it returned. */
    const accept = (release: unknown, fields: readonly SecretField[]) => {
      const fail = () => {
        try { (release as SecretRelease | undefined)?.dispose?.() } catch { /* Host errors never become visible. */ }
        throw new TypeError('Invalid secret release.')
      }
      const record = release && typeof release === 'object' ? Object.getOwnPropertyDescriptor(release, 'values')?.value : undefined
      if (!record || typeof record !== 'object') fail()
      const values = new Map<SecretField, HTMLElement>()
      const View = win?.HTMLElement
      for (const field of fields) {
        const property = Object.getOwnPropertyDescriptor(record, field)
        if (!property) continue
        const value: unknown = 'value' in property ? property.value : undefined
        if (!View || !(value instanceof View) || value.ownerDocument !== doc) fail()
        values.set(field, value as HTMLElement)
      }
      return values
    }
    const valuesElement = (fields: readonly SecretField[], values: Map<SecretField, HTMLElement>, surfaceLayout: Layout, perValueCopy: boolean, signal: AbortSignal) => {
      // Released values are selected to be copied: a click on them is no click on the reference (it would open its edit).
      const list = element('span', 'md-secret-values'); list.dataset.layout = surfaceLayout; list.dataset.mdNoSelect = ''
      fields.forEach((field, index) => {
        const item = element('span', 'md-secret-values__item'); item.dataset.field = field
        item.style.setProperty('--md-secret-index', String(index))
        if (surfaceLayout === 'inline') {
          if (fields.length > 1) { const icon = createIconElement(doc, FIELD_ICONS[field], 13); icon.classList.add('md-secret-values__icon'); item.append(icon) }
          item.append(hiddenText(`${names[field]}: `))
        }
        const holder = element('span', 'md-secret-values__value')
        const value = values.get(field)
        if (value) holder.append(value); else { holder.textContent = labels.missing; holder.dataset.missing = '' }
        item.append(holder)
        if (value && perValueCopy) item.append(copyButton(field, signal))
        list.append(item)
      })
      return list
    }

    interface Entry { readonly surface: ConcealedContent; isOpen(): boolean; destroy(): void }
    /** One invisible-ink surface for all shown fields: one explicit release, one parting of the mist. */
    const entry = (fields: readonly SecretField[], surfaceLayout: Layout): Entry => {
      const available = state === 'available'
      const single = fields.length === 1
      let open = false
      let restWidth: number | undefined
      let settle: ReturnType<typeof setTimeout> | undefined
      /** Inline: while the fog still covers it, the capsule first widens smoothly to the width of its values;
       *  only then does the mist part over the whole width. It narrows again once covered. */
      const widen = (list: HTMLElement, signal: AbortSignal) => new Promise<void>(resolve => {
        const target = surface.element
        const current = target.getBoundingClientRect().width
        if (!current) { resolve(); return }
        clearTimeout(settle)
        // Measure off-layout; the probe is invisible and inert, and the values leave it before they are shown.
        const probe = element('span', 'md-secret-probe'); probe.setAttribute('aria-hidden', 'true'); probe.inert = true
        probe.append(list); target.append(probe)
        const natural = list.getBoundingClientRect().width
        probe.remove(); list.remove()
        const rail = surface.actions.getBoundingClientRect().width
        target.style.setProperty('--md-secret-rail', `${Math.ceil(rail)}px`)
        const wanted = Math.ceil(natural + rail + 14)
        restWidth ??= current
        if (wanted <= current) { resolve(); return }
        if (reduced()) {
          // Without movement: the capsule dims, takes its new width while dim, and brightens again.
          if (typeof target.animate !== 'function') { target.style.inlineSize = `${wanted}px`; resolve(); return }
          const motion = conceal(target)
          const dim = target.animate([{ opacity: 1 }, { opacity: .3 }], { duration: motion.duration('fast'), easing: motion.ease, fill: 'forwards' })
          dim.finished.then(() => {
            target.style.inlineSize = `${wanted}px`; dim.cancel()
            return target.animate([{ opacity: .3 }, { opacity: 1 }], { duration: motion.duration('enter'), easing: motion.ease }).finished
          }).catch(() => {}).finally(resolve)
          return
        }
        target.style.inlineSize = `${current}px`; void target.offsetWidth
        target.style.inlineSize = `${wanted}px`
        let timer: ReturnType<typeof setTimeout> | undefined
        const done = () => {
          clearTimeout(timer); target.removeEventListener('transitionend', ended); signal.removeEventListener('abort', done); resolve()
        }
        const ended = (event: TransitionEvent) => { if (event.target === target && event.propertyName === 'inline-size') done() }
        target.addEventListener('transitionend', ended); signal.addEventListener('abort', done, { once: true })
        timer = setTimeout(done, conceal(target).duration('unfold') + 150)
      })
      /** Card notes: under the fog the row keeps a fixed synthetic height; once released it grows (or shrinks)
       *  smoothly to the text, up to a limit after which the notes scroll. */
      const fitNotes = (list: HTMLElement, signal: AbortSignal) => new Promise<void>(resolve => {
        const body = surface.element.parentElement
        if (!fields.includes('notes') || !body) { resolve(); return }
        const probe = element('span', 'md-secret-probe'); probe.setAttribute('aria-hidden', 'true'); probe.inert = true
        probe.dataset.layout = 'block'; probe.style.inlineSize = `${surface.element.clientWidth}px`
        probe.append(list); surface.element.append(probe)
        const notes = list.querySelector<HTMLElement>('[data-field="notes"] .md-secret-values__value')
        const height = notes?.getBoundingClientRect().height ?? 0
        probe.remove(); list.remove()
        if (!height) { resolve(); return }
        const rem = parseFloat(win?.getComputedStyle(doc.documentElement).fontSize ?? '16') || 16
        body.style.setProperty('--md-secret-notes-row', `${Math.ceil(Math.max(2.75 * rem, height + 1.4 * rem))}px`)
        if (reduced() || !win?.getComputedStyle(body).transitionDuration.match(/[1-9]/)) { resolve(); return }
        let timer: ReturnType<typeof setTimeout> | undefined
        const done = () => { clearTimeout(timer); body.removeEventListener('transitionend', ended); signal.removeEventListener('abort', done); resolve() }
        const ended = (event: TransitionEvent) => { if (event.target === body) done() }
        body.addEventListener('transitionend', ended); signal.addEventListener('abort', done, { once: true })
        timer = setTimeout(done, conceal(body).duration('unfold') + 150)
      })
      const unfitNotes = () => { surface.element.parentElement?.style.removeProperty('--md-secret-notes-row') }
      const shrink = () => {
        const target = surface.element
        const rest = restWidth; restWidth = undefined
        target.style.removeProperty('--md-secret-rail')
        if (rest === undefined || !target.style.inlineSize) return
        const release = () => { if (restWidth === undefined) target.style.removeProperty('inline-size') }
        if (reduced()) {
          if (typeof target.animate !== 'function') { release(); return }
          const motion = conceal(target)
          const dim = target.animate([{ opacity: 1 }, { opacity: .3 }], { duration: motion.duration('fast'), easing: motion.ease, fill: 'forwards' })
          dim.finished.then(() => { release(); dim.cancel(); target.animate([{ opacity: .3 }, { opacity: 1 }], { duration: motion.duration('enter'), easing: motion.ease }) }, () => {})
          return
        }
        target.style.inlineSize = `${target.getBoundingClientRect().width}px`; void target.offsetWidth
        target.style.inlineSize = `${rest}px`
        settle = setTimeout(release, conceal(target).duration('unfold') + 100)
      }
      const surface = createSurface!({
        ownerDocument: doc, kind: 'secret', layout: surfaceLayout, signal: lifetime.signal, revealTimeoutMs,
        access: available ? 'available' : 'requestable', requestPending, requestRefused, labels: concealedLabels,
        reveal: available ? async ({ signal }) => {
          const release = await services.reveal({ secretId, fields: [...fields], signal, ownerDocument: doc })
          const values = accept(release, fields)
          if (signal.aborted) { try { (release as SecretRelease).dispose?.() } catch { /* Ignored. */ } return }
          const operations = new AbortController()
          const list = valuesElement(fields, values, surfaceLayout, attrs.showCopy && !single, operations.signal)
          const dispose = () => {
            operations.abort()
            if (surfaceLayout === 'inline') shrink(); else unfitNotes()
            try { (release as SecretRelease).dispose?.() } catch { /* Host errors never become visible plaintext. */ }
          }
          if (surfaceLayout === 'inline') await widen(list, signal); else await fitNotes(list, signal)
          if (signal.aborted) { dispose(); return }
          open = true
          const limit = attrs.autoHideSeconds === null ? undefined : setTimeout(() => surface.conceal(), attrs.autoHideSeconds * 1000)
          return { element: list, dispose() {
            open = false; clearTimeout(limit); dispose()
          } }
        } : undefined,
        copy: available && attrs.showCopy && single ? ({ signal }) => services.copy({ secretId, field: fields[0]!, signal }) : undefined,
        requestAccess: request,
        confirmRequest: requestReason === 'off' ? undefined : confirm,
      })
      surface.element.dataset.secretFields = fields.join(' ')
      if (surfaceLayout === 'inline') {
        surface.element.style.setProperty('--md-secret-slots', String(fields.length))
        // Field icons over the glitter tell which fields sit in the sentence; they are presentation, not values.
        if (fields.length > 1) {
          const hints = element('span', 'md-secret-hints'); hints.setAttribute('aria-hidden', 'true')
          for (const field of fields) { const slot = element('span', 'md-secret-hints__slot'); slot.append(createIconElement(doc, FIELD_ICONS[field], 12)); hints.append(slot) }
          surface.element.append(hints)
        }
      }
      const created: Entry = { surface, isOpen: () => open, destroy() { entries.delete(created); clearTimeout(settle); surface.destroy() } }
      entries.add(created)
      return created
    }
    /** Explicit rail action next to Reveal: an icon with a text accessible name. */
    const railAction = (target: HTMLElement, label: string, icon: MarkdownIconName, click: () => void) => {
      const button = element('button', 'md-concealed__button'); button.type = 'button'; button.title = label
      button.dataset.concealedDetails = ''; button.setAttribute('aria-expanded', 'false')
      const text = element('span', 'md-concealed__label'); text.textContent = label
      button.append(createIconElement(doc, icon, 14), text)
      button.onclick = click; target.append(button)
      return button
    }
    /** Height changes glide instead of jumping; reduced motion simply applies them. */
    const animateHeight = (target: HTMLElement, change: () => void) => {
      const from = target.getBoundingClientRect().height
      change()
      const to = target.getBoundingClientRect().height
      if (reduced() || !from || from === to || typeof target.animate !== 'function') return
      target.style.overflow = 'hidden'
      const motion = conceal(target)
      target.animate([{ height: `${from}px` }, { height: `${to}px` }], { duration: motion.duration('unfold'), easing: motion.ease })
        .finished.catch(() => {}).finally(() => { target.style.removeProperty('overflow') })
    }
    /** Card: field names in the clear on the left, one invisible-ink surface over all values on the right. */
    const card = (options: { toggle?: { label: string; icon: MarkdownIconName; click: () => void; expanded: boolean } }) => {
      const root = element(tag, 'md-secret-card'); root.setAttribute('role', 'group'); root.setAttribute('aria-label', labels.card)
      const header = element(tag, 'md-secret-card__header')
      const badge = element('span', 'md-secret-card__badge'); badge.append(createIconElement(doc, 'key-round', 15))
      const title = element('span', 'md-secret-card__title'); title.textContent = labels.card
      header.append(badge, title)
      let toggle: HTMLButtonElement | undefined
      if (options.toggle) {
        toggle = element('button', 'md-secret-card__toggle'); toggle.type = 'button'
        toggle.setAttribute('aria-expanded', String(options.toggle.expanded))
        const text = element('span', 'md-secret-card__toggle-text'); text.textContent = options.toggle.label
        toggle.append(createIconElement(doc, options.toggle.icon, 14), text)
        toggle.onclick = options.toggle.click
        header.append(toggle)
      }
      // The ink and the revealed values have their own clicks (reveal, select to copy): none of them opens the swap.
      const body = element(tag, 'md-secret-card__body'); body.dataset.mdNoSelect = ''
      root.append(header, body)
      let view: Entry | undefined
      const fill = (fields: readonly SecretField[]) => {
        view?.destroy()
        body.replaceChildren()
        body.style.setProperty('--md-secret-rows', String(fields.length))
        // Notes get a taller synthetic row; its height never depends on the concealed text.
        body.style.setProperty('--md-secret-template', fields.map(field => field === 'notes' ? 'var(--md-secret-notes-row, var(--md-secret-notes-rest))' : 'var(--md-secret-row)').join(' '))
        body.style.removeProperty('--md-secret-notes-row')
        fields.forEach((field, index) => {
          const label = element('span', 'md-secret-card__label'); label.dataset.field = field
          label.style.gridRow = String(index + 1); label.style.setProperty('--md-secret-index', String(index))
          const text = element('span', 'md-secret-card__label-text'); text.textContent = names[field]
          label.append(createIconElement(doc, FIELD_ICONS[field], 14), text)
          body.append(label)
        })
        view = entry(fields, 'block')
        body.append(view.surface.element)
        return view
      }
      return { root, toggle, fill, view: () => view }
    }
    const paintBlock = () => {
      const canExpand = state === 'available' && attrs.showDetails && shown.length < FIELD_ORDER.length
      const toggleCard = () => {
        const wasOpen = built.view()?.isOpen() ?? false
        expanded = !expanded
        let next: Entry | undefined
        animateHeight(built.root, () => { next = built.fill(expanded ? FIELD_ORDER : shown) })
        const toggle = built.toggle!
        toggle.setAttribute('aria-expanded', String(expanded))
        toggle.querySelector('.md-secret-card__toggle-text')!.textContent = expanded ? labels.collapse : labels.details
        toggle.querySelector('svg')!.replaceWith(createIconElement(doc, expanded ? 'chevrons-down-up' : 'chevrons-up-down', 14))
        // Values already released stay uncovered: the wider card releases every field in one step.
        if (wasOpen) next?.surface.reveal()
      }
      const built = card({ toggle: canExpand ? { label: expanded ? labels.collapse : labels.details, icon: expanded ? 'chevrons-down-up' : 'chevrons-up-down', click: toggleCard, expanded } : undefined })
      built.fill(expanded ? FIELD_ORDER : shown)
      container.append(built.root)
      owner.decorate?.(built.root)
    }
    /** Inline Detail: the entry unfolds into a floating card with every field, anchored under the sentence. */
    const openDetail = (anchor: HTMLElement, main: Entry, trigger: HTMLButtonElement) => {
      if (closeDetail) { closeDetail(false); return }
      const wasOpen = main.isOpen()
      const panel = element('span', 'md-secret-popover')
      const built = card({ toggle: { label: labels.collapse, icon: 'minimize-2', click: () => close(false), expanded: true } })
      const view = built.fill(FIELD_ORDER)
      panel.append(built.root); anchor.append(panel)
      trigger.setAttribute('aria-expanded', 'true')
      const bounds = panel.getBoundingClientRect()
      const overflow = bounds.right - (doc.documentElement.clientWidth - 12)
      if (overflow > 0) panel.style.setProperty('--md-secret-shift', `${-Math.min(overflow, Math.max(0, bounds.left - 12))}px`)
      // The card grows out of the capsule: it starts as the capsule itself and unfolds down and sideways.
      const folded = () => {
        const box = panel.getBoundingClientRect()
        const from = main.surface.element.getBoundingClientRect()
        const left = Math.max(0, from.left - box.left)
        const right = Math.max(0, box.right - from.right)
        const bottom = Math.max(0, box.height - from.height)
        const lift = box.top - from.top
        return { clipPath: `inset(0px ${right}px ${bottom}px ${left}px round 8px)`, transform: `translateY(${-lift}px)`, opacity: 0 }
      }
      const unfolded = { clipPath: 'inset(0px 0px 0px 0px round 16px)', transform: 'none', opacity: 1 }
      const animated = !reduced() && typeof panel.animate === 'function'
      // Values already released stay released: the card uncovers every field in one step. While it unfolds, its
      // clip makes the ink report itself as off-screen (which would abort a release), so the mist parts afterwards.
      const release = () => { if (wasOpen && closeDetail === close) view.surface.reveal() }
      if (wasOpen) main.surface.conceal()
      if (animated) {
        panel.dataset.open = ''; panel.dataset.animated = ''
        // Opacity arrives early, the shape keeps unfolding for the whole duration.
        panel.animate([folded(), { opacity: 1, offset: .25 }, unfolded], { duration: conceal(panel).duration('unfold'), easing: conceal(panel).easeRoll })
          .finished.then(release, () => {})
      } else nextFrame(() => { panel.dataset.open = ''; release() })
      const outside = (event: Event) => { if (!anchor.contains(event.target as globalThis.Node | null)) close(false) }
      // Taken here (prevented): this Esc closes the card only, the secret stays selected (P3).
      const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.preventDefault(); close(false); trigger.focus() } }
      // Escape works from the card and from the entry's own rail, where focus rests after pressing Detail.
      doc.addEventListener('pointerdown', outside, true)
      anchor.addEventListener('keydown', escape)
      const close = (immediate: boolean) => {
        if (closeDetail !== close) return
        closeDetail = undefined
        doc.removeEventListener('pointerdown', outside, true); anchor.removeEventListener('keydown', escape)
        trigger.setAttribute('aria-expanded', 'false')
        if (immediate || reduced()) { view.destroy(); panel.remove(); return }
        // The card folds back into the sentence; its values are covered first and cleared once it has gone.
        view.surface.conceal()
        panel.inert = true
        const finish = () => { view.destroy(); panel.remove() }
        if (!animated) { delete panel.dataset.open; setTimeout(finish, conceal(panel).duration('enter')); return }
        const folding = panel.animate([unfolded, { opacity: 1, offset: .7 }, folded()], { duration: conceal(panel).duration('enter'), easing: conceal(panel).ease, fill: 'forwards' })
        folding.finished.then(finish, finish)
      }
      closeDetail = close
    }
    /** Inline: the shared inline chip (a tag, as a YouTrack issue): the key and the fields' names in the clear, the values under the ink. */
    const paintInline = () => {
      // The ink has its own tap (reveal, copy), as the card's body: the key and the fields' names start the edit (P1).
      const anchor = element('span', 'md-secret-entry'); anchor.dataset.mdNoSelect = ''
      const main = entry(shown, 'inline')
      anchor.append(main.surface.element)
      if (state === 'available' && attrs.showDetails) {
        const trigger: HTMLButtonElement = railAction(main.surface.actions, labels.details, 'maximize-2', () => openDetail(anchor, main, trigger))
      }
      const fieldNames = shown.map(field => names[field]).join(' · ')
      const chip = createMarkdownChip(doc, { icon: 'key-round', label: fieldNames, className: 'md-secret-chip', ariaLabel: `${labels.card}: ${fieldNames}` }).element
      chip.setAttribute('role', 'group'); chip.append(anchor)
      container.append(chip)
    }
    const paint = () => {
      closeDetail?.(true)
      ;[...entries].forEach(item => item.destroy())
      container.replaceChildren()
      container.hidden = state === 'hidden' || lifetime.signal.aborted
      if (container.hidden) return
      if (layout === 'inline') paintInline(); else paintBlock()
    }
    /** The reason typed in the request panel; handed to the host with the request, then forgotten. */
    let reason = ''
    /** Anchored reason panel (Odeslat/Zrušit), before anything is sent. Resolves false when dismissed. */
    const confirm = ({ anchor, signal }: { anchor: HTMLElement; signal: AbortSignal }) => new Promise<boolean>(resolve => {
      let submitted = false
      reason = ''
      openMarkdownReaderPopover({
        anchor, editor: owner.editor, root: owner.root, title: secretsStrings.requestReasonTitle, signal, className: 'md-secret-request',
        render(body, close) {
          const form = element('form', 'md-secret-request__form'); form.noValidate = true
          const fields = element('div', 'md-context-fields')
          const label = element('label', 'md-context-field md-context-field-wide')
          const caption = element('span', 'md-secret-request__label')
          caption.textContent = requestReason === 'required' ? secretsStrings.requestReasonLabel : secretsStrings.requestReasonOptionalLabel
          const input = element('textarea', 'md-secret-request__reason'); input.name = 'reason'; input.rows = 3
          input.maxLength = MAX_REQUEST_REASON_LENGTH; input.placeholder = secretsStrings.requestReasonPlaceholder
          if (requestReason === 'required') input.required = true
          label.append(caption, input); fields.append(label)
          const error = element('p', 'md-secret-request__error'); error.setAttribute('role', 'alert')
          const actions = element('div', 'md-module-actions')
          const cancel = element('button', 'md-btn'); cancel.type = 'button'; cancel.textContent = markdownStrings.cancel; cancel.onclick = close
          const send = element('button', 'md-btn-primary'); send.type = 'submit'; send.textContent = secretsStrings.requestReasonSubmit
          actions.append(cancel, send); form.append(fields, error, actions)
          const submit = () => {
            const text = input.value.trim()
            if (requestReason === 'required' && !text) { error.textContent = secretsStrings.requestReasonRequiredError; input.focus(); return }
            if (text.length > MAX_REQUEST_REASON_LENGTH) { error.textContent = secretsStrings.requestReasonTooLongError(MAX_REQUEST_REASON_LENGTH); return }
            reason = text; submitted = true; close()
          }
          input.oninput = () => { error.textContent = '' }
          // Ctrl+Enter (Cmd+Enter) sends from the text area; Enter alone starts a new line.
          input.onkeydown = event => { if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) { event.preventDefault(); submit() } }
          form.onsubmit = event => { event.preventDefault(); submit() }
          body.append(form)
          return () => resolve(submitted && !signal.aborted)
        },
      })
      if (signal.aborted && !submitted) resolve(false)
    })
    /** The surface shows sending/pending itself; success keeps it calm and remembers the pending request. */
    const request = async () => {
      if (lifetime.signal.aborted || requesting || state !== 'requestable' || requestPending) throw new Error('Request unavailable.')
      const operation = new AbortController(); requesting = operation
      const abort = () => operation.abort()
      const scope = lifetime.signal
      scope.addEventListener('abort', abort, { once: true })
      const timer = setTimeout(abort, 60_000)
      const context: SecretRequestContext = { secretId, signal: operation.signal, reason }
      reason = ''
      try {
        await services.requestAccess(context)
        if (operation.signal.aborted) throw new Error('Request cancelled.')
        requestPending = true; requestRefused = false
      } finally {
        clearTimeout(timer); scope.removeEventListener('abort', abort)
        if (requesting === operation) requesting = undefined
      }
    }
    let stopped = false
    let painted = false
    let discovery: AbortController | undefined
    /** Discovery, again on `refresh`: an unchanged answer keeps what is shown (released values included). */
    const discover = async () => {
      discovery?.abort()
      const run = new AbortController(); discovery = run
      const scope = lifetime.signal
      const abort = () => run.abort()
      scope.addEventListener('abort', abort, { once: true })
      try {
        const ink = loadConcealment()
        const discovered = await services.discover({ secretId, signal: run.signal })
        if (run.signal.aborted) return
        // A hidden reference never needs the ink; a visible one waits for it before its first paint.
        if (discovered !== 'hidden') createSurface = (await ink).createConcealedContent
        if (run.signal.aborted) return
        let next: typeof state, pending: boolean, refused = false
        if (typeof discovered === 'object' && discovered?.access === 'requestable' && typeof discovered.requestPending === 'boolean') {
          next = 'requestable'; pending = discovered.requestPending
          refused = !pending && discovered.requestRefused === true
        } else {
          next = discovered === 'available' || discovered === 'requestable' ? discovered : 'hidden'
          pending = false
        }
        // A request this reference knew as pending that is now settled without access was not approved; that stays
        // until access changes or a new request is sent.
        if (painted && state === 'requestable' && next === 'requestable' && !pending && (requestPending || requestRefused)) refused = true
        if (painted && next === state && pending === requestPending && refused === requestRefused) return
        // A reference that changes in place (approved, refused, revoked) fades into its new state.
        const repaint = painted
        state = next; requestPending = pending; requestRefused = refused; painted = true
        if (repaint) container.dataset.mdSecretRepaint = ''
        paint()
      } catch {
        if (!run.signal.aborted) { state = 'hidden'; painted = true; paint() }
      } finally {
        scope.removeEventListener('abort', abort)
        if (discovery === run) discovery = undefined
      }
    }
    const revoke = () => { state = 'hidden'; discovery?.abort(); lifetime.abort(); paint() }
    const handle: LiveReference = {
      secretId,
      conceal(immediate) {
        if (stopped) return
        for (const item of [...entries]) if (item.isOpen()) { if (immediate) item.surface.hide(); else item.surface.conceal() }
      },
      refresh() {
        if (stopped) return
        // A revoked reference comes back only through a new discovery, never by itself.
        if (lifetime.signal.aborted) lifetime = new AbortController()
        void discover()
      },
    }
    const stop = () => {
      if (stopped) return
      stopped = true
      live.delete(handle)
      closeDetail?.(true)
      ;[...entries].forEach(item => item.destroy())
      discovery?.abort(); lifetime.abort(); container.replaceChildren(); container.hidden = true
      parentSignal.removeEventListener('abort', stop)
      const cleanup = unsubscribe; unsubscribe = undefined; cleanup?.()
    }
    paint()
    if (parentSignal.aborted) { stop(); return stop }
    parentSignal.addEventListener('abort', stop, { once: true })
    live.add(handle)
    try {
      const cleanup = services.subscribeRevocation(secretId, revoke)
      if (stopped) cleanup(); else unsubscribe = cleanup
    } catch { state = 'hidden'; painted = true; paint(); return stop }
    if (!lifetime.signal.aborted) void discover()
    return stop
  }
  return {
    conceal(secretId, options) {
      const found = matching(secretId)
      found.forEach(item => item.conceal(options?.immediate === true))
      return found.length
    },
    refresh(secretId) {
      const found = matching(secretId)
      found.forEach(item => item.refresh())
      return found.length
    },
    id: 'secrets', apiVersion: 1, schemaVersion: 4, profiles: ['rich'], entities: [defineMarkdownEntityKind({ kind: 'secret', inline: 'secretInlineReference' })],
    migrations: [
      { from: 1, to: 2, migrate: document => ({ ...document }) },
      // v3 adds the optional card row list on block references; existing documents keep their single field.
      { from: 2, to: 3, migrate: document => ({ ...document }) },
      // v4 lets inline references show several fields too; existing documents keep their single field.
      { from: 3, to: 4, migrate: document => ({ ...document }) },
    ],
    create({ signal }) {
      const node = (layout: Layout, name = layout === 'inline' ? 'secretInlineReference' : 'secretReference') => Node.create({
        name, group: layout, inline: layout === 'inline', atom: true,
        addAttributes: () => ({
          secretId: { default: null, rendered: false, validate: defineMarkdownAttributeValidator({ kind: 'text', pattern: SECRET_ID_PATTERN }, validateId) },
          field: { default: 'password', rendered: false, validate: defineMarkdownAttributeValidator({ kind: 'enum', values: FIELD_ORDER }, validateField) },
          fields: { default: null, rendered: false, validate: defineMarkdownAttributeValidator({ kind: 'structured', validator: 'secretFields' }, validateFields) },
          showCopy: { default: true, rendered: false, validate: defineMarkdownAttributeValidator({ kind: 'boolean' }, validateBoolean) },
          showDetails: { default: false, rendered: false, validate: defineMarkdownAttributeValidator({ kind: 'boolean' }, validateBoolean) },
          autoHideSeconds: { default: DEFAULT_AUTO_HIDE_SECONDS, rendered: false,
            validate: defineMarkdownAttributeValidator({ kind: 'integer', minimum: MIN_AUTO_HIDE_SECONDS, maximum: MAX_AUTO_HIDE_SECONDS, nullable: true }, validateAutoHide) },
        }),
        // Only the plugin's own Markdown parse creates a reference from HTML; pasted HTML never matches.
        parseHTML: () => [secretMarkdownParseRule(layout), secretClipboardParseRule(layout)],
        // A constant empty marker connects static onRender with its validated document snapshot.
        // HTML/clipboard never carry an ID, plaintext, label, or serialized metadata.
        renderHTML: ({ node }) => [layout === 'inline' ? 'span' : 'div', { [layout === 'inline' ? 'data-md-secret-inline' : 'data-md-secret']: '', 'data-md-secret-slot': secretClipboardSlot(node.attrs), hidden: '' }],
        renderText: () => '',
        // The lossless Markdown form carries the reference attributes only (markdown.ts).
        addStorage: () => ({ markdown: { serialize: secretMarkdownSerializer(layout), parse: { setup: installSecretMarkdown } } }),
        addNodeView: () => ({ node, view, editor, getPos }) => {
          const dom = view.dom.ownerDocument.createElement(layout === 'inline' ? 'span' : 'div')
          dom.contentEditable = 'false'
          dom.dataset.mdSecretView = layout
          const abort = new AbortController()
          let toolbar: MarkdownContextToolbar | undefined, settingsButton: HTMLButtonElement | undefined
          let close: (() => void) | undefined
          const position = () => typeof getPos === 'function' ? getPos() : undefined
          // The edit (P1): a click on the reference, Enter or F2 open its settings where it is shown; the same gesture closes them
          // again (an open popover marks its anchor `aria-expanded`). The toolbar's „Upravit“ opens them there too and closes them likewise.
          const settings = () => {
            const again = dom.getAttribute('aria-expanded') === 'true'
            close?.(); close = again ? undefined : openSettings(editor, dom, position, layout, abort.signal, settingsButton && [settingsButton])
          }
          const swap = pickReplacement && (() => { const at = position(); if (at !== undefined) void swapReference(editor, at, pickReplacement, abort.signal) })
          const attrs = reference(node.attrs)
          // The edit chips belong to the painted card, so a reference the reader may not discover shows none.
          const decorate = layout === 'block' ? (card: HTMLElement) => editStrip(editor, card, attrs, position, { swap, more: settings }) : undefined
          const stop = mount(dom, attrs, signal, layout, { editor, root: view.dom, decorate })
          const undo = createUndoFromControls(editor)
          dom.addEventListener('keydown', undo)
          // Selection-owned toolbar above the reference; hidden automatically while readonly.
          queueMicrotask(() => {
            if (abort.signal.aborted || editor.isDestroyed) return
            toolbar = createMarkdownContextToolbar({ editor, anchor: dom, getPos: position, label: layout === 'inline' ? secretsStrings.inlineToolbarLabel : secretsStrings.cardToolbarLabel, signal: abort.signal,
              // A click on the reference, Enter and F2 edit it in place (P1); the swap is the named action „Vyměnit heslo“ beside it.
              onActivate: settings })
            settingsButton = toolbar.addButton({ label: secretsStrings.settingsButtonText, icon: 'sliders-horizontal', text: secretsStrings.settingsButtonText, onClick: settings })
            if (swap) toolbar.addButton({ label: secretsStrings.swapLabel, icon: 'replace', text: secretsStrings.swapButtonText, onClick: swap })
            toolbar.addSeparator()
            toolbar.addButton({ label: secretsStrings.removeLabel, icon: 'trash', onClick() {
              if (editor.isEditable) dispatchMarkdownHistoryOperation(editor, editor.state.tr.deleteSelection())
            } })
          })
          return {
            dom, stopEvent: event => event.type !== 'contextmenu', ignoreMutation: () => true,
            destroy() { dom.removeEventListener('keydown', undo); abort.abort(); close?.(); toolbar?.destroy(); stop() },
          }
        },
      })
      return {
        extensions: [node('block'), node('inline'), markdownChipDeleteKeys('secrets', ['secretInlineReference'])], commands: [insertSecretReference, insertInlineSecretReference, updateSecretReferencePresentation],
        markdownLossy: secretMarkdownLossy,
        onRender({ container, document, signal: renderSignal }) {
          const references: { attrs: ReferenceAttrs; layout: Layout }[] = []
          const walk = (item: MarkdownReadonlyJSONContent) => {
            if (item.type === 'secretReference' || item.type === 'secretInlineReference') {
              references.push({ attrs: reference(item.attrs), layout: item.type === 'secretInlineReference' ? 'inline' : 'block' })
            }
            item.content?.forEach(walk)
          }
          walk(document)
          const cleanups = [...container.querySelectorAll<HTMLElement>('[data-md-secret], [data-md-secret-inline]')]
            .flatMap((element, index) => {
              const reference = references[index]
              const layout = element.hasAttribute('data-md-secret-inline') ? 'inline' : 'block'
              return reference?.layout === layout ? [mount(element, reference.attrs, renderSignal, layout, { root: container })] : []
            })
          return () => cleanups.reverse().forEach(stop => stop())
        },
      }
    },
  }
}
