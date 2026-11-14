import { Node, type Editor, type JSONContent, type AnyExtension } from '@tiptap/core'
import type { MarkdownIconName } from '../../ui/iconMarkup'
import type { Node as PMNode, DOMOutputSpec } from '@tiptap/pm/model'
import { NodeSelection } from '@tiptap/pm/state'
import { defineMarkdownCommand, isCommandDefinition } from '../../core/plugins/commands'
import type { MarkdownPlugin, MarkdownReadonlyJSONContent } from '../../core/plugins/types'
import { createUndoFromControls } from './undoFromControls'
import type { MarkdownPluginCommand } from '../../core/commandTypes'
import { createMarkdownModuleLoader } from './lazy'
import { createMarkdownContextToolbar, type MarkdownContextToolbar } from './contextToolbar'
import { activeMarkdownPopover, leaveMarkdownPopover } from './popoverInteraction'
import { closeMarkdownHistory, dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { markdownOverlaysFor } from '../../core/overlay'
import { moduleDocumentSnapshot } from './documentSnapshot'
import { observeMarkdownEditability } from './editability'
import { createMarkdownModuleActivation } from './activation'
import { markdownStrings } from '../../ui/strings'
import { modulesStrings } from '../../ui/strings/modules'
import { chipToTextShortcut, convertMarkdownChipToText, markdownChipText } from '../chips/chipText'
import { defineMarkdownAttributeValidator } from '../../schema/constraints'
import { loadedMarkdownDialect, markdownDialect, serializedMarkdownDocument } from '../../engine/markdownDialectLoader'

/** How long the document is quiet before a `watchDocument` block reads it again (contract J12.6: idle, ≤ 150 ms). */
const WATCH_IDLE_MS = 100

export interface MarkdownModuleContext<T> {
  container: HTMLElement
  rootContainer?: HTMLElement
  data: T
  document: MarkdownReadonlyJSONContent
  signal: AbortSignal
  editor?: Editor
  toolbar?: MarkdownContextToolbar
  getPos?: () => number | undefined
  anchor?: HTMLElement
  update?: (data: T, options?: { history?: 'typing' }) => void
  /** Delimit a live editing session in local and collaborative undo history. */
  closeHistory?: () => void
  /**
   * A context for UI that outlives one render of the block, such as a host detail workspace; see
   * `MarkdownModuleNodeContext`. `signal` of a render context aborts on every re-render (editability
   * change, lazy re-activation), the node context's only with the node.
   */
  nodeContext?: () => MarkdownModuleNodeContext<T>
}
/**
 * A block node's context for host UI that outlives one render (`MarkdownModuleContext.nodeContext()`).
 * Its `signal` aborts only when the node itself is removed or replaced or the editor is destroyed, never
 * on a readonly toggle; editability changes arrive through `onEditableChange` instead. In the static
 * renderer (no `editor`, no `update`) it lives as long as the rendered output.
 */
export interface MarkdownModuleNodeContext<T> {
  /**
   * The node's stable element, to position or scroll host UI against. The module's renderer owns its
   * children: hosts must never render into it (use their own element, e.g. a detail workspace).
   */
  anchor: HTMLElement
  /** The editor's root element. */
  rootContainer?: HTMLElement
  /** A snapshot taken when the context was created; read the current node through `getPos`. */
  data: T
  document: MarkdownReadonlyJSONContent
  signal: AbortSignal
  editor?: Editor
  getPos?: () => number | undefined
  /** Writes to the node across re-renders; refused while the editor is readonly or after `signal` aborts. */
  update?: (data: T, options?: { history?: 'typing' }) => void
  closeHistory?: () => void
  /**
   * Calls `listener` with the new state whenever the editor becomes readonly or editable again, until
   * the returned function is called or `signal` aborts. Never calls it in the static renderer.
   */
  onEditableChange(listener: (editable: boolean) => void): () => void
}
export interface MarkdownModuleRuntime<T> {
  render(context: MarkdownModuleContext<T>): void | (() => void) | MarkdownModuleView<T> | Promise<void | (() => void) | MarkdownModuleView<T>>
  edit?(context: MarkdownModuleContext<T>): void | (() => void)
  /** `false`: a click on the block only selects it (J2.1, contract 3.6); Enter/F2 on the selected block open `edit`. Default true. */
  editOnClick?: boolean
}
/** Optional retained view for renderers that animate updates between data snapshots. */
export interface MarkdownModuleView<T> {
  update(context: MarkdownModuleContext<T>): void
  /** Editability flipped (contract J1.2): show or hide the edit affordances in place. Without it, a flip renders the block again. */
  editable?(editable: boolean): void
  /** Runtime supplies current readable source and its own status when prepare returns false. */
  fallback?: 'runtime'
  /** Optional readiness of the current async paint; false means an explicit readable fallback. */
  prepare?(): Promise<boolean>
  destroy(): void
}
/**
 * J4.3: Backspace at the start of a filled text block right after the block (Delete at its end, right before it) selects the block;
 * the second press deletes it, as one undo step. ProseMirror's join would delete the atom at once. An empty text block keeps its own
 * Backspace (it is removed), and a caret inside a selection or a text range is left alone.
 */
function selectNeighbouringBlock(editor: Editor, name: string, forward: boolean): boolean {
  const { selection, doc } = editor.state
  if (!selection.empty || !selection.$from.parent.isTextblock || selection.$from.parent.content.size === 0) return false
  const $from = selection.$from
  if (forward ? $from.parentOffset !== $from.parent.content.size : $from.parentOffset !== 0) return false
  const boundary = forward ? $from.after() : $from.before()
  const neighbour = forward ? doc.resolve(boundary).nodeAfter : doc.resolve(boundary).nodeBefore
  if (neighbour?.type.name !== name) return false
  editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(doc, forward ? boundary : boundary - neighbour.nodeSize)))
  return true
}

