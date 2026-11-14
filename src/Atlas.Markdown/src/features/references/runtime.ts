import { posToDOMRect, type Editor } from '@tiptap/core'
import { DOMSerializer, Fragment, Slice, type Node as PMNode } from '@tiptap/pm/model'
import { NodeSelection, type Selection, type Transaction } from '@tiptap/pm/state'
import { ReplaceStep } from '@tiptap/pm/transform'
import type { MarkdownReferenceProvider, MarkdownReferenceValue } from '../../core/plugins/types'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { markdownStrings as words } from '../../ui/strings'
import { referencesStrings as strings } from '../../ui/strings/references'
import { createMarkdownContextToolbar, openMarkdownContextPopover, type MarkdownContextToolbar } from '../modules/contextToolbar'
import { markdownModuleUrl } from '../modules/forms'
import { matchesReferenceUrl as matches, referenceValue } from './paste'
import { modulesStrings } from '../../ui/strings/modules'
import { chipToTextShortcut, convertMarkdownChipToText, markdownChipText } from '../chips/chipText'

interface Target {
  from: number
  to: number
  snapshot: Slice
  selection: Selection
  abort: AbortController
  close: () => void
  failed?: () => void
  /** Presses there keep the panel open (the chip it edits: its own click closes it). */
  roots?: readonly HTMLElement[]
}

function navigationUrl(value: string): boolean {
  try { return !/[\u0000-\u001f\u007f]/.test(value) && markdownModuleUrl(value) === value } catch { return false }
}

export class ReferenceRuntime {
  private target?: Target
  private toolbar?: MarkdownContextToolbar
  private toolbarAnchor?: HTMLElement
  private toolbarPosition?: number
  private toolbarNode?: PMNode
  private observer?: MutationObserver
  private destroyed = false
  private epoch = 0
  private lastSelection?: Selection
  private lastEditable = true
  private toolbarOpen?: HTMLButtonElement
  /** The open edit of a selected chip (`edit`), so the same gesture closes it. */
  private editing?: Target

  constructor(private editor: Editor, private providers: readonly MarkdownReferenceProvider[]) {
    const doc = editor.view.dom.ownerDocument
    this.observer = doc.defaultView?.MutationObserver ? new doc.defaultView.MutationObserver(() => this.update()) : undefined
    this.observer?.observe(doc.body, { childList: true, subtree: true })
    this.observer?.observe(editor.view.dom, { attributes: true, attributeFilter: ['contenteditable', 'href', 'aria-disabled'], subtree: true })
  }

  map(transaction: Transaction): void {
    const target = this.target
    if (!target) return
    let start = target.from, end = target.to
    for (const map of transaction.mapping.maps) {
      let touched = false
      map.forEach((from, to) => { if (from < end && to > start || from === to && from > start && from < end) touched = true })
      if (touched) { this.cancel(); return }
      start = map.map(start, 1); end = map.map(end, -1)
    }
    target.selection = target.selection.map(transaction.doc, transaction.mapping)
    const from = transaction.mapping.mapResult(target.from, 1), to = transaction.mapping.mapResult(target.to, -1)
    target.from = from.pos; target.to = to.pos
    if (from.deletedAcross || to.deletedAcross || !this.valid(target, transaction.doc, transaction.selection)) this.cancel()
  }

  private valid(target: Target, doc = this.editor.state.doc, selection = this.editor.state.selection): boolean {
    return !this.destroyed && !this.editor.isDestroyed && this.editor.isEditable && this.editor.view.dom.isConnected &&
      !target.abort.signal.aborted && target.from < target.to && target.to <= doc.content.size &&
      doc.slice(target.from, target.to).eq(target.snapshot) && selection.eq(target.selection)
  }

  private cancel(): void {
    const target = this.target
    this.target = undefined
    if (target) { target.abort.abort(); target.close() }
  }

  private track(from: number, to: number): Target {
    this.cancel()
    const target: Target = { from, to, snapshot: this.editor.state.doc.slice(from, to), selection: this.editor.state.selection, abort: new AbortController(), close: () => {} }
    this.target = target
    return target
  }

