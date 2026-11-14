import { UniqueID } from '@tiptap/extension-unique-id'
import type { JSONContent } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { createMarkdownContextToolbar, defineMarkdownAttributeValidator, markdownContextIcon, markdownScrollBehavior, isRichFormat, openMarkdownContextPopover, RICH_FORMAT, type MarkdownPlugin, type MarkdownRichInput, type MarkdownRichValue, type MarkdownContextToolbar } from '../../core'
import { headingAnchorsStrings } from './strings'

export interface HeadingAnchorsOptions {
  /** Canonical page URL supplied by the host; enables the selected-heading link action. */
  baseUrl?: string | (() => string)
  /** Host router override, e.g. a wiki that already uses the URL fragment for its page route. */
  getHeadingUrl?: (headingId: string) => string
  /** Required for local ID generation when a collaboration extension is present. */
  collaborationReady?: () => boolean
}
// Schema 2: the dot joined the charset (portal.vpn) and `aliases` keeps a heading's former ids, comma separated.
const ID = '[A-Za-z0-9][A-Za-z0-9._-]{0,127}'
const ID_PATTERN = `^${ID}$`
const ALIASES_PATTERN = `^(?:${ID}(?:,${ID})*)?$`
const idPattern = new RegExp(ID_PATTERN), aliasesPattern = new RegExp(ALIASES_PATTERN)
const validId = (value: unknown): value is string => typeof value === 'string' && idPattern.test(value)
const validAliases = (value: unknown) => typeof value === 'string' && value.length <= 1000 && aliasesPattern.test(value)
let nextView = 0
const generateId = () => globalThis.crypto.randomUUID()
const restoreHeadingFocus = new WeakMap<HTMLElement, () => void>()

/** Run once on the host's authoritative document, before publishing a collaboration migration. */
export function migrateHeadingAnchors(value: MarkdownRichInput, options: { copy?: boolean } = {}): MarkdownRichValue {
  if (!isRichFormat(value.format) || value.version !== 1) throw new TypeError('Heading anchors require a rich document.')
  const declared = value.plugins?.find(plugin => plugin.id === 'heading-anchors')
  if (declared && declared.version !== 1 && declared.version !== 2) throw new TypeError('Unsupported heading-anchors schema version.')
  const document = structuredClone(value.document) as JSONContent
  const seen = new Set<string>()
  const walk = (node: JSONContent) => {
    if (node.type === 'heading') {
      let id: unknown = node.attrs?.headingId
      if (options.copy || !validId(id) || seen.has(id)) {
        do { id = generateId() } while (seen.has(id as string))
      }
      seen.add(id as string); node.attrs = { ...node.attrs, headingId: id }
      // A copy is a new identity: its former ids belong to the original.
      if (options.copy && node.attrs.aliases) node.attrs.aliases = ''
    }
    node.content?.forEach(walk)
  }
  walk(document)
  return { ...value, format: RICH_FORMAT, document: document as MarkdownRichValue['document'], plugins: [...(value.plugins ?? []).filter(plugin => plugin.id !== 'heading-anchors'), { id: 'heading-anchors', version: 2 }] }
}
export function headingAnchorUrl(baseUrl: string, headingId: string): string {
  if (!validId(headingId)) throw new TypeError('Invalid heading identity.')
  const url = new URL(baseUrl)
  if (!['https:', 'http:'].includes(url.protocol)) throw new TypeError('Heading links require an HTTP(S) page URL.')
  url.hash = `heading=${headingId}`
  return url.href
}
/** Resolve a share-link identity inside a specific mounted editor/renderer. */
export function navigateToHeading(root: HTMLElement, headingId: string): boolean {
  if (!validId(headingId)) return false
  const heading = [...root.querySelectorAll<HTMLElement>('h1[data-heading-id],h2[data-heading-id],h3[data-heading-id],h4[data-heading-id],h5[data-heading-id],h6[data-heading-id]')]
    .find(element => element.dataset.headingId === headingId && !element.closest('[data-md-module]'))
  if (!heading) return false
  heading.scrollIntoView?.({ block: 'start', behavior: markdownScrollBehavior(root) })
  // Chromium blurs a non-interactive heading as soon as tabindex is removed.
  // Keep its temporary focusability until focus leaves, then restore the DOM.
  restoreHeadingFocus.get(heading)?.()
  const previous = heading.getAttribute('tabindex')
  const restore = () => {
    heading.removeEventListener('blur', restore)
    restoreHeadingFocus.delete(heading)
    if (previous === null) heading.removeAttribute('tabindex'); else heading.setAttribute('tabindex', previous)
  }
  restoreHeadingFocus.set(heading, restore)
  heading.addEventListener('blur', restore, { once: true })
  heading.tabIndex = -1; heading.focus({ preventScroll: true })
  if (heading.ownerDocument.activeElement !== heading) restore()
  return true
}

