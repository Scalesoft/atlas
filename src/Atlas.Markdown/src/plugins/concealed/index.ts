import { Node, type Editor } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { NodeSelection, type EditorState } from '@tiptap/pm/state'
import {
  closeMarkdownHistory, createIconElement, createMarkdownContextToolbar, defineMarkdownAttributeValidator, defineMarkdownCommand, dispatchMarkdownHistoryOperation,
  type MarkdownContextToolbar, type MarkdownPlugin, type MarkdownReadonlyJSONContent,
  createMarkdownStubbedPopover,
} from '../../core'
import { concealedStrings } from './strings'
import { observeMarkdownEditability } from '../../core'
import type { ConcealedContent } from '../../concealment'
import {
  DEFAULTS, NAME, reconcealSummary, settingsOf, validateComplete, validateMode, validateReconceal,
  type ConcealedContentLabels, type ConcealedContentSettings, type ConcealedRevealMode,
} from './model'
import './style.css'

export type { ConcealedContentLabels, ConcealedContentSettings, ConcealedRevealMode } from './model'

export interface ConcealedContentPluginOptions {
  readonly labels?: Partial<ConcealedContentLabels>
}

/** Built at plugin-factory time (not at import time) so a host's `setMarkdownStrings` call takes effect. */
function defaultLabels(): ConcealedContentLabels {
  const strings = concealedStrings
  return {
    block: strings.block, reveal: strings.reveal, hide: strings.hide, scratch: strings.scratch, button: strings.button,
    revealMode: strings.revealMode, preview: strings.preview, unwrap: strings.unwrap, remove: strings.remove,
    settings: strings.settings,
  }
}

/** Nearest concealed block around the selection: the selected node itself or an ancestor. */
function concealedAt(state: EditorState): { pos: number; node: PMNode } | undefined {
  const selection = state.selection
  if (selection instanceof NodeSelection && selection.node.type.name === NAME) return { pos: selection.from, node: selection.node }
  const { $from } = selection
  for (let depth = $from.depth; depth > 0; depth--) {
    if ($from.node(depth).type.name === NAME) return { pos: $from.before(depth), node: $from.node(depth) }
  }
  return undefined
}

/** Wraps the selected blocks, or unwraps the concealed block around the selection. One undo step. */
export const toggleConcealedContent = defineMarkdownCommand<void>({
  id: 'concealed.toggle',
  active: editor => concealedAt(editor.state) !== undefined,
  apply: () => ({ state, tr, dispatch, commands, editor }) => {
    if (!editor.isEditable) return false
    const found = concealedAt(state)
    if (found) {
      if (dispatch) { closeMarkdownHistory(tr); tr.replaceWith(found.pos, found.pos + found.node.nodeSize, found.node.content) }
      return true
    }
    if (dispatch) closeMarkdownHistory(tr)
    return commands.wrapIn(NAME)
  },
})
/** Changes the settings of the concealed block around the selection; omitted keys stay. One undo step. */
export const setConcealedContentSettings = defineMarkdownCommand<Partial<ConcealedContentSettings>>({
  id: 'concealed.set-settings',
  apply: input => ({ state, tr, dispatch }) => {
    const found = concealedAt(state)
    if (!found || !input || typeof input !== 'object' || Object.keys(input).some(key => !(key in DEFAULTS))) return false
    let next: ConcealedContentSettings
    try { next = settingsOf({ ...settingsOf(found.node.attrs), ...input }) } catch { return false }
    if (dispatch) { closeMarkdownHistory(tr); tr.setNodeMarkup(found.pos, undefined, { ...found.node.attrs, ...next }) }
    return true
  },
})
export const setConcealedRevealMode = defineMarkdownCommand<{ readonly reveal: ConcealedRevealMode }>({
  id: 'concealed.set-reveal',
  apply: input => ({ state, tr, dispatch }) => {
    try { validateMode(input?.reveal) } catch { return false }
    const found = concealedAt(state)
    if (!found) return false
    if (dispatch) { closeMarkdownHistory(tr); tr.setNodeAttribute(found.pos, 'reveal', input.reveal) }
    return true
  },
})

/** The invisible ink loads with the first sealed block, so documents without one never download it. */
let concealment: Promise<typeof import('../../concealment')> | undefined
const loadConcealment = () => concealment ??= import('../../concealment')
/** The settings panel loads for authors only: when an editable block's toolbar appears, not for readers. */
const settingsPopover = createMarkdownStubbedPopover(() => import('./settings'))

