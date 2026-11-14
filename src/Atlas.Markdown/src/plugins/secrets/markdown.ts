// Lossless Markdown form of secret references (README "Markdown form"). A reference is a directive that carries only
// the reference attributes, never a value:
//
//     ::secret{secretId=d847b3af-4a87-4e97-aab8-a74a4cf48c89 fields="username password" showDetails=true}
//     VPN login :secret{secretId=d847b3af-4a87-4e97-aab8-a74a4cf48c89 autoHideSeconds=null}
//
// `::secret{…}` alone on a line is a block reference (card), `:secret{…}` inside text an inline one. Attributes at
// their default are left out. The syntax is recognized only where the page's own Markdown is parsed (the page
// source, stored Markdown); pasted or dropped Markdown from another wiki keeps it as literal text, and HTML paste
// can never create a reference.
import type { Node as PMNode } from '@tiptap/pm/model'
import { isMarkdownImportActive } from '../../core'
import {
  DEFAULT_AUTO_HIDE_SECONDS, DEFAULT_FIELD, DEFAULT_SHOW_COPY, DEFAULT_SHOW_DETAILS, reference, type ReferenceAttrs, type SecretField,
} from './model'
import { secretsStrings } from './strings'

type Layout = 'inline' | 'block'

/** The directive name in `:secret{…}` / `::secret{…}`. */
export const SECRET_DIRECTIVE = 'secret'
const OPENER = `:${SECRET_DIRECTIVE}{`
/** Plain text that would read as a directive: a document holding it cannot round-trip through Markdown. */
const DIRECTIVE_TEXT = new RegExp(`:${SECRET_DIRECTIVE}\\{`)
const MAX_DIRECTIVE_LENGTH = 512

/**
 * Marks the HTML the plugin's own Markdown rule writes for the ProseMirror parser. It is random per page load and
 * never leaves memory (neither the document, HTML output nor the clipboard carry it), so clipboard HTML cannot
 * pose as a parsed directive.
 */
const SOURCE_TOKEN = (() => {
  const bytes = new Uint8Array(16)
  globalThis.crypto.getRandomValues(bytes)
  return [...bytes].map(byte => byte.toString(16).padStart(2, '0')).join('')
})()

/** Every attribute of a node as stored, defaults filled in; `fields` keeps its persisted order. */
function nodeAttrs(value: unknown): ReferenceAttrs {
  const valid = reference(value)
  const fields = (value as { fields?: readonly SecretField[] | null }).fields
  return { ...valid, fields: fields === undefined || fields === null ? null : Object.freeze([...fields]) }
}

/** The directive of one reference node; only non-default attributes are written. */
export function serializeSecretDirective(layout: Layout, attrs: unknown): string {
  const node = nodeAttrs(attrs)
  const parts = [`secretId=${node.secretId}`]
  if (node.field !== DEFAULT_FIELD) parts.push(`field=${node.field}`)
  if (node.fields !== null) parts.push(node.fields.length === 1 ? `fields=${node.fields[0]}` : `fields="${node.fields.join(' ')}"`)
  if (node.showCopy !== DEFAULT_SHOW_COPY) parts.push(`showCopy=${node.showCopy}`)
  if (node.showDetails !== DEFAULT_SHOW_DETAILS) parts.push(`showDetails=${node.showDetails}`)
  if (node.autoHideSeconds !== DEFAULT_AUTO_HIDE_SECONDS) parts.push(`autoHideSeconds=${node.autoHideSeconds}`)
  return `${layout === 'block' ? ':' : ''}:${SECRET_DIRECTIVE}{${parts.join(' ')}}`
}

const ATTRIBUTE = /[ \t]*([A-Za-z][A-Za-z0-9]*)=(?:"([^"\\\n]*)"|([^\s"{}=\\]+))/y
function invalid(): never { throw new Error(secretsStrings.markdownInvalidReference) }