/** The content height of blocks drawn in this session, by module and data (J8.1): a revisit or a second copy starts at the right size. */
const drawnHeights = new Map<string, number>()
const HEIGHTS_STORE = 'atlas.md.drawnHeights'
let heightsLoaded = false
/** Short key for a block's data (FNV-1a): the heights outlive the session, so a reload starts at the size the block had. */
const heightKeyOf = (name: string, data: unknown): string => {
  const text = JSON.stringify(data); let hash = 2166136261
  for (let index = 0; index < text.length; index++) hash = Math.imul(hash ^ text.charCodeAt(index), 16777619)
  return `${name}:${text.length}:${(hash >>> 0).toString(36)}`
}
const loadDrawnHeights = () => {
  if (heightsLoaded) return; heightsLoaded = true
  try { for (const [key, height] of Object.entries(JSON.parse(globalThis.localStorage?.getItem(HEIGHTS_STORE) ?? '{}') as Record<string, number>)) drawnHeights.set(key, height) } catch { /* no storage, no memory */ }
}
let saveTimer: ReturnType<typeof setTimeout> | undefined
const saveDrawnHeights = () => {
  clearTimeout(saveTimer)
  saveTimer = setTimeout(() => { try { globalThis.localStorage?.setItem(HEIGHTS_STORE, JSON.stringify(Object.fromEntries(drawnHeights))) } catch { /* full or blocked */ } }, 2000)
}

/** The modules whose insertion just asked for their edit to open (J5.7), per editor: the block opens it when its runtime is ready and it is the selected node. */
const openOnInsertRequests = new WeakMap<Editor, string>()