  private anchor(target: Target): HTMLElement {
    const anchor = this.editor.view.dom.ownerDocument.createElement('span')
    anchor.dataset.mdNoSelect = ''; anchor.style.position = 'fixed'; anchor.style.pointerEvents = 'none'
    anchor.getBoundingClientRect = () => posToDOMRect(this.editor.view, target.from, target.to)
    this.editor.view.dom.parentElement!.append(anchor)
    target.abort.signal.addEventListener('abort', () => anchor.remove(), { once: true })
    return anchor
  }

  private popover(target: Target, title: string, render: (body: HTMLElement, close: () => void) => void, menu = false): void {
    const anchor = this.anchor(target)
    target.close = openMarkdownContextPopover({
      editor: this.editor, anchor, title, variant: menu ? 'menu' : 'panel', trackSelection: false, signal: target.abort.signal, interactionRoots: target.roots,
      positionAnchor: { contextElement: this.editor.view.dom, getBoundingClientRect: () => posToDOMRect(this.editor.view, target.from, target.to) },
      className: menu ? undefined : 'md-module-form-popover',
      render: (body, close) => {
        render(body, close)
        return () => { target.abort.abort(); anchor.remove(); if (this.target === target) this.target = undefined }
      },
    })
  }

  private button(body: HTMLElement, label: string, action: () => void, menu = false): HTMLButtonElement {
    const button = body.ownerDocument.createElement('button')
    button.type = 'button'; button.className = menu ? 'md-menu-item' : 'md-btn'
    button.textContent = label
    if (menu) button.setAttribute('role', 'menuitem')
    button.addEventListener('click', action); body.append(button)
    return button
  }

  offerPaste(from: number, to: number, url: string, choices: readonly MarkdownReferenceProvider[]): void {
    const target = this.track(from, to)
    this.popover(target, strings.pasted, (body, close) => {
      for (const provider of choices) this.button(body, strings.convert(provider.label), () => {
        if (!this.valid(target)) { close(); return }
        const next = { from: target.from, to: target.to }; close()
        this.resolve(this.track(next.from, next.to), provider, url)
      }, true)
      this.button(body, strings.keep, close, true)
    }, true)
  }

  private value(provider: MarkdownReferenceProvider, node: PMNode): MarkdownReferenceValue | null {
    return referenceValue(provider, node)
  }

  private resolved(provider: MarkdownReferenceProvider, value: unknown): PMNode {
    const node = this.editor.schema.nodeFromJSON(JSON.parse(JSON.stringify(value)))
    if (node.type.name !== provider.nodeType || !node.isInline || !node.isAtom || node.isText || !this.value(provider, node)) throw new Error('Invalid reference')
    node.check()
    return node
  }

  private apply(target: Target, node: PMNode): void {
    if (!this.valid(target)) { this.cancel(); return }
    const transaction = this.editor.state.tr
    if (transaction.maybeStep(new ReplaceStep(target.from, target.to, new Slice(Fragment.from(node), 0, 0))).failed) {
      if (target.failed) target.failed()
      else {
        const next = { from: target.from, to: target.to }; this.cancel()
        this.popover(this.track(next.from, next.to), strings.proposal, body => {
          const status = body.ownerDocument.createElement('p'); status.setAttribute('role', 'alert'); status.textContent = strings.failed; body.append(status)
        })
      }
      return
    }
    this.cancel()
    dispatchMarkdownHistoryOperation(this.editor, transaction)
    this.editor.view.focus()
  }

  private link(url: string, label: string, marks: readonly import('@tiptap/pm/model').Mark[] = []): PMNode {
    return this.editor.schema.text(label, [...marks.filter(mark => mark.type.name !== 'link'), this.editor.schema.marks.link!.create({ href: url })])
  }

  /** The address a resolver reads: a saved relative one (`#/pages/…`) against the page, as the field shows it as saved (never the dev origin). */
  private absolute(url: string): string {
    try { return new URL(url, this.editor.view.dom.ownerDocument.baseURI).href } catch { return url /* A host without an HTTP base: an absolute address only. */ }
  }

