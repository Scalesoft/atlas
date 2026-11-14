import type { Editor } from '@tiptap/core'
import { createIconElement, createSuggestionList, createSuggestionPopup, foldSearchText, isMarkdownComposition, markdownModuleUrl, type MarkdownIconName, type SuggestionPopup } from '../core'
import { copyAssistTheme } from './marker'
import { assistMotion, finished, menuIn, menuOut } from './motion'
import type { AssistStrings, MarkdownAssistAvatar } from './types'

export interface AssistListRow {
  readonly title: string
  readonly description?: string
  readonly icon: MarkdownIconName
  readonly ai?: boolean
  readonly reason?: string
  readonly avatar?: MarkdownAssistAvatar
}

/** At most three characters of the host's initials, as text. */
const initialsOf = (avatar?: MarkdownAssistAvatar) => typeof avatar?.initials === 'string' ? Array.from(avatar.initials.trim()).slice(0, 3).join('') : ''
/** An http(s) or relative image URL without credentials (the shared list's rule for avatars), else nothing. */
function avatarSource(avatar?: MarkdownAssistAvatar): string {
  try { return markdownModuleUrl(avatar?.src, true) } catch { return '' }
}

export interface AssistListOptions {
  readonly editor: Editor
  readonly strings: AssistStrings
  readonly rows: readonly AssistListRow[]
  /** Where the list grows from (the marker, else the block). */
  readonly anchor: () => DOMRect
  readonly reducedMotion?: () => boolean
  readonly onSelect: (index: number) => void
  readonly onActive: (index: number) => void
  readonly onDismissRow: (index: number) => void
  readonly onSuppressRow: (index: number) => void
  /** The popup was dismissed from outside (pointer outside, a replacing panel). */
  readonly onDismissed: () => void
}

/**
 * The block's suggestions in the shared suggestion list and popup (same rows, navigation and exclusive
 * panel registry as slash, `@` and `[[`), plus the prototype's footer. Typing focus stays in the editor;
 * the runtime forwards the editor's keys to `keydown`.
 */
export class AssistList {
  readonly element: HTMLElement
  private readonly list: ReturnType<typeof createSuggestionList>
  private readonly popup: SuggestionPopup
  private readonly live: HTMLElement
  private readonly reason: HTMLElement
  private readonly why: HTMLButtonElement
  private rows: readonly AssistListRow[] = []
  /** Typing filters the rows, as in the block's menu (J4.9): the indexes of `rows` shown, in order. */
  private filter = ''
  private shown: number[] = []
  private active = -1
  private closed = false
  private readonly root: HTMLElement
  private readonly previous: Map<string, string | null>

  constructor(private readonly options: AssistListOptions) {
    const { editor, strings } = options
    const root = this.root = editor.view.dom
    const doc = root.ownerDocument
    const element = this.element = doc.createElement('div')
    element.className = 'md-assist-menu'
    copyAssistTheme(root, element)
    // Not announced on appearance (passive suggestions stay silent); only when the author opens the list.
    this.live = doc.createElement('div'); this.live.className = 'md-assist-live'; this.live.setAttribute('role', 'status')
    this.list = createSuggestionList({ document: doc, label: strings.listLabel, testId: 'assist-suggestions', emptyText: strings.empty,
      // Rows carry their index as id; the active option reports its element id (`…-opt-<index>`).
      onSelect: id => { const index = Number(id); if (Number.isInteger(index) && index >= 0) options.onSelect(index) },
      onActiveChange: id => {
        this.active = this.indexOf(id)
        if (id) root.setAttribute('aria-activedescendant', id); else root.removeAttribute('aria-activedescendant')
        this.showReason(false)
        options.onActive(this.active)
      },
    })
    this.reason = doc.createElement('p'); this.reason.className = 'md-assist-reason'; this.reason.hidden = true; this.reason.setAttribute('aria-live', 'polite')
    const footer = doc.createElement('div'); footer.className = 'md-assist-menu-footer'
    this.why = this.footerButton(footer, strings.why, () => this.showReason(this.why.getAttribute('aria-expanded') !== 'true'))
    this.why.setAttribute('aria-expanded', 'false')
    this.footerButton(footer, strings.suppressKind, () => { if (this.active >= 0) options.onSuppressRow(this.active) })
    element.append(this.live, this.list.element, this.reason, footer)
    const attributes = ['aria-controls', 'aria-expanded', 'aria-autocomplete', 'aria-activedescendant']
    this.previous = new Map(attributes.map(name => [name, root.getAttribute(name)]))
    root.setAttribute('aria-controls', this.list.element.id); root.setAttribute('aria-expanded', 'true'); root.setAttribute('aria-autocomplete', 'list')
    this.popup = createSuggestionPopup({ editor, contextElement: root, content: element, placement: 'bottom-start', getReferenceRect: options.anchor,
      onDismiss: () => { if (!this.closed) { this.close(false); options.onDismissed() } } })
    this.update(options.rows)
    root.addEventListener('beforeinput', this.onText as EventListener, true)
    menuIn(element, assistMotion(root, options.reducedMotion))
  }