export interface MarkdownBlockModuleOptions<T> {
  id: string; name: string; label: string; defaults: T; inline?: boolean
  /**
   * The height of the block's content before it is drawn (J8.1): the stub keeps it, so hydration moves nothing. A block drawn before in
   * this session keeps the height it had (the drawing's size is not known from its data).
   */
  stubHeight?(data: T, document: MarkdownReadonlyJSONContent): number
  /** The `stubHeight` is exact (it follows the page's own data): a remembered height never overrides it. */
  exactStub?: boolean
  /** What an inline stub shows while it loads, as wide as the drawn chip (default `text`): a wider stub wraps the line, then unwraps it. */
  stubText?(data: T): string
  /** A block of this kind opens its edit (the source, the file name) as soon as the command inserts it (J5.7). */
  openOnInsert?: boolean
  /** Second line of the slash menu item: what the block is for. */
  description?: string
  /** Registry name for the insert action, without the `svg:` prefix. A module without one gets a generic box. */
  icon?: MarkdownIconName
  /** Extra slash search terms of the insert action (the label always matches); case and diacritics are ignored. */
  keywords?: readonly string[]
  /** `/graf koláč`: the typed text after the name picks the inserted data; nothing when it names no choice (no row). */
  parameter?: { label: string; data(text: string): T | undefined; title(text: string): string }
  watchDocument?: boolean
  insertCommand?: MarkdownPluginCommand<T>
  normalize(value: unknown): T
  /**
   * The structured validator published for `data` in the schema manifest (describeMarkdownSchema), which a server
   * validating stored documents implements by this name. Default `${id}:${name}`, which such a server must register.
   */
  dataValidator?: string
  text(data: T): string
  /**
   * Markdown source for `data` when the editor's Markdown dialect parses it back to the same data, for
   * example a ```mermaid fence that `parseHTML` claims. `undefined` keeps the `text` fallback and marks the
   * block as Markdown-lossy. A block's source is written line by line under its container's prefix, an
   * inline module's source is one line. `document` is the document being written or checked (absent when
   * unknown), for data that refers to other nodes such as a chart's linked table.
   */
  markdown?(data: T, document?: PMNode): string | undefined
  html?(data: T): DOMOutputSpec
  load(): Promise<MarkdownModuleRuntime<T>>
  extensions?: readonly AnyExtension[]
  /**
   * Extra pasted/imported DOM recognized as this module, such as the TeX math wrappers core emits for
   * Markdown `$…$`/`$$…$$` imports. Data failing `normalize` leaves the element to the default parser; for a
   * dialect fence or link (`data-md-fence`/`data-md-link`) the failure is also a dialect diagnostic.
   */
  parseHTML?: readonly { readonly tag: string; data(element: HTMLElement): unknown }[]
  /**
   * The module's `a-` component in the Markdown dialect, written and read by the plugin's lazily loaded form
   * (`registerMarkdownDialectForm`) in place of `markdown`: `tag` is the element core marks for the component,
   * `lossy` (eager, so fidelity checks never load the form) says when `write` returns `undefined`.
   */
  dialect?: {
    readonly tag: string
    lossy?(data: T, document?: PMNode): boolean
    write(data: T, document?: PMNode): string | undefined
    read(element: HTMLElement): unknown
  }
}

function assertJsonData(value: unknown, ancestors = new Set<object>()): void {
  if (value === null || typeof value === 'string' || typeof value === 'boolean' || (typeof value === 'number' && Number.isFinite(value))) return
  if (!value || typeof value !== 'object' || ancestors.has(value)) throw new TypeError('Module data must be finite, acyclic JSON values.')
  const prototype = Object.getPrototypeOf(value)
  if ((!Array.isArray(value) && prototype !== Object.prototype && prototype !== null) || Object.getOwnPropertySymbols(value).length) throw new TypeError('Module data must contain plain JSON objects.')
  ancestors.add(value)
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index++) assertJsonData(value[index], ancestors)
  } else {
    for (const property of Object.values(Object.getOwnPropertyDescriptors(value))) {
      if (!property.enumerable) continue
      if (!('value' in property)) throw new TypeError('Module data cannot contain accessors.')
      assertJsonData(property.value, ancestors)
    }
  }
  ancestors.delete(value)
}