  private resolve(target: Target, provider: MarkdownReferenceProvider, initial: string, editing = false, fallback?: MarkdownReferenceValue): void {
    this.popover(target, editing ? strings.address : strings.proposal, (body, close) => {
      let request: AbortController | undefined, revision = 0, result: PMNode | undefined, applyLoaded = false
      let url = initial
      const invalidate = () => { revision++; request?.abort(); request = undefined; result = undefined; apply.disabled = true; applyLoaded = false }
      const status = body.ownerDocument.createElement('p'); status.setAttribute('role', 'status')
      const preview = body.ownerDocument.createElement('div')
      target.failed = () => { status.setAttribute('role', 'alert'); status.textContent = strings.failed; apply.disabled = true }
      let input: HTMLInputElement | undefined
      if (editing) {
        const label = body.ownerDocument.createElement('label'); label.className = 'md-context-field md-context-field-wide'; label.textContent = strings.addressLabel
        // Text with the URL keyboard: a saved relative address is valid here.
        input = body.ownerDocument.createElement('input'); input.type = 'text'; input.inputMode = 'url'; input.value = initial
        label.append(input); body.append(label)
        input.addEventListener('input', () => { url = input!.value; invalidate(); status.textContent = ''; preview.replaceChildren(); if (fallback) apply.disabled = !navigationUrl(url) })
        // Enter takes the address (P4): a loaded or plain one at once, otherwise it loads and applies once the target is known.
        input.addEventListener('keydown', event => {
          if (event.key !== 'Enter' || event.isComposing) return
          event.preventDefault()
          if (!apply.disabled) apply.click()
          else if (!fallback) { if (!request) load(); applyLoaded = !!request }
        })
      }
      const load = () => {
        invalidate(); preview.replaceChildren()
        const address = this.absolute(url)
        if (!this.valid(target) || !matches(provider, address)) { status.textContent = strings.invalid; return }
        request = new AbortController()
        const current = revision, signal = request.signal
        status.setAttribute('role', 'status')
        status.textContent = words.loading
        void Promise.resolve().then(() => signal.aborted ? null : provider.resolveUrl!(address, { signal })).then(value => {
          if (signal.aborted || current !== revision || !this.valid(target)) return
          result = this.resolved(provider, value).mark(target.snapshot.content.firstChild?.marks.filter(mark => mark.type.name !== 'link') ?? [])
          preview.replaceChildren(DOMSerializer.fromSchema(this.editor.schema).serializeNode(result, { document: body.ownerDocument }))
          status.textContent = ''; apply.disabled = false
          if (applyLoaded) apply.click()
        }).catch(() => {
          if (signal.aborted || current !== revision || !this.valid(target)) return
          status.setAttribute('role', 'alert'); status.textContent = strings.failed; retry.hidden = false
        })
      }
      if (editing && !fallback) this.button(body, strings.load, load)
      body.append(status, preview)
      const retry = this.button(body, words.retry, () => { retry.hidden = true; load() }); retry.hidden = true
      const actions = body.ownerDocument.createElement('div'); actions.className = 'md-module-actions'; body.append(actions)
      const apply = this.button(actions, words.apply, () => {
        if (fallback && navigationUrl(url)) this.apply(target, this.link(url, fallback.label, target.snapshot.content.firstChild?.marks))
        else if (result) this.apply(target, result)
      }); apply.disabled = !fallback
      apply.className = 'md-btn-primary'
      const cancel = this.button(actions, words.cancel, close)
      actions.insertBefore(cancel, apply)
      target.abort.signal.addEventListener('abort', invalidate, { once: true })
      if (!editing) load()
    })
  }

  /**
   * The selected chip's edit where it is shown (P1, P9): Enter or F2 or the toolbar's „Upravit“ open its address at the chip, the same
   * key closes it again. A click follows the chip as any link (contract J3.5: a link chip opens its target, the host's link rule).
   */
  edit(): void {
    const selection = this.editor.state.selection
    if (this.destroyed || !this.editor.isEditable || !(selection instanceof NodeSelection)) return
    if (this.target && this.target === this.editing) { this.cancel(); return }
    const provider = this.providers.find(item => item.nodeType === selection.node.type.name)
    const value = provider && this.value(provider, selection.node)
    if (!provider || !value) return
    const target = this.editing = this.track(selection.from, selection.to)
    const chip = this.editor.view.nodeDOM(selection.from)
    if (chip?.nodeType === 1) target.roots = [chip as HTMLElement]
    this.resolve(target, provider, value.url, true, provider.resolveUrl && provider.matchUrl ? undefined : value)
  }