/** Mounts the invisible ink over already laid-out content. The content keeps its place (hidden), so the page
 *  does not jump; Reveal shows a copy of it under the parting mist. This is visual concealment for readers,
 *  not access control: the content is part of the document. */
function seal(body: HTMLElement, content: () => HTMLElement, settings: ConcealedContentSettings, labels: ConcealedContentLabels): { destroy(): void } {
  let surface: ConcealedContent | undefined
  let destroyed = false
  void loadConcealment().then(({ createConcealedContent }) => {
    if (destroyed || !body.isConnected) return
    surface = mount(createConcealedContent)
  }, () => { /* The block stays sealed; its placeholder ink keeps the content hidden. */ })
  return { destroy() { destroyed = true; surface?.destroy(); surface = undefined } }

  function mount(create: typeof import('../../concealment').createConcealedContent): ConcealedContent {
  const doc = body.ownerDocument
  const mode = settings.reveal
  const surface = create({
    ownerDocument: doc, kind: 'text', layout: 'block', access: 'available',
    labels: { reveal: labels.reveal, hide: labels.hide },
    showRevealControl: mode === 'button', scratchToReveal: mode === 'scratch',
    reconcealAfterMs: settings.reconcealAfterMs,
    completeRevealAt: mode === 'scratch' && settings.completeAtPercent !== null ? settings.completeAtPercent / 100 : null,
    reveal: async () => {
      const copy = content().cloneNode(true) as HTMLElement
      copy.removeAttribute('contenteditable'); copy.querySelectorAll('[contenteditable]').forEach(item => item.removeAttribute('contenteditable'))
      copy.className = 'md-concealed-block__copy'
      return { element: copy }
    },
  })
  surface.element.classList.add('md-concealed-block__ink')
  body.append(surface.element)
  return surface
  }
}

/**
 * The editors that have been editable: their author sees the content, also once the editor is locked against accidental edits (read-only,
 * owner 4.0). The ink is a view against shoulder-surfing, not access control: whoever has the document may reveal it, so a lock changes
 * nothing the block shows, only that its content cannot be edited.
 */
const authored = new WeakSet<Editor>()