  private footerButton(footer: HTMLElement, label: string, run: () => void): HTMLButtonElement {
    const button = footer.ownerDocument.createElement('button')
    button.type = 'button'; button.tabIndex = -1; button.textContent = label
    button.addEventListener('mousedown', event => event.preventDefault())
    button.addEventListener('click', run)
    footer.append(button)
    return button
  }
  private indexOf(id: string): number {
    const match = /-opt-(\d+)$/.exec(id)
    return match ? this.shown[Number(match[1])] ?? -1 : -1
  }
  private showReason(open: boolean): void {
    const text = this.rows[this.active]?.reason
    this.reason.textContent = open && text ? text : ''
    this.reason.hidden = !(open && text)
    this.why.setAttribute('aria-expanded', String(open && !!text))
  }

  update(rows: readonly AssistListRow[]): void {
    if (this.closed) return
    this.rows = rows
    const words = foldSearchText(this.filter).split(/\s+/u).filter(Boolean)
    this.shown = rows.flatMap((row, index) => words.every(word => foldSearchText(`${row.title} ${row.description ?? ''}`).includes(word)) ? [index] : [])
    rows = this.shown.map(index => this.rows[index]!)
    const avatars = rows.map(row => ({ initials: initialsOf(row.avatar), src: avatarSource(row.avatar) }))
    // The shared rows draw an avatar image, else a text icon (the initials), else the registry icon; ids stay the rows' own indexes.
    this.list.update(rows.map((row, index) => ({ id: String(this.shown[index]), title: row.title, description: row.description,
      icon: avatars[index]!.initials || `svg:${row.icon}`, avatar: avatars[index]!.src })))
    rows.forEach((row, index) => {
      const { initials, src } = avatars[index]!
      const icon = (initials || src) && this.list.element.querySelector<HTMLElement>(`#${this.list.element.id}-opt-${index} .ic`)
      if (!icon) return
      icon.classList.add('md-assist-avatar')
      // A broken image never leaves an empty badge: the initials, else the component's icon.
      icon.querySelector('img')?.addEventListener('error', () => {
        icon.classList.toggle('md-assist-avatar', !!initials)
        icon.replaceChildren(initials || createIconElement(icon.ownerDocument, row.icon, 18))
      }, { once: true })
    })
    // The AI tag belongs to model output only; the source decides by `ai: true`.
    rows.forEach((row, index) => {
      if (!row.ai) return
      const title = this.list.element.querySelector(`#${this.list.element.id}-opt-${index} .t`)
      if (!title) return
      const tag = title.ownerDocument.createElement('span'); tag.className = 'md-assist-ai-tag'; tag.textContent = this.options.strings.aiTag
      title.append(' ', tag)
    })
    this.live.textContent = rows.length ? this.options.strings.count(rows.length) : ''
    this.why.disabled = !rows.length
  }

  /** Editor keys while open. Returns true when the key was consumed. */
  keydown(event: KeyboardEvent): boolean {
    if (this.closed || isMarkdownComposition(event, this.options.editor)) return false
    const consume = () => { event.preventDefault(); event.stopImmediatePropagation(); return true }
    switch (event.key) {
      case 'Escape': this.close(); this.options.onDismissed(); return consume()
      // Tab never accepts: it closes and keeps its native meaning.
      case 'Tab': this.close(); this.options.onDismissed(); return false
      case 'Delete':
        if (this.active < 0) return false
        this.options.onDismissRow(this.active); return consume()
      case 'Enter':
        if (event.altKey) { this.close(); this.options.onDismissed(); return consume() }
        if (!this.rows.length) { this.close(); this.options.onDismissed(); return false }
        this.list.onKeyDown(event); return consume()
      case 'ArrowDown': case 'ArrowUp': case 'Home': case 'End':
        return this.list.onKeyDown(event) ? consume() : false
      // Typing filters the rows and never reaches the page; Backspace takes the filter back, with none left it deletes as usual.
      case 'Backspace':
        if (!this.filter) return false
        this.filter = this.filter.slice(0, -1); this.update(this.rows); return consume()
      default:
        if (event.key.length !== 1 || event.ctrlKey || event.metaKey || event.altKey || (!this.filter && event.key === ' ')) return false
        this.type(event.key); return consume()
    }
  }

  private type(text: string): void { this.filter += text; this.update(this.rows) }
  /** Text that comes without a key (an input method, dictation) filters too. */
  private readonly onText = (event: InputEvent) => {
    if (this.closed || event.inputType !== 'insertText' || !event.data || event.isComposing) return
    event.preventDefault(); event.stopPropagation(); this.type(event.data)
  }

  /** `menuOut` (120 ms), then the popup goes. */
  close(animated = true): void {
    if (this.closed) return
    this.closed = true
    this.root.removeEventListener('beforeinput', this.onText as EventListener, true)
    for (const [name, value] of this.previous) { if (value === null) this.root.removeAttribute(name); else this.root.setAttribute(name, value) }
    this.element.inert = true
    const destroy = () => { this.popup.destroy(); this.list.destroy() }
    if (!animated) { destroy(); return }
    void finished(menuOut(this.element, assistMotion(this.root, this.options.reducedMotion))).then(destroy)
  }
}