/** Data/schema remain synchronous; the actual renderer and form are loaded on first use. */
export function defineMarkdownBlockModule<T>(options: MarkdownBlockModuleOptions<T>) {
  const normalize = (value: unknown): T => {
    const normalized = options.normalize(value)
    assertJsonData(normalized)
    return structuredClone(normalized)
  }
  const defaults = normalize(options.defaults)
  const markdownSource = (value: unknown, document?: PMNode | null): string | undefined => {
    // Before the dialect loads, a dialect form throws instead of writing the lossy text.
    if (options.dialect) markdownDialect()
    try { return (options.dialect?.write ?? options.markdown)?.(normalize(value), document ?? undefined) } catch { return undefined }
  }
  const markdownLossy = (value: unknown, document: PMNode): boolean => {
    if (!options.dialect) return markdownSource(value, document) === undefined
    try { return !!options.dialect.lossy?.(normalize(value), document) } catch { return true }
  }
  if (options.insertCommand && !isCommandDefinition(options.insertCommand)) throw new TypeError('Module commands must be created with defineMarkdownCommand.')
  const load = createMarkdownModuleLoader(options.load)
  const insert = options.insertCommand ?? defineMarkdownCommand<T>({
    id: `${options.id}.insert`,
    apply: data => ({ commands, editor }) => {
      try {
        const inserted = commands.insertContent({ type: options.name, attrs: { data: normalize(data) } })
        if (inserted && options.openOnInsert) { openOnInsertRequests.set(editor, options.name); setTimeout(() => { if (openOnInsertRequests.get(editor) === options.name) openOnInsertRequests.delete(editor) }, 3000) }
        return inserted
      }
      catch { return false }
    },
  })
  function mount(context: MarkdownModuleContext<T>) {
    const lifetime = new AbortController()
    let stopRender = () => {}
    let editClean: (() => void) | void
    let toolbar: MarkdownContextToolbar | undefined
    let deleteSeparator: HTMLElement | undefined
    let stopped = false
    let retained: { view: MarkdownModuleView<T>; context: MarkdownModuleContext<T> } | undefined
    // The edit a fresh insertion asked for (J5.7): it opens once the block is drawn, not while its runtime still renders.
    let pendingEdit: (() => void) | undefined
    const doc = context.container.ownerDocument
    let document = context.document
    let editable = context.editor?.isEditable
    const contentHost = doc.createElement(options.inline ? 'span' : 'div')
    const controls = doc.createElement(options.inline ? 'span' : 'div')
    context.container.replaceChildren(contentHost, controls)
    const undoFromControls = createUndoFromControls(context.editor)
    if (context.editor) context.container.addEventListener('keydown', undoFromControls)
    // A loading stub (J8.3) is a quiet skeleton laid over the content that is being drawn, as large as the finished block; its text stays
    // readable for assistive technology and search. A message that matters (a failure, no full view) stays visible.
    const fallback = (message: string, error = false, loading = false) => {
      const element = doc.createElement(options.inline ? 'span' : 'div')
      element.dataset.moduleFallback = ''
      if (loading && !options.inline) element.classList.add('md-module-stub')
      const source = doc.createElement(options.inline ? 'span' : 'div'); source.textContent = loading && options.stubText ? options.stubText(context.data) : options.text(context.data)
      const status = doc.createElement('span'); status.textContent = ` — ${message}`; status.setAttribute('role', error ? 'alert' : 'status')
      // An inline stub is as wide as its text (J3.9): the loading message is for assistive technology only, or the line wrapped and
      // unwrapped as the chip arrived (UX open blink).
      if (loading && options.inline) status.className = 'md-module-stub-status'
      element.append(source, status)
      return element
    }
    const heightKey = options.inline || options.exactStub ? '' : (loadDrawnHeights(), heightKeyOf(options.name, context.data))
    const stubHeight = (): number => { try { return (heightKey ? drawnHeights.get(heightKey) : undefined) ?? options.stubHeight?.(context.data, context.document) ?? 0 } catch { return 0 } }
    if (!options.inline) { const height = stubHeight(); if (height > 0) contentHost.style.minHeight = `${height}px` }
    contentHost.append(fallback(modulesStrings.textPreview, false, true))
    const update = context.update ? (data: T, options?: { history?: 'typing' }) => { if (!lifetime.signal.aborted) context.update!(normalize(data), options) } : undefined
    // Bound to the node view (context.signal), not to this mount or one of its render generations.
    const nodeContext = (): MarkdownModuleNodeContext<T> => {
      const { editor, rootContainer, signal } = context
      return {
        anchor: context.container, rootContainer, data: context.data, document, signal, editor, getPos: context.getPos,
        update: context.update ? (data: T, options?: { history?: 'typing' }) => { if (!signal.aborted) context.update!(normalize(data), options) } : undefined,
        closeHistory: context.closeHistory,
        onEditableChange(listener) {
          if (signal.aborted || !editor || !rootContainer) return () => {}
          let last = editor.isEditable
          const stopObserving = observeMarkdownEditability(editor, rootContainer, () => {
            if (editor.isEditable === last || signal.aborted) return
            last = editor.isEditable; listener(last)
          })
          const end = () => { stopObserving(); signal.removeEventListener('abort', end) }
          signal.addEventListener('abort', end, { once: true })
          return end
        },
      }
    }
    // The edit session's own writes (a live field, an inline popover) come back through updateData: those keep it open.
    let editWriting = false
    const editUpdate = update ? (data: T, options?: { history?: 'typing' }) => { editWriting = true; try { update(data, options) } finally { editWriting = false } } : undefined
    const editContext: MarkdownModuleContext<T> = { ...context, update: editUpdate, container: contentHost, signal: lifetime.signal, nodeContext }
    const closeEdit = () => { const cleanup = editClean; editClean = undefined; cleanup?.() }
    let generation = 0
    const start = async () => {
      if (stopped) return false
      // Only a re-render keeps the height of what it replaces; on the first one the text preview's height needs no reserve, and reading
      // it forced a layout of the whole document per component as a page opened for editing (perf review 2026-11-02).
      // A restart reserved it already: measuring again after other modules' writes would force a layout per module.
      const reservedHeight = generation && !contentHost.style.minHeight ? contentHost.getBoundingClientRect().height : 0
      if (reservedHeight > 0) contentHost.style.minHeight = `${reservedHeight}px`
      stopRender()
      const current = ++generation
      const abort = new AbortController()
      let cleanup: (() => void) | void
      const actions: HTMLElement[] = []
      let disposed = false
      const dispose = () => {
        if (disposed) return
        disposed = true; retained = undefined; abort.abort(); actions.forEach(action => action.remove())
        const release = cleanup; cleanup = undefined; release?.()
      }
      stopRender = dispose
      // Each generation gets its own element. Late third-party writes affect detached DOM only.
      const content = doc.createElement(options.inline ? 'span' : 'div')
      content.dataset.moduleRuntime = ''
      const readable = fallback(modulesStrings.loadingFullView, false, true)
      contentHost.replaceChildren(readable, content); controls.replaceChildren()
      const local: MarkdownModuleContext<T> = { ...context, document, container: content, signal: abort.signal, nodeContext,
        update: update ? (data: T, options?: { history?: 'typing' }) => { if (!disposed && current === generation) update(data, options) } : undefined,
      }
      try {
        const runtime = await load()
        if (stopped || disposed || current !== generation) return false
        if (!toolbar && context.editor && context.getPos && context.update) {
          const edit = (anchor: HTMLElement) => {
            if (!stopped && context.editor?.isEditable) {
              closeEdit(); editContext.document = document; editContext.anchor = anchor; editContext.toolbar = toolbar
              editClean = runtime.edit!(editContext)
            }
          }
          // The block itself is editable where it is shown: a click on its own content or Enter/F2 opens the edit, a second one closes it
          // (applying a changed form, D3).
          const editInPlace = () => {
            const active = activeMarkdownPopover(context.editor!)
            if (active?.anchor === context.container) leaveMarkdownPopover(active); else edit(context.container)
          }
          toolbar = createMarkdownContextToolbar({ editor: context.editor, anchor: context.container, getPos: context.getPos, label: options.label, signal: lifetime.signal,
            onActivate: runtime.edit ? editInPlace : undefined, activateOnClick: runtime.editOnClick !== false })
          if (runtime.edit) {
            context.container.classList.add('md-module-editable')
            toolbar.addButton({ label: modulesStrings.editBlockLabel(options.label), icon: 'settings', text: options.label, onClick: edit })
            const selection = context.editor.state.selection
            if (openOnInsertRequests.get(context.editor) === options.name && selection instanceof NodeSelection && selection.from === context.getPos()) {
              openOnInsertRequests.delete(context.editor); pendingEdit = () => edit(context.container)
            }
          }
          // An inline chip with a text of its own (registerMarkdownChipText) goes back to that text, as every chip does.
          const chip = context.editor.state.doc.nodeAt(context.getPos() ?? -1)
          if (options.inline && markdownChipText(chip) !== undefined) {
            toolbar.addButton({ label: modulesStrings.chipToTextLabel(chipToTextShortcut()), icon: 'pilcrow', onClick() {
              const position = context.getPos?.()
              if (position !== undefined) convertMarkdownChipToText(context.editor!, position)
            } })
          }
          toolbar.addSeparator()
          deleteSeparator = toolbar.element.lastElementChild as HTMLElement
          const remove = toolbar.addButton({ label: modulesStrings.removeBlockLabel(options.label), icon: 'trash', onClick() {
            const position = context.getPos?.()
            if (position === undefined || !context.editor?.isEditable) return
            const current = context.editor.state.doc.nodeAt(position)
            if (current?.type.name === options.name) {
              dispatchMarkdownHistoryOperation(context.editor, context.editor.state.tr.deleteRange(position, position + current.nodeSize))
              context.editor.view.focus()
            }
          } })
          remove.classList.add('bb-danger')
        }
        if (toolbar) {
          const owned = toolbar
          local.toolbar = { ...owned,
            addButton(options) { const button = owned.addButton(options); if (deleteSeparator) owned.element.insertBefore(button, deleteSeparator); actions.push(button); return button },
            addSeparator() { owned.addSeparator(); const separator = owned.element.lastElementChild; if (separator) { if (deleteSeparator) owned.element.insertBefore(separator, deleteSeparator); actions.push(separator as HTMLElement) } },
          }
          owned.refresh()
        }
        content.replaceChildren()
        const release = await runtime.render(local)
        const destroy = typeof release === 'function' ? release : release ? () => release.destroy() : undefined
        if (stopped || disposed || current !== generation) { destroy?.(); return false }
        cleanup = destroy
        if (release && typeof release !== 'function') retained = { view: release, context: local }
        if (pendingEdit) { const run = pendingEdit; pendingEdit = undefined; queueMicrotask(() => { if (!stopped && !disposed && current === generation) run() }) }
        const prepared = release && typeof release !== 'function' && release.prepare ? await release.prepare() : true
        if (stopped || disposed || current !== generation) return false
        if (release && typeof release !== 'function' && retained?.view === release && retained.context !== local) return false
        if (!prepared || !content.hasChildNodes()) {
          if (content.hasChildNodes() && retained?.view.fallback === 'runtime') readable.remove()
          else { readable.classList.remove('md-module-stub'); readable.replaceChildren(fallback(modulesStrings.fullViewUnavailable)) }
          return false
        }
        readable.remove(); contentHost.style.minHeight = ''
        // One read per drawn block, after the frame that showed it: the next copy or visit starts at this height.
        if (!options.inline) doc.defaultView?.requestAnimationFrame(() => { const height = contentHost.getBoundingClientRect().height; if (height > 0 && !stopped && heightKey) { if (drawnHeights.size > 400) drawnHeights.clear(); drawnHeights.set(heightKey, Math.round(height)); saveDrawnHeights() } })
        return true
      } catch {
        if (stopped || disposed || current !== generation) return false
        dispose()
        // Detach the failed renderer's element before installing the stable fallback.
        contentHost.replaceChildren(fallback(modulesStrings.renderFailed, true))
        const retry = doc.createElement('button'); retry.type = 'button'; retry.dataset.moduleRetry = ''
        retry.textContent = markdownStrings.retry; retry.onclick = () => activation.requestRender(); controls.append(retry)
        return false
      }
    }
    const updateReadiness = (current: NonNullable<typeof retained>, prepared: boolean) => {
      if (retained !== current || stopped) return false
      const ready = prepared && current.context.container.hasChildNodes()
      contentHost.querySelector('[data-module-fallback]')?.remove()
      if (!ready && !(current.view.fallback === 'runtime' && current.context.container.hasChildNodes())) {
        contentHost.prepend(fallback(modulesStrings.fullViewUnavailable))
      }
      else contentHost.style.minHeight = ''
      return ready
    }
    const activation = createMarkdownModuleActivation({ container: context.container, signal: lifetime.signal, inline: options.inline, render: start,
      async prepare() {
        const current = retained
        if (!current) return !contentHost.querySelector('[data-module-fallback]') && !!contentHost.querySelector('[data-module-runtime]')?.hasChildNodes()
        const capturedContext = current.context
        try {
          const ready = current.view.prepare ? await current.view.prepare() : true
          return current.context === capturedContext ? updateReadiness(current, ready) : false
        }
        catch { return current.context === capturedContext ? updateReadiness(current, false) : false }
      },
    })
    const restart = (height = contentHost.getBoundingClientRect().height) => {
      if (height > 0) contentHost.style.minHeight = `${height}px`
      stopRender()
      contentHost.replaceChildren(fallback(modulesStrings.waitingForFullView, false, true))
      activation.requestRender()
    }
    const stopEditable = context.editor && context.rootContainer ? observeMarkdownEditability(context.editor, context.rootContainer, () => {
      if (editable !== context.editor?.isEditable) {
        editable = context.editor?.isEditable; closeEdit()
        // Not rendered yet (still away from the viewport): its text preview has no affordance to change.
        if (!generation) return
        // A view that toggles its own edit controls flips in place (contract J1.2); the others restart, batched below.
        if (retained?.view.editable) { retained.view.editable(!!editable); return }
        // Every module of the page restarts at once: each reads its height now and writes in a microtask, so the page is laid out
        // once, not once per module (J12.10: 16 contact cards forced 16 layouts, ~85 ms of a 150-block page's toggle).
        const height = contentHost.getBoundingClientRect().height
        queueMicrotask(() => { if (!stopped) restart(height) })
      }
    }) : undefined
    const stop = () => {
      if (stopped) return
      stopped = true; context.signal.removeEventListener('abort', stop); stopEditable?.(); activation.destroy()
      lifetime.abort(); toolbar?.destroy(); context.container.classList.remove('md-module-editable'); context.container.removeEventListener('keydown', undoFromControls)
      // Remove the live generation first, even when an async renderer ignores its abort signal.
      contentHost.replaceChildren(); controls.replaceChildren()
      try { closeEdit() } finally { stopRender() }
    }
    context.signal.addEventListener('abort', stop, { once: true })
    if (context.signal.aborted) stop()
    const refresh = (nextDocument: MarkdownReadonlyJSONContent) => {
      document = nextDocument
      if (!retained) { restart(); return }
      retained.context = { ...retained.context, data: context.data, document }
      try {
        const current = retained
        current.view.update(current.context)
        if (retained === current && !stopped) {
          if (current.view.prepare) {
            contentHost.querySelector('[data-module-fallback]')?.remove()
            if (current.view.fallback !== 'runtime' || !current.context.container.hasChildNodes()) {
              contentHost.prepend(fallback(modulesStrings.preparingNewPreview))
            }
            activation.requestPreparation()
          }
          else activation.setReady(updateReadiness(current, true))
        }
      }
      catch { restart() }
    }
    return { destroy: stop, refresh, activate: activation.activate, updateData(data: T, nextDocument: MarkdownReadonlyJSONContent) {
      // A runtime without a retained view re-renders; its own edit's write re-renders here, so the edit stays open.
      // An edit the person is typing in stays open too (an undo from its field, a collaborator): its view syncs the field.
      const typing = !!editClean && !!context.editor && markdownOverlaysFor(context.editor.view.dom).some(overlay => overlay.contains(doc.activeElement))
      if (stopped || (!retained && !editWriting && !typing)) return false
      if (!editWriting && !typing) closeEdit()
      context.data = normalize(data); editContext.data = context.data
      refresh(nextDocument); return true
    } }
  }
  const createPlugin = (): MarkdownPlugin => ({
    id: options.id, apiVersion: 1, schemaVersion: 1, profiles: ['rich'],
    create() {
      const node = Node.create({
        name: options.name, group: options.inline ? 'inline' : 'block', inline: !!options.inline, atom: true, draggable: !options.inline,
        addKeyboardShortcuts(): Record<string, () => boolean> {
          return options.inline ? {} : {
            Backspace: () => selectNeighbouringBlock(this.editor, options.name, false),
            Delete: () => selectNeighbouringBlock(this.editor, options.name, true),
          }
        },
        addAttributes: () => ({ data: { default: defaults, rendered: false,
          validate: defineMarkdownAttributeValidator({ kind: 'structured', validator: options.dataValidator ?? `${options.id}:${options.name}` }, value => { normalize(value) }) } }),
        parseHTML: () => [{ tag: `[data-md-module="${options.name}"]`, getAttrs: element => {
          try { return { data: normalize(JSON.parse(element.getAttribute('data-value') ?? 'null')) } } catch { return false }
        } }, ...[...options.parseHTML ?? [], ...options.dialect ? [{ tag: options.dialect.tag, data: options.dialect.read }] : []].map(source => ({ tag: source.tag, getAttrs: (element: HTMLElement) => {
          try { return { data: normalize(source.data(element)) } } catch (error) { loadedMarkdownDialect()?.reportMarkdownDialectFailure(element, error); return false }
        } }))],
        renderHTML: ({ node }) => {
          const data = normalize(node.attrs.data)
          return [options.inline ? 'span' : 'section', {
            'data-md-module': options.name, 'data-value': JSON.stringify(data), class: 'md-module',
          }, options.html?.(data) ?? [options.inline ? 'span' : 'p', {}, options.text(data)]]
        },
        renderText: ({ node }) => options.text(normalize(node.attrs.data)),
        addStorage: () => ({ markdown: { serialize(state: { text(value: string, escape?: boolean): void; closeBlock(node: PMNode): void }, node: PMNode) {
          const source = markdownSource(node.attrs.data, serializedMarkdownDocument())
          if (source !== undefined) state.text(source, false)
          else state.text(options.text(normalize(node.attrs.data)))
          if (!options.inline) state.closeBlock(node)
        } }, /** The module's label, e.g. for the page source's protected block labels. */ label: options.label }),
        addNodeView: () => ({ node: initial, editor, getPos, view }) => {
          const dom = view.dom.ownerDocument.createElement(options.inline ? 'span' : 'section')
          dom.className = 'md-module'; dom.dataset.mdModule = options.name; dom.contentEditable = 'false'
          let node = initial
          let lastDocument = editor.state.doc
          let mounted: ReturnType<typeof mount> | undefined
          const lifetime = new AbortController()
          const render = () => {
            mounted?.destroy()
            lastDocument = editor.state.doc
            mounted = mount({ container: dom, rootContainer: view.dom, data: normalize(node.attrs.data), document: moduleDocumentSnapshot(editor.state.doc, editor.state),
              signal: lifetime.signal, editor, getPos,
              closeHistory() {
                if (!lifetime.signal.aborted && !editor.isDestroyed) editor.view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
              },
              update(data, updateOptions) {
                const position = getPos()
                if (position === undefined || editor.isDestroyed || !editor.isEditable) return
                const current = editor.state.doc.nodeAt(position)
                if (current?.type.name !== options.name) return
                const { selection } = editor.state
                const transaction = editor.state.tr.setNodeMarkup(position, undefined, { ...current.attrs, data: normalize(data) })
                // The atom is replaced, which drops its selection into the text: the edited element stays selected (P7).
                if (selection instanceof NodeSelection && selection.from === position) transaction.setSelection(NodeSelection.create(transaction.doc, position))
                if (updateOptions?.history === 'typing') editor.view.dispatch(transaction)
                else dispatchMarkdownHistoryOperation(editor, transaction)
              },
            })
          }
          // A block that follows the document (the table of contents) reads it once typing pauses, not per keystroke (J12.6).
          let watching: ReturnType<typeof setTimeout> | undefined
          const refreshWatched = () => { watching = undefined; if (!editor.isDestroyed && lastDocument !== editor.state.doc) { lastDocument = editor.state.doc; mounted?.refresh(moduleDocumentSnapshot(editor.state.doc, editor.state)) } }
          const onUpdate = () => { if (options.watchDocument) { clearTimeout(watching); watching = setTimeout(refreshWatched, WATCH_IDLE_MS) } }
          editor.on('update', onUpdate); render()
          // The right button is the editor's one menu (P2) on the chip or title button too; inside a field or link it stays the browser's.
          return { dom, stopEvent: event => event.type !== 'contextmenu' && !!(event.target as HTMLElement)?.closest('button,input,textarea,select,a,details'), ignoreMutation: () => true,
            selectNode() { dom.classList.add('ProseMirror-selectednode'); mounted?.activate() },
            deselectNode() { dom.classList.remove('ProseMirror-selectednode') },
            update(next) {
              if (next.type !== node.type) return false
              if (!next.eq(node)) {
                node = next; lastDocument = editor.state.doc
                if (!mounted?.updateData(normalize(node.attrs.data), moduleDocumentSnapshot(editor.state.doc, editor.state))) render()
              }
              return true
            },
            destroy() { lifetime.abort(); clearTimeout(watching); mounted?.destroy(); editor.off('update', onUpdate) },
          }
        },
      })
      const action = { id: `${options.id}.insert`, label: options.label, ...options.description ? { description: options.description } : {}, icon: `svg:${options.icon ?? 'box'}`,
        group: 'content', variants: ['document'] as const, command: insert(defaults),
        ...options.keywords?.length ? { keywords: [...options.keywords] } : {},
        ...options.parameter ? { parameter: { get label() { return options.parameter!.label }, title: (text: string) => options.parameter!.title(text),
          command: (text: string) => { const data = options.parameter!.data(text); return data === undefined ? undefined : insert(data) } } } : {} }
      return { extensions: [node, ...(options.extensions ?? [])], commands: [insert], actions: { slash: [action], toolbar: [action] },
        markdownLossy(document) {
          let found = false
          document.descendants(node => { if (!found && node.type.name === options.name && markdownLossy(node.attrs.data, document)) found = true })
          return found
        },
        onRender({ container, document, signal }) {
          const cleanups = [...container.querySelectorAll<HTMLElement>(`[data-md-module="${options.name}"]`)].map(element => mount({
            container: element, rootContainer: container, data: normalize(JSON.parse(element.dataset.value ?? 'null')), document, signal,
          }))
          return () => cleanups.reverse().forEach(mounted => mounted.destroy())
        },
      }
    },
  })
  return { createPlugin, insert, node: (data: T): JSONContent => ({ type: options.name, attrs: { data: normalize(data) } }) }
}
