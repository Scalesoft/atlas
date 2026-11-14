import type { DOMOutputSpec } from '@tiptap/pm/model'
import { formatMarkdownShortcut } from '../../core/keyboardShortcuts'
import { createIconElement, type MarkdownIconName } from '../../ui/iconMarkup'
import { modulesStrings } from '../../ui/strings/modules'
import { CHIP_TO_TEXT_KEYS } from './chipText'

/*
 * The inline chip: one look and one set of states for every chip in a sentence (spec: owner 2026-10-30, „Čip v textu“). Two looks,
 * taken from the two the owner likes: a tag (`md-chip--tag`, framed and soft, as the YouTrack issue: an issue, a page, a
 * requirement's facts) and a mention (`md-chip--mention`, a person: the name with an 18 px avatar, framed only on hover, as the
 * internal contact mention). Both share the slots (media, label, detail), the states (unresolved: dashed with „?“, muted, selected:
 * `ProseMirror-selectednode` or `data-chip-selected`), the focus ring and the motion tokens; colours and sizes are `--md-chip-*`
 * tokens (chip.css, loaded with the editor styles, styles/editor.css) a host or a chip's tone overrides. Blocks stay blocks: a chip
 * is only for an atom inside a line of text.
 */

/** A run of text in a chip's label or detail, with an optional class of its own (an issue key's monospace). */
export interface MarkdownChipPart { readonly text: string; readonly className?: string }
export type MarkdownChipText = string | readonly MarkdownChipPart[]
export interface MarkdownChipOptions {
  /** `tag` (default): framed and soft; `mention`: a person, the name with an avatar. */
  readonly variant?: 'tag' | 'mention'
  /** The element: `a` with `href` for a link, `button` for a control, `span` otherwise (default). */
  readonly tag?: 'span' | 'a' | 'button'
  readonly href?: string
  readonly label: MarkdownChipText
  /** Muted text after the label: an issue's state, an unresolved person's likely candidates. */
  readonly detail?: MarkdownChipText
  /** Before the label: an icon of the editor's set, or an element (a person's avatar). */
  readonly icon?: MarkdownIconName
  readonly media?: Element
  /** `unresolved`: dashed with „?“ (a person not decided yet); `muted`: greyed (a done requirement's facts). */
  readonly state?: 'unresolved' | 'muted'
  readonly title?: string
  /** What a screen reader says, with the state words („Jana Nováková, nerozhodnutý kontakt“); the label otherwise. */
  readonly ariaLabel?: string
  /** An editable chip names its keys in the tooltip (back to text, the actions menu). */
  readonly editable?: boolean
  /** Extra classes of the chip's own kind (`md-youtrack-issue`). */
  readonly className?: string
}

/** The keys every editable chip names in its tooltip. */
export const markdownChipHint = () => modulesStrings.chipHint(formatMarkdownShortcut(CHIP_TO_TEXT_KEYS), formatMarkdownShortcut(['Mod', 'Space']))

const parts = (text: MarkdownChipText): readonly MarkdownChipPart[] => typeof text === 'string' ? [{ text }] : text
const plain = (text: MarkdownChipText | undefined) => text === undefined ? '' : parts(text).map(part => part.text).join('')
const classOf = (options: MarkdownChipOptions) => ['md-chip', `md-chip--${options.variant ?? 'tag'}`, options.className].filter(Boolean).join(' ')
const titleOf = (options: MarkdownChipOptions) => [options.title, options.editable ? markdownChipHint() : ''].filter(Boolean).join('\n') || undefined

/** The chip as a ProseMirror DOM spec (a node's `renderHTML`): the same markup `createMarkdownChip` builds, without media. */
export function markdownChipSpec(options: MarkdownChipOptions, attrs: Record<string, string | undefined> = {}): DOMOutputSpec {
  const spans = (text: MarkdownChipText, slot: string) => parts(text).map(part => ['span', { class: [slot, part.className].filter(Boolean).join(' ') }, part.text])
  return [options.tag ?? 'span', {
    ...attrs, class: classOf(options), ...(options.href ? { href: options.href } : {}), ...(options.state ? { 'data-chip-state': options.state } : {}),
    ...(titleOf(options) ? { title: titleOf(options) } : {}), ...(options.ariaLabel ? { 'aria-label': options.ariaLabel } : {}),
  }, ...spans(options.label, 'md-chip-label'), ...(options.detail !== undefined ? spans(options.detail, 'md-chip-detail') : []),
  ...(options.state === 'unresolved' ? [['span', { class: 'md-chip-mark', 'aria-hidden': 'true' }, '?']] : [])] as DOMOutputSpec
}

export interface MarkdownChip {
  readonly element: HTMLElement
  /** Changes what the chip shows (the label, the detail once candidates are known, the state); unnamed options stay. */
  update(options: Partial<MarkdownChipOptions>): void
}

/** The chip as an element (a node view, a widget); `element` makes an existing one the chip (a static renderer's link). */
export function createMarkdownChip(doc: Document, options: MarkdownChipOptions, element: HTMLElement = doc.createElement(options.tag ?? 'span')): MarkdownChip {
  let current = options
  const fill = (into: HTMLElement[], text: MarkdownChipText, slot: string) => parts(text).forEach(part => {
    const span = doc.createElement('span'); span.className = [slot, part.className].filter(Boolean).join(' '); span.textContent = part.text; into.push(span)
  })
  const render = () => {
    element.className = classOf(current)
    const children: Element[] = []
    const media = current.media ?? (current.icon ? createIconElement(doc, current.icon, 14) : undefined)
    if (media) { media.classList.add('md-chip-media'); media.setAttribute('aria-hidden', 'true'); children.push(media) }
    const spans: HTMLElement[] = []
    fill(spans, current.label, 'md-chip-label')
    if (current.detail !== undefined && plain(current.detail)) fill(spans, current.detail, 'md-chip-detail')
    if (current.state === 'unresolved') { const mark = doc.createElement('span'); mark.className = 'md-chip-mark'; mark.setAttribute('aria-hidden', 'true'); mark.textContent = '?'; spans.push(mark) }
    element.replaceChildren(...children, ...spans)
    if (current.href && element instanceof HTMLAnchorElement) element.href = current.href
    // In the writer's text a chip is no Tab stop (Esc, Tab leaves the editor; the caret and Enter reach it); a reader tabs through links.
    if (current.editable) element.tabIndex = -1; else element.removeAttribute('tabindex')
    if (current.state) element.dataset.chipState = current.state; else delete element.dataset.chipState
    const title = titleOf(current)
    if (title) element.title = title; else element.removeAttribute('title')
    element.setAttribute('aria-label', current.ariaLabel ?? [plain(current.label), plain(current.detail)].filter(Boolean).join(' '))
  }
  render()
  return { element, update(next) { current = { ...current, ...next }; render() } }
}
