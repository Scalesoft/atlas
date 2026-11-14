import type { Editor } from '@tiptap/core'
import { NodeSelection, Selection, type Transaction } from '@tiptap/pm/state'
import { autoUpdate, computePosition, flip, hide, offset, shift, size, type VirtualElement } from '@floating-ui/dom'
import { markdownOverlaysFor, registerMarkdownOverlay } from '../../core/overlay'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { activeMarkdownPopover, bindMarkdownMenu, leaveMarkdownPopover, registerMarkdownPopover } from './popoverInteraction'
import { isMarkdownComposition, trackMarkdownComposition } from './composition'
import { createUndoFromControls } from './undoFromControls'
import { createIconElement, legacyIconName } from '../../ui/iconMarkup'
import { deleteIntention, registerMarkdownIntentions, type MarkdownIntention } from '../intentions/intentions'

export interface MarkdownContextToolbar {
  element: HTMLElement
  addButton(options: { label: string; icon?: string; text?: string; onClick: (button: HTMLButtonElement) => void; disabled?: boolean }): HTMLButtonElement
  addSeparator(): void
  refresh(): void
  destroy(): void
}

/** Draws a toolbar icon from the one registry; legacy keys (`'trash'`) still resolve. */
export function markdownContextIcon(doc: Document, name: string): SVGSVGElement {
  return createIconElement(doc, legacyIconName(name), 16)
}

/** A DOM trigger or a live selection/caret rectangle; never serialized into document content. */
export type MarkdownPopoverAnchor = HTMLElement | VirtualElement

/** A toolbar's „Převést na text“ (by its icon) is the menu's shared row: core's (first in the menu, with its own word) stands for it. */
const sharedRows: Readonly<Record<string, { id: string; priority: number }>> = { pilcrow: { id: 'core.chip-to-text', priority: 0 } }
let toolbarCount = 0

/** `frames: false`: a virtual anchor is followed through its `contextElement` (resize, move, scroll) instead of read every frame; the returned stop's `update` re-places it. */
function place(anchor: MarkdownPopoverAnchor, element: HTMLElement, placement: 'top-start' | 'bottom-start' | 'inside-top-end' | 'right', alive: () => boolean, frames = true): (() => void) & { update: () => void } {
  let revision = 0
  const update = () => {
    const current = ++revision
    const inside = placement === 'inside-top-end'
    // A popover above or below its anchor never slides over it (a block's detail covered the row it edits): too tall for either
    // side, it keeps the roomier one and scrolls (size). A toolbar inside its block follows the block's top into view.
    void computePosition(anchor, element, { placement: inside ? 'top-end' : placement, strategy: 'fixed', middleware: [inside ? offset(({ rects }) => -rects.floating.height - 6) : offset(6), ...inside || placement === 'right' ? [] : [flip()], shift({ padding: 12, crossAxis: inside }),
      size({ padding: 12, apply({ availableHeight }) { if (alive()) element.style.maxHeight = `${Math.max(80, availableHeight)}px` } }),
      hide(),
    ] }).then(({ x, y, middlewareData }) => {
      if (alive() && current === revision) {
        // Its anchor scrolled out of sight: shift kept the toolbar at the window's edge, over the host's bar (a heading's „Kopírovat odkaz“
        // over the page's actions). It goes with its anchor, unless the focus is in it. An offset above zero: a page without layout has none.
        const away = !!middlewareData.hide?.referenceHidden && Object.values(middlewareData.hide.referenceHiddenOffsets ?? {}).some(by => by > 0)
        Object.assign(element.style, { left: `${x}px`, top: `${y}px`, visibility: away && !element.contains(element.ownerDocument.activeElement) ? 'hidden' : '' })
        element.dataset.positioned = 'true'
      }
    }).catch(() => {})
  }
  const win = element.ownerDocument.defaultView
  const stop = autoUpdate(anchor, element, update, { elementResize: typeof win?.ResizeObserver === 'function', layoutShift: typeof win?.IntersectionObserver === 'function', animationFrame: frames && !('nodeType' in anchor) && typeof requestAnimationFrame === 'function' })
  return Object.assign(() => { revision++; stop() }, { update })
}

/**
 * Removes a closed popover at once (its listeners, focus and content go as before) and lets a lifeless copy of it leave the way
 * it came (md-popover-leave, controls.css); without that animation (no styles, reduced motion at 0 ms) the copy goes too.
 */