export function createConcealedContentPlugin(options: ConcealedContentPluginOptions = {}): MarkdownPlugin {
  const labels: ConcealedContentLabels = { ...defaultLabels(), ...options.labels }
  const modeLabel = (mode: ConcealedRevealMode) => mode === 'button' ? labels.button : labels.scratch
  return {
    id: 'concealed', apiVersion: 1, schemaVersion: 2, profiles: ['rich'],
    // v2 adds the re-cover delay and the scratch completion share; v1 blocks read them as defaults.
    migrations: [{ from: 1, to: 2, migrate: document => ({ ...document }) }],
    create() {
      const block = Node.create({
        name: NAME, group: 'block', content: 'block+', defining: true,
        addAttributes: () => ({
          reveal: { default: 'scratch', validate: defineMarkdownAttributeValidator({ kind: 'enum', values: ['scratch', 'button'] }, validateMode),
            parseHTML: (element: HTMLElement) => element.dataset.reveal === 'button' ? 'button' : 'scratch',
            renderHTML: (attributes: Record<string, unknown>) => ({ 'data-reveal': attributes.reveal }) },
          reconcealAfterMs: { default: DEFAULTS.reconcealAfterMs, validate: defineMarkdownAttributeValidator({ kind: 'integer', minimum: 0, maximum: 600000, nullable: true }, validateReconceal),
            parseHTML: (element: HTMLElement) => { const value = element.dataset.reconceal; if (value === 'never') return null; const ms = Number(value); return Number.isInteger(ms) && ms >= 0 && ms <= 600000 ? ms : DEFAULTS.reconcealAfterMs },
            renderHTML: (attributes: Record<string, unknown>) => ({ 'data-reconceal': attributes.reconcealAfterMs === null ? 'never' : String(attributes.reconcealAfterMs) }) },
          completeAtPercent: { default: DEFAULTS.completeAtPercent, validate: defineMarkdownAttributeValidator({ kind: 'integer', minimum: 10, maximum: 100, nullable: true }, validateComplete),
            parseHTML: (element: HTMLElement) => { const value = element.dataset.complete; if (value === 'off') return null; const share = Number(value); return Number.isInteger(share) && share >= 10 && share <= 100 ? share : DEFAULTS.completeAtPercent },
            renderHTML: (attributes: Record<string, unknown>) => ({ 'data-complete': attributes.completeAtPercent === null ? 'off' : String(attributes.completeAtPercent) }) },
        }),
        parseHTML: () => [{ tag: 'div[data-md-concealed]' }],
        renderHTML: ({ HTMLAttributes }) => ['div', { ...HTMLAttributes, 'data-md-concealed': '', class: 'md-concealed-block' }, 0],
        // Markdown has no concealment: the content is kept, the wrapper is reported as lossy.
        addStorage: () => ({ markdown: { serialize(state: { renderContent(node: PMNode): void; closeBlock(node: PMNode): void }, node: PMNode) {
          state.renderContent(node); state.closeBlock(node)
        } } }),
        addNodeView: () => ({ node: initial, editor, getPos, view }) => {
          let node = initial
          const doc = view.dom.ownerDocument
          const element = <K extends keyof HTMLElementTagNameMap>(name: K, className: string) => {
            const created = doc.createElement(name); created.className = className; return created
          }
          const dom = element('div', 'md-concealed-block'); dom.dataset.mdConcealedView = ''
          const chip = element('div', 'md-concealed-block__chip'); chip.contentEditable = 'false'; chip.tabIndex = 0
          const chipLabel = element('span', 'md-concealed-block__label'); chipLabel.textContent = labels.block
          const chipHint = element('span', 'md-concealed-block__hint')
          chip.append(createIconElement(doc, 'eye-off', 15), chipLabel, chipHint)
          const body = element('div', 'md-concealed-block__body')
          const contentDOM = element('div', 'md-concealed-block__content')
          body.append(contentDOM); dom.append(chip, body)
          const abort = new AbortController()
          let toolbar: MarkdownContextToolbar | undefined
          let close: (() => void) | undefined
          let previewButton: HTMLButtonElement | undefined, settingsButton: HTMLButtonElement | undefined
          let surface: { destroy(): void } | undefined
          let sealedWith = ''
          let preview = false
          const position = () => typeof getPos === 'function' ? getPos() : undefined
          // Readers see the ink and reveal it; authors see the content (a lock keeps it so) and can preview the ink while editing.
          const refresh = () => {
            const settings = settingsOf(node.attrs)
            dom.dataset.reveal = settings.reveal; chipHint.textContent = `${modeLabel(settings.reveal)} · ${reconcealSummary(settings.reconcealAfterMs)}`
            if (editor.isEditable) authored.add(editor)
            const sealed = !authored.has(editor) || (preview && editor.isEditable)
            dom.toggleAttribute('data-sealed', sealed); dom.toggleAttribute('data-readonly', !editor.isEditable)
            previewButton?.setAttribute('aria-pressed', String(preview))
            const key = JSON.stringify(settings)
            if (surface && (!sealed || sealedWith !== key)) { surface.destroy(); surface = undefined }
            if (sealed && !surface) { surface = seal(dom, () => contentDOM, settings, labels); sealedWith = key }
          }
          // The edit (P1): a click on the label, Enter or F2 open the settings at the block; the same gesture closes them again (an
          // open popover marks its anchor `aria-expanded`). The toolbar's „Upravit“ opens them at the label too and closes them likewise.
          const settings = (anchor: HTMLElement) => {
            const again = anchor.getAttribute('aria-expanded') === 'true'
            close?.(); close = undefined
            if (again || !editor.isEditable) return
            // At once as a stub of the panel's size while its chunk loads (stubbedPopover.ts).
            close = settingsPopover.open({ editor, anchor, title: labels.settings, signal: abort.signal, className: 'md-concealed-settings', interactionRoots: settingsButton && [settingsButton],
              render: (panel, container, closePopover) => panel.settingsRenderer(editor, position, labels)(container, closePopover) })
          }
          chip.onkeydown = event => {
            if (!['Enter', ' ', 'F2'].includes(event.key) || !editor.isEditable) return
            event.preventDefault()
            const at = position(); if (at === undefined) return
            editor.commands.setNodeSelection(at); editor.view.focus(); settings(chip)
          }
          // Hosts switch readonly through editor options, which emit no event: the editor's one shared observer tells (no work per update).
          const stopEditable = observeMarkdownEditability(editor, view.dom, refresh)
          queueMicrotask(() => {
            if (abort.signal.aborted || editor.isDestroyed) return
            toolbar = createMarkdownContextToolbar({ editor, anchor: chip, getPos: position, label: labels.block, signal: abort.signal, onActivate: () => settings(chip) })
            if (editor.isEditable) settingsPopover.prefetch()
            settingsButton = toolbar.addButton({ label: concealedStrings.settingsButtonText, icon: 'sliders-horizontal', text: concealedStrings.settingsButtonText, onClick: () => settings(chip) })
            previewButton = toolbar.addButton({ label: labels.preview, icon: 'eye', text: concealedStrings.previewButtonText, onClick() { preview = !preview; refresh() } })
            previewButton.setAttribute('aria-pressed', 'false')
            toolbar.addSeparator()
            // „Převést na text“ (P12): the content stays, only the ink goes.
            toolbar.addButton({ label: labels.unwrap, icon: 'pilcrow', text: labels.unwrap, onClick() {
              const at = position(); if (at === undefined || !editor.isEditable) return
              const current = editor.state.doc.nodeAt(at); if (!current) return
              dispatchMarkdownHistoryOperation(editor, editor.state.tr.replaceWith(at, at + current.nodeSize, current.content))
            } })
            toolbar.addButton({ label: labels.remove, icon: 'trash', onClick() {
              if (editor.isEditable) dispatchMarkdownHistoryOperation(editor, editor.state.tr.deleteSelection())
            } })
          })
          refresh()
          return {
            dom, contentDOM,
            update(next) { if (next.type !== node.type) return false; node = next; refresh(); return true },
            // Only the editable content belongs to ProseMirror; the chip and the ink are our own UI.
            stopEvent: event => event.type !== 'contextmenu' && !contentDOM.contains(event.target as globalThis.Node | null),
            ignoreMutation: mutation => mutation.type !== 'selection' && (!contentDOM.contains(mutation.target) || mutation.target === contentDOM && mutation.type === 'attributes'),
            destroy() { abort.abort(); close?.(); toolbar?.destroy(); stopEditable(); surface?.destroy() },
          }
        },
      })
      return {
        extensions: [block], commands: [toggleConcealedContent, setConcealedContentSettings, setConcealedRevealMode],
        actions: {
          slash: [{ id: 'concealed.toggle', label: labels.block, description: concealedStrings.slashDescription, icon: 'svg:eye-off', keywords: ['skrytý', 'skryt', 'spoiler', 'mlha', 'invisible ink'], command: toggleConcealedContent() }],
          toolbar: [{ id: 'concealed.toggle', label: concealedStrings.toolbarLabel, icon: 'svg:eye-off', command: toggleConcealedContent() }],
          // Next to Quote in the bubble over a selection: hide the selected text or section under invisible ink.
          selection: [{ id: 'concealed.toggle', label: concealedStrings.selectionLabel, icon: 'svg:eye-off', command: toggleConcealedContent() }],
        },
        markdownLossy(document) { let found = false; document.descendants(item => { found ||= item.type.name === NAME }); return found },
        onRender({ container, document }) {
          // Settings come from the validated document, in order, not from the rendered markup.
          const stored: ConcealedContentSettings[] = []
          const walk = (item: MarkdownReadonlyJSONContent, nested: boolean) => {
            const self = item.type === NAME
            if (self && !nested) stored.push(settingsOf({ ...item.attrs }))
            item.content?.forEach(child => walk(child, nested || self))
          }
          walk(document, false)
          // Nested blocks travel inside their outer block's copy; each outer block gets one surface.
          const blocks = [...container.querySelectorAll<HTMLElement>('[data-md-concealed]')].filter(item => !item.parentElement?.closest('[data-md-concealed]'))
          const surfaces = blocks.map((item, index) => {
            // The editor's box (body > content), so the block measures the same read, written and statically rendered.
            const original = [...item.childNodes], doc = item.ownerDocument
            const body = doc.createElement('div'), content = doc.createElement('div')
            body.className = 'md-concealed-block__body'; content.className = 'md-concealed-block__content'
            content.append(...original); body.append(content); item.append(body)
            item.setAttribute('data-sealed', '')
            return { surface: seal(item, () => content, stored[index] ?? DEFAULTS, labels), item, body, original }
          })
          return () => surfaces.forEach(({ surface, item, body, original }) => { surface.destroy(); item.append(...original); body.remove(); item.removeAttribute('data-sealed') })
        },
      }
    },
  }
}

/** Editor helper for hosts that build their own toolbar. */
export function isInsideConcealedContent(editor: Editor): boolean { return concealedAt(editor.state) !== undefined }