/**
 * Parses the attribute list between the braces. Anything but `name=value` pairs, a repeated name, an unknown
 * name (a value, a label…) or an invalid value rejects the whole directive; nothing is silently dropped.
 */
export function parseSecretDirectiveAttributes(body: string): ReferenceAttrs {
  const raw: Record<string, unknown> = {}
  ATTRIBUTE.lastIndex = 0
  let position = 0
  while (position < body.length) {
    if (!body.slice(position).trim()) break
    ATTRIBUTE.lastIndex = position
    const match = ATTRIBUTE.exec(body)
    if (!match) invalid()
    const [, name, quoted, bare] = match
    if (Object.hasOwn(raw, name!)) invalid()
    const text = quoted ?? bare!
    raw[name!] = name === 'fields' ? text.split(/[ \t]+/).filter(Boolean)
      : name === 'showCopy' || name === 'showDetails' ? (text === 'true' ? true : text === 'false' ? false : text)
      : name === 'autoHideSeconds' ? (text === 'null' ? null : /^\d{1,5}$/.test(text) ? Number(text) : text)
      : text
    position = ATTRIBUTE.lastIndex
  }
  try { return nodeAttrs(raw) } catch { return invalid() }
}

// Minimal structural markdown-it surface; tiptap-markdown does not ship its types.
interface MdToken { meta: unknown; map: [number, number] | null; block: boolean }
interface MdInlineState { src: string; pos: number; posMax: number; push(type: string, tag: string, nesting: number): MdToken }
interface MdBlockState {
  src: string; bMarks: number[]; eMarks: number[]; tShift: number[]; sCount: number[]; blkIndent: number; line: number
  push(type: string, tag: string, nesting: number): MdToken
}
interface MarkdownIt {
  inline: { ruler: { before(name: string, rule: string, fn: (state: MdInlineState, silent: boolean) => boolean): void } }
  block: { ruler: { before(name: string, rule: string, fn: (state: MdBlockState, start: number, end: number, silent: boolean) => boolean, options?: { alt: string[] }): void } }
  renderer: { rules: Record<string, ((tokens: MdToken[], index: number) => string) | undefined> }
  utils: { escapeHtml(value: string): string }
}

/** `{…}` right after the opener at `start`, or null when the braces do not close on this line. */
function directiveBody(source: string, start: number, end: number): { body: string; end: number } | null {
  const open = start + OPENER.length
  const limit = Math.min(end, open + MAX_DIRECTIVE_LENGTH)
  for (let position = open; position < limit; position++) {
    const char = source[position]
    if (char === '}') return { body: source.slice(open, position), end: position + 1 }
    if (char === '{' || char === '\n') return null
  }
  return null
}

function inlineRule(state: MdInlineState, silent: boolean): boolean {
  const { src, pos } = state
  if (isMarkdownImportActive() || !src.startsWith(OPENER, pos) || src[pos - 1] === ':') return false
  const found = directiveBody(src, pos, state.posMax)
  if (!found) return false
  const attrs = parseSecretDirectiveAttributes(found.body)
  if (!silent) state.push('secret_inline', 'span', 0).meta = attrs
  state.pos = found.end
  return true
}

function blockRule(state: MdBlockState, startLine: number, _endLine: number, silent: boolean): boolean {
  if (isMarkdownImportActive() || state.sCount[startLine]! - state.blkIndent >= 4) return false
  const start = state.bMarks[startLine]! + state.tShift[startLine]!
  const end = state.eMarks[startLine]!
  if (state.src[start] !== ':' || !state.src.startsWith(OPENER, start + 1)) return false
  const found = directiveBody(state.src, start + 1, end)
  if (!found || state.src.slice(found.end, end).trim()) return false
  const attrs = parseSecretDirectiveAttributes(found.body)
  if (silent) return true
  const token = state.push('secret_block', 'div', 0)
  token.block = true; token.meta = attrs; token.map = [startLine, startLine + 1]
  state.line = startLine + 1
  return true
}