function leaveAnimated(element: HTMLElement): void {
  if (!element.isConnected || !element.dataset.positioned) { element.remove(); return }
  const ghost = element.cloneNode(true) as HTMLElement
  const fields = element.querySelectorAll<HTMLInputElement>('input,textarea')
  ghost.querySelectorAll<HTMLInputElement>('input,textarea').forEach((field, index) => { field.value = fields[index]?.value ?? '' })
  ghost.setAttribute('aria-hidden', 'true'); ghost.inert = true; ghost.dataset.leaving = 'true'
  // The copy shares no id, name or label target with the page it stays in for a moment.
  for (const node of [ghost, ...ghost.querySelectorAll('[id],[name],[for]')]) for (const name of ['id', 'name', 'for']) node.removeAttribute(name)
  element.replaceWith(ghost)
  const style = ghost.ownerDocument.defaultView?.getComputedStyle(ghost)
  const seconds = style?.animationName === 'md-popover-leave' ? parseFloat(style.animationDuration) : 0
  if (!(seconds > 0)) { ghost.remove(); return }
  const end = (event: Event) => { if (event.target === ghost) ghost.remove() }
  ghost.addEventListener('animationend', end); ghost.addEventListener('animationcancel', end)
  // A page whose animations do not run (a tab in the background) still loses the copy.
  setTimeout(() => ghost.remove(), seconds * 1000 + 100)
}