export function createHeadingAnchorsPlugin(options: HeadingAnchorsOptions = {}): MarkdownPlugin {
  // A version 1 document (no dot, no aliases) is valid version 2 content; the raised version keeps an older tab from rejecting a dotted id.
  return { id: 'heading-anchors', apiVersion: 1, schemaVersion: 2, migrations: [{ from: 1, to: 2, migrate: document => ({ ...document }) }], profiles: ['rich'], create() {
    const scope = `md-heading-view-${++nextView}`
    /** The heading's link as the host builds it (`getHeadingUrl` or `baseUrl`); undefined without one or for a rejected address. */
    const headingUrl = (headingId: unknown): string | undefined => {
      if (!validId(headingId) || (!options.baseUrl && !options.getHeadingUrl)) return undefined
      try {
        if (!options.getHeadingUrl) return headingAnchorUrl(typeof options.baseUrl === 'function' ? options.baseUrl() : options.baseUrl!, headingId)
        const custom = new URL(options.getHeadingUrl(headingId))
        return ['https:', 'http:'].includes(custom.protocol) && !custom.username && !custom.password ? custom.href : undefined
      } catch { return undefined }
    }
    const extension = UniqueID.extend({
      // The reader's selection bubble (FormattingToolbar) copies a heading's link through the same builder.
      addStorage() { return { ...(this as typeof this & { parent?: () => object }).parent?.(), headingUrl } },
      // Migration is explicit, never scheduled on every client's first sync.
      onCreate() {},
      addGlobalAttributes() {
        return [{ types: ['heading'], attributes: { headingId: {
          default: null,
          validate: defineMarkdownAttributeValidator({ kind: 'text', nullable: true, pattern: ID_PATTERN }, value => {
            if (value !== null && !validId(value)) throw new TypeError('Invalid heading identity.')
          }),
          parseHTML: (element: HTMLElement) => { const id = element.getAttribute('data-heading-id'); return validId(id) ? id : null },
          renderHTML: (attrs: Record<string, unknown>) => validId(attrs.headingId) ? { 'data-heading-id': attrs.headingId, id: `${scope}-${attrs.headingId}` } : {},
        }, aliases: {
          default: '',
          validate: defineMarkdownAttributeValidator({ kind: 'text', maxLength: 1000, singleLine: true, pattern: ALIASES_PATTERN }, value => {
            if (!validAliases(value)) throw new TypeError('Invalid heading aliases.')
          }),
          parseHTML: (element: HTMLElement) => { const aliases = element.getAttribute('data-heading-aliases'); return validAliases(aliases) ? aliases : '' },
          renderHTML: (attrs: Record<string, unknown>) => attrs.aliases ? { 'data-heading-aliases': attrs.aliases } : {},
        } } }]
      },
      addProseMirrorPlugins() {
        const upstream = (this as typeof this & { parent?: () => Plugin[] }).parent?.() ?? []
        this.storage.needsInitialIdGeneration = false
        const editor = this.editor
        const collaborative = editor.extensionManager.extensions.some(item => item.name === 'collaboration')
        return upstream.map(plugin => {
          const append = plugin.spec.appendTransaction
          if (!append) return plugin
          const drop = plugin.props.handleDOMEvents?.drop
          let localDrag = false
          return new Plugin({ ...plugin.spec,
            // Upstream attaches this provenance listener to global window. Bind it
            // to the actual editor document so iframe moves retain their identity.
            view(view) {
              const win = view.dom.ownerDocument.defaultView
              const track = (event: Event) => {
                const target = event.target
                localDrag = !!target && 'nodeType' in target && !!view.dom.parentElement?.contains(target as Node)
              }
              win?.addEventListener('dragstart', track)
              return { destroy() { localDrag = false; win?.removeEventListener('dragstart', track) } }
            },
            props: { ...plugin.props, handleDOMEvents: { ...plugin.props.handleDOMEvents,
              drop(view, event) {
                const preserve = localDrag && !['copy', 'copyMove'].includes(event.dataTransfer?.effectAllowed ?? '')
                localDrag = false
                return preserve ? false : drop?.call(this, view, event) ?? false
              },
            } },
            appendTransaction(transactions, before, after) {
            if (!editor.isEditable || (collaborative && !options.collaborationReady?.()) || transactions.some(tr => tr.getMeta('y-sync$'))) return
            return append.call(this, transactions, before, after)
          } })
        })
      },
    }).configure({ attributeName: 'headingId', types: ['heading'], generateID: generateId })
    return {
      extensions: [extension],
      // Heading ids are identity, not content: callers that carry them separately ask to ignore them.
      markdownLossy(document, options) {
        if (options?.ignoreIdentity) return false
        let found = false; document.descendants(node => { found ||= node.type.name === 'heading' && !!node.attrs.headingId }); return found
      },
      onAttach({ editor, signal }) {
        if (!options.baseUrl && !options.getHeadingUrl) return
        let toolbar: MarkdownContextToolbar | undefined
        let position: number | undefined
        let anchor: HTMLElement | undefined
        let disposed = false
        let closePanel: (() => void) | undefined
        let panelOpen = false
        let copyAttempt = 0
        let feedbackButton: HTMLButtonElement | undefined
        let feedbackTimer: ReturnType<typeof setTimeout> | undefined
        const clearCopyFeedback = () => {
          if (feedbackTimer !== undefined) clearTimeout(feedbackTimer)
          feedbackTimer = undefined
          if (feedbackButton?.isConnected) {
            delete feedbackButton.dataset.feedback
            feedbackButton.title = headingAnchorsStrings.copyLink
            feedbackButton.setAttribute('aria-label', headingAnchorsStrings.copyLink)
            feedbackButton.replaceChildren(markdownContextIcon(feedbackButton.ownerDocument, 'link'))
          }
          feedbackButton = undefined
        }
        const showCopied = (button: HTMLButtonElement) => {
          if (disposed || !button.isConnected || !editor.isEditable) return
          clearCopyFeedback(); feedbackButton = button
          button.dataset.feedback = 'success'
          button.title = headingAnchorsStrings.linkCopied
          button.setAttribute('aria-label', headingAnchorsStrings.linkCopied)
          button.replaceChildren(markdownContextIcon(button.ownerDocument, 'check'))
          feedbackTimer = setTimeout(clearCopyFeedback, 1600)
        }
        const close = () => { copyAttempt++; clearCopyFeedback(); closePanel?.(); closePanel = undefined; panelOpen = false; toolbar?.destroy(); toolbar = undefined; position = undefined; anchor = undefined }
        const refresh = () => {
          if (signal.aborted || editor.isDestroyed) { close(); return }
          const selection = editor.state.selection
          const depth = selection.$from.depth
          const node = selection.$from.parent
          const next = depth && node.type.name === 'heading' && validId(node.attrs.headingId) ? selection.$from.before(depth) : undefined
          const element = next === undefined ? undefined : editor.view.nodeDOM(next) as HTMLElement | undefined
          if (!editor.isEditable || next === undefined || !element) { close(); return }
          // The same heading moved by an edit above it (a colleague typing): the toolbar stays, only its position follows.
          if (anchor === element) { position = next; toolbar?.refresh(); return }
          close(); position = next; anchor = element
          // Beside the heading's text end: above the heading, the button covered the start of the block before it.
          const textEnd = { contextElement: element, getBoundingClientRect: () => {
            const heading = position === undefined ? null : editor.state.doc.nodeAt(position)
            // A host's chips after the title (a requirement's state) are part of the line: the button stands after them, never over them.
            // Chips that wrap: the end of their last line (their bounding box would reach the first line's end, where a host's state stands).
            const lines = element.querySelector<HTMLElement>(':scope > .rq-chips')?.getClientRects(), chips = lines?.[lines.length - 1]
            if (chips?.width) return new DOMRect(chips.right, chips.top, 0, chips.height)
            try { // no node or no layout: the heading's own box
              const end = editor.view.coordsAtPos(position! + heading!.nodeSize - 1)
              return new DOMRect(end.right, end.top, 0, end.bottom - end.top)
            } catch { return element.getBoundingClientRect() }
          } }
          toolbar = createMarkdownContextToolbar({ editor, anchor: element, positionAnchor: textEnd, placement: 'right', getPos: () => position, label: headingAnchorsStrings.linkToHeading, signal, selectOnClick: false, markAnchor: false, followFrames: false,
            isVisible: () => editor.state.selection.empty && editor.state.selection.$from.parent.type.name === 'heading'
              && (editor.view.hasFocus() || panelOpen || !!toolbar?.element.contains(editor.view.dom.ownerDocument.activeElement)),
          })
          toolbar.addButton({ label: headingAnchorsStrings.copyLink, icon: 'link', onClick(button) {
            clearCopyFeedback()
            const attempt = ++copyAttempt
            const current = position === undefined ? undefined : editor.state.doc.nodeAt(position)
            const url = headingUrl(current?.attrs.headingId)
            if (!url) return
            const fallback = () => {
              if (attempt !== copyAttempt || disposed || !button.isConnected || !editor.isEditable) return
              closePanel?.(); panelOpen = true
              closePanel = openMarkdownContextPopover({ editor, anchor: button, title: headingAnchorsStrings.linkToHeading, signal, render(container) {
              const label = container.ownerDocument.createElement('label'); label.textContent = headingAnchorsStrings.linkToHeading
              const input = container.ownerDocument.createElement('input'); input.readOnly = true; input.value = url; label.append(input); container.append(label); input.select()
              return () => { panelOpen = false }
              } })
            }
            const clipboard = editor.view.dom.ownerDocument.defaultView?.navigator.clipboard
            if (!clipboard) { fallback(); return }
            try {
              void clipboard.writeText(url).then(() => { if (attempt === copyAttempt) showCopied(button) }, fallback)
            } catch { fallback() }
          } })
        }
        editor.on('selectionUpdate', refresh); editor.on('transaction', refresh); refresh()
        return () => { disposed = true; editor.off('selectionUpdate', refresh); editor.off('transaction', refresh); close() }
      },
    }
  } }
}