const installed = new WeakSet<object>()
/** tiptap-markdown calls this before every parse; the rules are added to each parser once. */
export function installSecretMarkdown(md: MarkdownIt): void {
  if (installed.has(md)) return
  installed.add(md)
  md.inline.ruler.before('emphasis', 'secret_inline', inlineRule)
  md.block.ruler.before('paragraph', 'secret_block', blockRule, { alt: ['paragraph', 'reference', 'blockquote', 'list'] })
  const render = (tag: 'span' | 'div') => (tokens: MdToken[], index: number) =>
    `<${tag} data-md-secret-source="${SOURCE_TOKEN}" data-md-secret-attrs="${md.utils.escapeHtml(JSON.stringify(tokens[index]!.meta))}"></${tag}>`
  md.renderer.rules.secret_inline = render('span')
  md.renderer.rules.secret_block = render('div')
}

/**
 * A copy of a reference carries no id: its HTML names an in-memory slot of this page load, filled when the clipboard HTML is made
 * (the ids never reach the clipboard). Pasting in the same session finds the attributes again (J3.7); HTML from anywhere else has no slot.
 */
const clipboardSlots = new Map<string, ReferenceAttrs>()
const CLIPBOARD_SLOTS = 200
export function secretClipboardSlot(attrs: unknown): string {
  const bytes = new Uint8Array(8)
  globalThis.crypto.getRandomValues(bytes)
  const slot = [...bytes].map(byte => byte.toString(16).padStart(2, '0')).join('')
  try { clipboardSlots.set(slot, nodeAttrs(attrs)) } catch { return '' }
  if (clipboardSlots.size > CLIPBOARD_SLOTS) clipboardSlots.delete(clipboardSlots.keys().next().value!)
  return slot
}

/** Parse rule for the plugin's own Markdown output only: other HTML (clipboard, static output) never matches. */
export function secretMarkdownParseRule(layout: Layout) {
  return {
    tag: `${layout === 'inline' ? 'span' : 'div'}[data-md-secret-source]`,
    priority: 60,
    getAttrs(element: HTMLElement): ReferenceAttrs | false {
      if (element.getAttribute('data-md-secret-source') !== SOURCE_TOKEN) return false
      try { return nodeAttrs(JSON.parse(element.getAttribute('data-md-secret-attrs') ?? 'null')) } catch { return false }
    },
  }
}

interface SerializerState { write(text: string): void; closeBlock(node: PMNode): void }
export function secretMarkdownSerializer(layout: Layout) {
  return (state: SerializerState, node: PMNode) => {
    state.write(serializeSecretDirective(layout, node.attrs))
    if (layout === 'block') state.closeBlock(node)
  }
}

/**
 * Content the Markdown form cannot carry: text that would itself read as a directive, and an inline reference
 * right after a colon (`::secret{…}` would read as a block or as text).
 */
export function secretMarkdownLossy(document: PMNode): boolean {
  let lossy = false
  document.descendants(node => {
    if (lossy) return false
    if (node.isText && !node.marks.some(mark => mark.type.spec.code) && DIRECTIVE_TEXT.test(node.text ?? '')) lossy = true
    if (node.isTextblock) {
      node.forEach((child, _offset, index) => {
        if (child.type.name !== 'secretInlineReference' || index === 0) return
        const before = node.child(index - 1)
        if (before.isText && before.text?.endsWith(':')) lossy = true
      })
    }
    return !lossy
  })
  return lossy
}


/** The parse rule of a same-session paste (see secretClipboardSlot). */
export function secretClipboardParseRule(layout: Layout) {
  return {
    tag: `${layout === 'inline' ? 'span' : 'div'}[data-md-secret-slot]`,
    priority: 60,
    getAttrs(element: HTMLElement): ReferenceAttrs | false { return clipboardSlots.get(element.getAttribute('data-md-secret-slot') ?? '') ?? false },
  }
}