/** One transient, selection-owned toolbar. It never becomes document content. */
export function createMarkdownContextToolbar(options: { editor: Editor; anchor: HTMLElement; getPos: () => number | undefined; label: string; signal?: AbortSignal; isVisible?: () => boolean; selectOnClick?: boolean; markAnchor?: boolean
  /** Default `top-start`. A block whose anchor sits directly under a previous sibling (e.g. a
   *  line right under its neighbour) should open `bottom-start` instead — `flip()` only reacts to the
   *  viewport edge, not to the neighbouring block, so the default would draw over it. */
  placement?: 'top-start' | 'bottom-start'
    /** `inside-top-end`: over the block's own top-right corner, so a large block (image, code) hides no line of its neighbour above. */ | 'inside-top-end'
    /** `right`: beside `positionAnchor` (a heading's text end), covering neither the line nor its neighbours. */ | 'right'
  /** Where the toolbar stands when not at `anchor` itself (a place in its text). */
  positionAnchor?: MarkdownPopoverAnchor
  /** The component's primary action (e.g. edit), run after a click selects it and on Enter/F2 while it is the
   *  selected node. A click the component's own content already handled (`preventDefault`) does not run it. */
  onActivate?: () => void
  /** `false`: a click only selects the block; its primary action stays with Enter/F2 (a chart or a drawing whose surface is read, not edited). Default true. */
  activateOnClick?: boolean
  /** A virtual `positionAnchor` read every frame while shown forced a layout of the page each frame (a heading's chips, 3.6 perf): `false`
   *  follows its `contextElement` instead and re-places after the frame of a `refresh` that found its node changed (an edit in it). Default true. */
  followFrames?: boolean }): MarkdownContextToolbar {
  const { editor, anchor } = options
  const doc = anchor.ownerDocument
  const editorElement = editor.view.dom
  const element = doc.createElement('div'); element.className = 'md-context-toolbar bubble-menu'; element.hidden = true
  element.setAttribute('role', 'toolbar'); element.setAttribute('aria-label', options.label)
  // The theme is copied when the toolbar shows (refresh): reading ~40 computed properties here, for every component of a page
  // that is not laid out yet, cost seconds on entering editing (perf 3.5).
  doc.body.append(element)
  const unregister = registerMarkdownOverlay(editor, element, { consumesEscape: false })
  let destroyed = false; let positioning: ReturnType<typeof place> | undefined; let replacing = false
  // `followFrames: false`: the node the toolbar was last placed for. Its own change can move the place in its text (the heading's text end);
  // the block moving or resizing as a whole is autoUpdate's (layoutShift, elementResize), so another transaction places nothing.
  let placedFor: unknown
  const anchorNode = () => { const at = options.getPos(); return at === undefined ? undefined : editor.state.doc.nodeAt(at) }
  const refresh = () => {
    if (destroyed || editor.isDestroyed) return
    const selection = editor.state.selection
    const focus = doc.activeElement
    const focused = editor.view.dom.contains(focus) || element.contains(focus) || markdownOverlaysFor(editor.view.dom).some(overlay => overlay.contains(focus))
    const visible = editor.isEditable && anchor.isConnected && (options.isVisible ? options.isVisible() : selection instanceof NodeSelection && selection.from === options.getPos() && focused)
    if (options.markAnchor !== false) anchor.classList.toggle('md-component-selected', visible)
    if (element.hidden === !visible) {
      // Read once the frame that drew the change has painted: its layout is done, the read forces none.
      if (visible && options.followFrames === false && !replacing && anchorNode() !== placedFor) {
        placedFor = anchorNode(); replacing = true; requestAnimationFrame(() => setTimeout(() => { replacing = false; if (!element.hidden) positioning?.update() }))
      }
      return
    }
    element.hidden = !visible
    positioning?.(); positioning = undefined
    if (visible) { placedFor = anchorNode(); copyMarkdownOverlayTheme(editor.view.dom, element); positioning = place(options.positionAnchor ?? anchor, element, options.placement ?? 'top-start', () => !destroyed && !element.hidden, options.followFrames !== false) }
  }
  const select = (event: MouseEvent) => {
    if (options.selectOnClick === false || destroyed || !editor.isEditable || (event.target as Element)?.closest('button,a,input,select,textarea,summary,[data-md-no-select]')) return
    const position = options.getPos()
    if (position === undefined) return
    const handled = event.defaultPrevented
    // A click the runtime handled may have put the focus into a field of the block or its overlay: it stays there.
    const active = doc.activeElement
    const kept = handled && !!active && active !== editor.view.dom && (anchor.contains(active) || markdownOverlaysFor(editor.view.dom).some(overlay => overlay.contains(active)))
    event.preventDefault(); editor.commands.setNodeSelection(position); if (!kept) editor.view.focus(); refresh()
    if (!handled && options.activateOnClick !== false) options.onActivate?.()
  }
  // Capture: ProseMirror's own Enter (a paragraph next to the node) runs in its bubble listener on the same element.
  const activate = (event: KeyboardEvent) => {
    if (destroyed || event.target !== editor.view.dom || !['Enter', 'F2'].includes(event.key)
      || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey || isMarkdownComposition(event, editor)) return
    if (!(editor.state.selection instanceof NodeSelection) || editor.state.selection.from !== options.getPos()) return
    // A reader's primary action is the block's first read action (zoom, copy, open), as the double click is.
    if (!editor.isEditable) { const action = anchor.querySelector<HTMLElement>('.md-read-action'); if (action) { event.preventDefault(); event.stopImmediatePropagation(); action.click() } return }
    if (!options.onActivate) return
    // A suggestion list open at the editor (an unresolved contact's people) takes its Enter.
    if (markdownOverlaysFor(editor.view.dom).some(overlay => overlay.isConnected && overlay.classList.contains('md-suggestion-popup'))) return
    event.preventDefault(); event.stopImmediatePropagation(); options.onActivate()
  }
  const buttons = () => [...element.querySelectorAll<HTMLButtonElement>('button')]
    .filter(button => !button.disabled && !button.hidden && !button.closest('[hidden]'))
  const fromEditor = (event: KeyboardEvent) => {
    if (event.defaultPrevented || event.target !== editor.view.dom || event.key !== 'Tab' || event.shiftKey || event.altKey || event.ctrlKey || event.metaKey || isMarkdownComposition(event, editor)) return
    refresh()
    if (element.hidden || !(editor.state.selection instanceof NodeSelection) || editor.state.selection.from !== options.getPos()) return
    const first = buttons()[0]
    if (first) { event.preventDefault(); first.focus({ preventScroll: true }) }
  }
  // J4.5: Tab goes on through the block's own controls (read actions, a chip) and then out of the block into the text after it,
  // never off the page; Shift+Tab goes back the same way.
  const own = () => [...anchor.querySelectorAll<HTMLElement>('.md-read-action, summary, a[href], button, input, select, textarea, [role=button]')]
    .filter(control => !control.closest('[hidden], [inert]') && control.getClientRects().length > 0 && !(control as HTMLButtonElement).disabled)
  const leave = () => {
    const position = options.getPos(), node = position === undefined ? null : editor.state.doc.nodeAt(position)
    if (position === undefined || !node) { editor.view.focus(); return }
    editor.view.dispatch(editor.state.tr.setSelection(Selection.near(editor.state.doc.resolve(position + node.nodeSize), 1)).scrollIntoView()); editor.view.focus()
  }
  const throughBlock = (event: KeyboardEvent) => {
    if (event.defaultPrevented || event.key !== 'Tab' || event.altKey || event.ctrlKey || event.metaKey || destroyed || !editor.isEditable || isMarkdownComposition(event, editor)) return
    const target = event.target as HTMLElement, controls = own(), index = controls.indexOf(target)
    if (index < 0 || target.closest('input, textarea, select')) return
    if (!event.shiftKey && index === controls.length - 1) { event.preventDefault(); leave() }
    else if (event.shiftKey && index === 0) { event.preventDefault(); editor.view.focus() }
  }
  const keyboard = (event: KeyboardEvent) => {
    if (event.defaultPrevented || isMarkdownComposition(event, editor)) return
    if (event.key === 'Tab' && !event.shiftKey && !event.altKey && !event.ctrlKey && !event.metaKey && doc.activeElement === buttons().at(-1)) {
      event.preventDefault(); event.stopPropagation(); const next = own()[0]; if (next) next.focus({ preventScroll: true }); else leave(); return
    }
    if (event.key === 'Escape' || event.key === 'Tab' && event.shiftKey && doc.activeElement === buttons()[0]) {
      event.preventDefault(); event.stopPropagation(); editor.view.focus(); return
    }
    if (!(event.target instanceof doc.defaultView!.HTMLElement) || !event.target.matches('button')) return
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
    const items = buttons(), index = items.indexOf(doc.activeElement as HTMLButtonElement)
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
      : (index + (event.key === 'ArrowLeft' ? items.length - 1 : 1)) % items.length
    event.preventDefault(); items[next]?.focus({ preventScroll: true })
  }
  // Esc from the component's own controls (a card's chips, Detail) goes back to the selected component, like Esc from the toolbar.
  const fromOwn = (event: KeyboardEvent) => {
    const position = options.getPos()
    if (event.defaultPrevented || event.key !== 'Escape' || !editor.isEditable || position === undefined || !(event.target instanceof doc.defaultView!.HTMLElement) || !event.target.matches('button, a[href], [tabindex]')) return
    // A block's own title (a [tabindex] field) of an already selected block leaves Esc to the ladder: the caret goes after the block.
    if (!event.target.matches('button, a[href]') && editor.state.selection instanceof NodeSelection && editor.state.selection.from === position) return
    event.preventDefault(); event.stopPropagation(); editor.commands.setNodeSelection(position); editor.view.focus()
  }
  anchor.addEventListener('keydown', fromOwn)
  editorElement.addEventListener('keydown', fromEditor)
  editorElement.addEventListener('keydown', activate, true)
  element.addEventListener('keydown', keyboard)
  anchor.addEventListener('keydown', throughBlock)
  // P6: Ctrl+Z / Ctrl+Y on a toolbar button (an action that applied at once, the focus still on it) undo the document. Gone with the element.
  element.addEventListener('keydown', createUndoFromControls(editor))
  // „Co s tím jde dělat“ on the selected element offers what this toolbar does, button by button (the mouse's path stays the toolbar).
  const key = ++toolbarCount
  const stopIntentions = registerMarkdownIntentions(editor, ({ block }) => {
    const selection = editor.state.selection, selected = selection instanceof NodeSelection && selection.from === options.getPos()
    // A toolbar at the caret (a heading's „Kopírovat odkaz“, a code block's language) also joins the block's own menu (J4.9).
    if (destroyed || !anchor.isConnected || !(selected || block && options.isVisible) || options.isVisible?.() === false) return []
    return [...element.querySelectorAll<HTMLButtonElement>('button')].filter(button => !button.disabled && !button.hidden).map((button, index): MarkdownIntention => {
      const icon = button.querySelector('svg')?.getAttribute('data-icon'), shared = icon ? sharedRows[icon] : undefined
      // The toolbar's trash runs the element's own delete (its hooks, its „Zpět“) under core's row and word.
      if (icon === 'trash-2') return deleteIntention(editor, () => button.click())
      return { id: shared?.id ?? `toolbar.${key}.${index}`, label: button.getAttribute('aria-label') || button.textContent || '', ...icon ? { icon: `svg:${icon}` } : {},
        ...shared ? { priority: shared.priority } : {}, run: () => button.click() }
    })
  })
  // Native focus transfer runs a microtask checkpoint between focusout and focusin.
  // At that instant activeElement is body; hiding the toolbar would close its
  // popover before the clicked button can receive focus or dispatch its action.
  let refreshTimer: ReturnType<typeof setTimeout> | undefined
  const deferRefresh = () => {
    if (refreshTimer !== undefined) clearTimeout(refreshTimer)
    refreshTimer = setTimeout(() => { refreshTimer = undefined; refresh() }, 0)
  }
  const observer = doc.defaultView?.MutationObserver ? new doc.defaultView.MutationObserver(deferRefresh) : undefined
  const destroy = () => {
    if (destroyed) return
    destroyed = true; if (refreshTimer !== undefined) clearTimeout(refreshTimer)
    // toggle(…, false), not remove(): remove() writes the class attribute even without the class, and on a text block (a heading's link
    // toolbar) ProseMirror reads that mutation as an edit and turns the block selected by Esc into a selection of its text.
    positioning?.(); observer?.disconnect(); unregister(); stopIntentions(); element.remove(); anchor.classList.toggle('md-component-selected', false)
    anchor.removeEventListener('click', select); doc.removeEventListener('focusin', deferRefresh); doc.removeEventListener('focusout', deferRefresh)
    editorElement.removeEventListener('keydown', fromEditor); editorElement.removeEventListener('keydown', activate, true); element.removeEventListener('keydown', keyboard); anchor.removeEventListener('keydown', throughBlock); anchor.removeEventListener('keydown', fromOwn)
    editor.off('selectionUpdate', refresh); editor.off('transaction', deferRefresh); editor.off('destroy', destroy); options.signal?.removeEventListener('abort', destroy)
  }
  anchor.addEventListener('click', select); doc.addEventListener('focusin', deferRefresh); doc.addEventListener('focusout', deferRefresh)
  editor.on('selectionUpdate', refresh); editor.on('transaction', deferRefresh); editor.on('destroy', destroy); options.signal?.addEventListener('abort', destroy, { once: true })
  observer?.observe(editor.view.dom, { attributes: true, attributeFilter: ['contenteditable'] })
  element.addEventListener('mousedown', event => { event.preventDefault() })
  const toolbar: MarkdownContextToolbar = { element, refresh, destroy,
    addButton(buttonOptions) {
      const button = doc.createElement('button'); button.type = 'button'; button.className = 'bb'
      button.title = buttonOptions.label; button.setAttribute('aria-label', buttonOptions.label); button.disabled = buttonOptions.disabled ?? false
      if (buttonOptions.icon) button.append(markdownContextIcon(doc, buttonOptions.icon))
      if (buttonOptions.text) { const label = doc.createElement('span'); label.textContent = buttonOptions.text; button.append(label) }
      if (!buttonOptions.icon && !buttonOptions.text) button.textContent = buttonOptions.label
      button.addEventListener('click', () => {
        if (destroyed || !editor.isEditable || button.disabled) return
        const active = activeMarkdownPopover(editor)
        // Handle toggling before a module's edit callback cleans up its old draft.
        if (active?.anchor === button) { if (leaveMarkdownPopover(active)) button.focus({ preventScroll: true }); return }
        buttonOptions.onClick(button)
      })
      element.append(button); return button
    },
    addSeparator() { const separator = doc.createElement('span'); separator.className = 'sep'; separator.setAttribute('role', 'separator'); element.append(separator) },
  }
  if (options.signal?.aborted) destroy(); else deferRefresh()
  return toolbar
}