  update(): void {
    if (this.destroyed) return
    const current = this.editor.state.selection
    if (!this.lastSelection?.eq(current) || this.lastEditable !== this.editor.isEditable) this.epoch++
    this.lastSelection = current; this.lastEditable = this.editor.isEditable
    if (this.target && !this.valid(this.target)) this.cancel()
    const selection = this.editor.state.selection
    if (!this.editor.isEditable || !this.editor.view.dom.isConnected || !(selection instanceof NodeSelection)) { this.clearToolbar(); return }
    const provider = this.providers.find(item => item.nodeType === selection.node.type.name)
    const value = provider && this.value(provider, selection.node)
    const dom = this.editor.view.nodeDOM(selection.from)
    const anchor = dom?.nodeType === 1 ? dom as HTMLElement : undefined
    if (!provider || !value || !anchor) { this.clearToolbar(); return }
    if (this.toolbarAnchor === anchor && this.toolbarPosition === selection.from && this.toolbarNode?.eq(selection.node)) {
      if (this.toolbarOpen) this.toolbarOpen.disabled = this.navigationDenied(anchor)
      return
    }
    this.clearToolbar()
    this.toolbarAnchor = anchor; this.toolbarPosition = selection.from; this.toolbarNode = selection.node
    const toolbar = this.toolbar = createMarkdownContextToolbar({ editor: this.editor, anchor, getPos: () => this.editor.state.selection.from, label: strings.toolbar, selectOnClick: false, onActivate: () => this.edit() })
    const target = () => this.track(this.editor.state.selection.from, this.editor.state.selection.to)
    this.toolbarOpen = toolbar.addButton({ label: strings.open, icon: 'link', disabled: this.navigationDenied(anchor), onClick: () => {
      const link = this.renderedLink(anchor)
      if (link) {
        if (!this.navigationDenied(anchor)) link.click()
      } else this.editor.view.dom.ownerDocument.defaultView?.open(value.url, '_blank', 'noopener,noreferrer')
    } })
    toolbar.addButton({ label: strings.copy, icon: 'copy', onClick: () => {
      const clipboard = this.editor.view.dom.ownerDocument.defaultView?.navigator.clipboard
      const epoch = this.epoch
      void Promise.resolve().then(() => { if (!clipboard) throw new Error('Clipboard unavailable'); return clipboard.writeText(value.url) }).catch(() => {
        if (epoch !== this.epoch || this.destroyed || this.editor.isDestroyed || !this.editor.isEditable || !this.editor.view.dom.isConnected ||
          !selection.eq(this.editor.state.selection) || !this.editor.state.doc.nodeAt(selection.from)?.eq(selection.node)) return
        const current = target(); this.popover(current, strings.toolbar, body => { const status = body.ownerDocument.createElement('p'); status.setAttribute('role', 'alert'); status.textContent = strings.copyFailed; body.append(status) })
      })
    } })
    toolbar.addSeparator()
    const resolvable = Boolean(provider.resolveUrl && provider.matchUrl)
    toolbar.addButton({ label: resolvable ? strings.edit : strings.editAsLink, icon: 'edit', onClick: () => this.edit() })
    if (this.editor.schema.marks.link) toolbar.addButton({ label: strings.asLink, icon: 'link', onClick: () => this.apply(target(), this.link(value.url, value.label, selection.node.marks)) })
    // As every chip: back to its visible text (the page title, the issue key), no link left.
    if (markdownChipText(selection.node) !== undefined) toolbar.addButton({ label: modulesStrings.chipToTextLabel(chipToTextShortcut()), icon: 'pilcrow', onClick: () => convertMarkdownChipToText(this.editor, this.editor.state.selection.from) })
    toolbar.refresh()
  }

  private renderedLink(anchor: HTMLElement): HTMLAnchorElement | null {
    return anchor.matches('a') ? anchor as HTMLAnchorElement : anchor.querySelector('a')
  }
  private navigationDenied(anchor: HTMLElement): boolean {
    const link = this.renderedLink(anchor)
    return Boolean(link && (link.getAttribute('aria-disabled') === 'true' || !link.getAttribute('href') || !navigationUrl(link.getAttribute('href')!)))
  }

  private clearToolbar(): void { this.toolbar?.destroy(); this.toolbar = undefined; this.toolbarAnchor = undefined; this.toolbarPosition = undefined; this.toolbarNode = undefined; this.toolbarOpen = undefined }
  destroy(): void { this.destroyed = true; this.cancel(); this.clearToolbar(); this.observer?.disconnect() }
}