/**
 * Headerless, anchored controls; fields carry their own labels and preserve draft semantics. Esc closes the popover, except inside an
 * element marked `data-md-own-escape` (or under such an ancestor within the popover): that field handles Esc itself, e.g. to cancel just its own edit.
 * A panel with „Použít“ marks its form `data-md-apply` while it holds changes: a click outside or the focus leaving submits it (D3), as
 * „Použít“ does; a form its own check keeps open (an invalid value, its error at the field) keeps the panel open, otherwise it closes.
 * One at a place in the text (its anchor the editor) closes unchanged then as well, so none stays behind while the person goes on.
 */
export function openMarkdownContextPopover(options: { editor: Editor; anchor: HTMLElement; positionAnchor?: MarkdownPopoverAnchor; title: string; variant?: 'panel' | 'menu'; readonly?: boolean; trackSelection?: boolean; signal?: AbortSignal; className?: string; interactionRoots?: readonly HTMLElement[]
  /** Where the keyboard goes back when the panel closes, when not to where it was opened from (that one was redrawn by the save). */
  returnFocus?: () => HTMLElement | null | undefined
  render: (container: HTMLElement, close: () => void) => void | (() => void) }): () => void {
  const { editor, anchor } = options
  if (options.signal?.aborted || editor.isDestroyed || (!options.readonly && !editor.isEditable)) return () => {}
  const active = activeMarkdownPopover(editor)
  if (active?.anchor === anchor && active.key === options.title) { if (leaveMarkdownPopover(active)) anchor.focus({ preventScroll: true }); return () => {} }
  active?.close()
  const doc = anchor.ownerDocument
  const previous = doc.activeElement && doc.activeElement !== doc.body
    ? doc.activeElement as HTMLElement
    : anchor.matches('button,input,select,textarea,a[href],[tabindex]') ? anchor : editor.view.dom
  const menu = options.variant === 'menu'
  const element = doc.createElement('div'); element.className = `md-context-popover${menu ? ' md-context-menu' : ''} ${options.className ?? ''}`
  element.setAttribute('role', menu ? 'menu' : 'dialog'); element.setAttribute('aria-label', options.title); element.tabIndex = -1
  const body = doc.createElement('div'); body.className = 'md-context-popover-body'; element.append(body)
  const composition = trackMarkdownComposition(element)
  copyMarkdownOverlayTheme(editor.view.dom, element); doc.body.append(element)
  const unregister = registerMarkdownOverlay(editor, element)
  let closed = false; let cleanup: (() => void) | void; let positioning: (() => void) | undefined
  let releaseActive: (() => void) | undefined
  // While a click outside or the focus leaving applies the panel, its closing leaves the keyboard where the person went (P4).
  let leaving = false
  const navigation = menu ? bindMarkdownMenu(element) : undefined
  const nodeSelection = options.trackSelection !== false && editor.state.selection instanceof NodeSelection
  let position = editor.state.selection.from
  const toolbar = anchor.closest<HTMLElement>('.md-context-toolbar')
  const validate = () => {
    if (closed) return
    if (editor.isDestroyed || (!options.readonly && !editor.isEditable) || !anchor.isConnected || toolbar?.hidden || (nodeSelection && (!(editor.state.selection instanceof NodeSelection) || editor.state.selection.from !== position))) close(false)
  }
  const transaction = ({ transaction }: { transaction: Transaction }) => {
    position = transaction.mapping.map(position)
    queueMicrotask(validate)
  }
  const observer = doc.defaultView?.MutationObserver ? new doc.defaultView.MutationObserver(validate) : undefined
  const close = (restore = true, discard = false) => {
    if (closed) return
    closed = true
    element.removeEventListener('focusout', left)
    // Removing the panel fires no blur: a field still being edited is left first, so it saves as on a click outside (Esc discards).
    const focused = doc.activeElement as HTMLElement | null
    if (!discard && focused && element.contains(focused)) focused.blur?.()
    positioning?.(); observer?.disconnect(); unregister(); leaveAnimated(element)
    anchor.setAttribute('aria-expanded', 'false')
    doc.removeEventListener('pointerdown', outside, true); doc.removeEventListener('mousedown', spent, true); doc.removeEventListener('click', spent, true); doc.removeEventListener('click', released); doc.removeEventListener('pointercancel', released, true); doc.removeEventListener('keydown', keyboard, true)
    editor.off('destroy', abort); editor.off('transaction', transaction); options.signal?.removeEventListener('abort', abort)
    releaseActive?.(); navigation?.destroy(); composition.destroy()
    cleanup?.()
    // P7: back where the panel was opened from (or what redrew it); a panel closed under the keyboard (its element redrawn) gives it
    // to the editor, never to <body>. The editor takes it with its selection (view.focus), so a selected element stays selected.
    if (editor.isDestroyed || !(leaving || restore || element.contains(focused))) return
    const back = options.returnFocus?.() ?? previous
    const give = () => {
      const target = back?.isConnected && !back.closest('[hidden]') ? back : editor.view.dom
      if (target === editor.view.dom) editor.view.focus(); else if (target.isConnected) target.focus({ preventScroll: true })
    }
    // P4: a click outside leaves the keyboard where it went; a mouse click that went nowhere (plain page, <body>) and selected no
    // text gives it back once released. A touch keeps it where it went (no keyboard to return; focusing would scroll or open one).
    const nowhere = () => !editor.isDestroyed && (!doc.activeElement || doc.activeElement === doc.body)
    if (!leaving) give()
    else if (!press) setTimeout(() => { if (nowhere()) give() })
    else if (press.pointerType === 'mouse') {
      // The press ends with its release or its cancel; a cancelled one gives nothing back and leaves no listener for a later release.
      const ended = new AbortController()
      doc.addEventListener('pointerup', () => { ended.abort(); setTimeout(() => { if (nowhere() && doc.getSelection()?.isCollapsed !== false) give() }) }, { capture: true, signal: ended.signal })
      doc.addEventListener('pointercancel', () => ended.abort(), { capture: true, signal: ended.signal })
    }
  }
  const abort = () => close(false)
  // A popover at a place in the text (a chip, the caret) has the editor as its anchor: the editor around is outside it, not its opener.
  const opener = anchor === editor.view.dom ? null : anchor
  const within = (target: globalThis.Node) => element.contains(target) || !!opener?.contains(target) || !!options.interactionRoots?.some(root => root.contains(target))
  // Such a popover stands at a chip (its positionAnchor): a press on the chip is its opener's, so the second click closes it, as on a
  // real opener. A caret's place has no width: there every press in the text is outside.
  const atChip = (event: Event) => {
    if (opener || !options.positionAnchor || !('clientX' in event)) return false
    const box = options.positionAnchor.getBoundingClientRect(), { clientX: x, clientY: y } = event as MouseEvent
    return box.width > 0 && x >= box.left && x <= box.right && y >= box.top && y <= box.bottom
  }
  const pending = () => element.querySelector<HTMLFormElement>('form[data-md-apply]')
  /** D3: changes are applied as „Použít“ does; the form closes the panel, or keeps it open with the error at the field. */
  const leave = (run: () => void) => { leaving = true; try { run() } finally { leaving = false } }
  const apply = () => { const form = pending(); leave(() => { if (form) form.requestSubmit(); else close(false) }); return closed }
  // A press on the panel, its opener or its roots: the focus it moves (Chrome gives it to the editor) is no leaving; its click decides
  // (a second click on the opener applies and closes), so it ends after the click's own handlers.
  let pressed = false
  // A press outside that a form refused: the panel and the keyboard stay at the error, the rest of the press goes nowhere (D3).
  let refused = false
  // A press ends with its click, a key or its cancel (a touch that scrolled): the next click is someone else's.
  const released = () => { pressed = false; refused = false }
  // The click goes on to where it was aimed: the keyboard follows it, not back to the panel's opener.
  // The press outside that is closing the panel (its pointer decides whether the keyboard comes back).
  let press: PointerEvent | undefined
  const outside = (event: Event) => {
    pressed = within(event.target as globalThis.Node) || atChip(event); refused = false
    if (!pressed) { press = event as PointerEvent; try { refused = !apply() } finally { press = undefined } }
  }
  const spent = (event: Event) => {
    if (!refused) return
    if (event.type === 'click') refused = false
    event.preventDefault(); event.stopPropagation()
  }
  // Only a focus that went somewhere (a click on a control, a host's focus): none (the window left, a click on plain text) is no leaving.
  // Leaving applies a changed form; a popover at a place in the text, with no opener to toggle it, goes unchanged too.
  const left = (event: FocusEvent) => {
    if (closed || pressed || !(event.relatedTarget instanceof doc.defaultView!.Node) || within(event.relatedTarget)) return
    if (pending() || !opener) apply()
  }
  const keyboard = (event: KeyboardEvent) => {
    released()
    if (composition.active() || isMarkdownComposition(event, editor)) return
    if (event.key === 'Escape') { if (!element.contains((event.target as Element).closest?.('[data-md-own-escape]') ?? null)) { event.preventDefault(); event.stopPropagation(); close(true, true) } }
    else if (menu && event.key === 'Tab' && element.contains(event.target as globalThis.Node)) close()
    else if (event.key === 'Tab' && element.contains(event.target as globalThis.Node)) wrapTab(event)
  }
  // The panel sits at the end of <body>: Tab past its last control (or Shift+Tab before its first) wraps instead of leaving for <body>.
  const wrapTab = (event: KeyboardEvent) => {
    const items = [...element.querySelectorAll<HTMLElement>('input,textarea,select,button,a[href],[tabindex]:not([tabindex="-1"])')]
      .filter(item => !item.closest('[hidden]') && !(item as HTMLButtonElement).disabled && item.tabIndex >= 0)
    const edge = event.shiftKey ? items[0] : items.at(-1)
    if (!items.length || (event.target !== edge && event.target !== element)) return
    event.preventDefault(); (event.shiftKey ? items.at(-1) : items[0])!.focus({ preventScroll: true })
  }
  doc.addEventListener('pointerdown', outside, true); doc.addEventListener('mousedown', spent, true); doc.addEventListener('click', spent, true); doc.addEventListener('click', released); doc.addEventListener('pointercancel', released, true)
  doc.addEventListener('keydown', keyboard, true); element.addEventListener('focusout', left)
  // P6: Ctrl+Z on a panel's button (a choice that applied at once) undoes the document; a field keeps its own (undoFromControls).
  element.addEventListener('keydown', createUndoFromControls(editor))
  editor.on('destroy', abort); editor.on('transaction', transaction); options.signal?.addEventListener('abort', abort, { once: true })
  releaseActive = registerMarkdownPopover(editor, { anchor, key: options.title, close: abort, leave: apply })
  anchor.setAttribute('aria-haspopup', menu ? 'menu' : 'dialog'); anchor.setAttribute('aria-expanded', 'true')
  observer?.observe(editor.view.dom, { attributes: true, attributeFilter: ['contenteditable'] })
  observer?.observe(doc.body, { childList: true, subtree: true })
  if (toolbar) observer?.observe(toolbar, { attributes: true, attributeFilter: ['hidden'] })
  try {
    const release = options.render(body, () => close())
    if (closed) release?.(); else {
      cleanup = release; positioning = place(options.positionAnchor ?? anchor, element, 'bottom-start', () => !closed)
      navigation?.focus()
      if (!element.contains(doc.activeElement)) (body.querySelector<HTMLElement>('input,textarea,select,button,[tabindex="0"]') ?? element).focus({ preventScroll: true })
    }
  } catch (error) { close(false); throw error }
  return () => close()
}

/** One row of `openMarkdownRowMenu`; `checked` makes it a radio row, `detail` a muted word at the right. */
export interface MarkdownRowMenuRow { label: string; icon?: string; checked?: boolean; detail?: string; run: () => void }
/** A menu of rows in the editor's context menu (arrows walk them, Esc closes): the checked row focused, else the first; a row closes it, then runs. */
export function openMarkdownRowMenu(options: { editor: Editor; anchor: HTMLElement; positionAnchor?: MarkdownPopoverAnchor; title: string; rows: readonly MarkdownRowMenuRow[]; readonly?: boolean; className?: string }): () => void {
  const { rows, ...popover } = options
  return openMarkdownContextPopover({ ...popover, variant: 'menu',
    render(container, close) {
      const doc = container.ownerDocument
      const buttons = rows.map(row => {
        const button = doc.createElement('button')
        button.type = 'button'; button.className = 'md-context-menu-item'; button.setAttribute('role', row.checked === undefined ? 'menuitem' : 'menuitemradio')
        if (row.checked !== undefined) button.setAttribute('aria-checked', String(row.checked))
        if (row.icon) button.append(markdownContextIcon(doc, row.icon))
        button.append(row.label)
        if (row.detail) { const detail = doc.createElement('span'); detail.className = 'md-context-menu-detail'; detail.textContent = row.detail; button.append(detail) }
        button.addEventListener('click', () => { close(); row.run() })
        return button
      })
      container.append(...buttons)
      queueMicrotask(() => (buttons.find((_, index) => rows[index]!.checked) ?? buttons[0])?.focus({ preventScroll: true }))
      return () => {}
    } })
}
