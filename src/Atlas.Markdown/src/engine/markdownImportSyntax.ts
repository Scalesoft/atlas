import type { Schema } from '@tiptap/pm/model'
import type {
  MarkdownEmbedResolver, MarkdownImportDegradedKind, MarkdownImportDegradedPart, MarkdownImportInfo,
  MarkdownWikiLinkRequest, MarkdownWikiLinkResolver,
} from '../core/types'
import { inputStrings } from '../ui/strings/input'
import { MARKDOWN_IMAGE_PROTOCOLS, MARKDOWN_LINK_PROTOCOLS, safeMarkdownUrl } from '../core/safeUrl'
import { MARKDOWN_DIALECT_SOURCE, markdownDialect } from './markdownDialectLoader'

/**
 * Foreign-wiki syntax (GitHub, Obsidian, Wiki.js) mapped onto existing document nodes while
 * pasted or dropped Markdown is parsed. Rules emit markup only for nodes and marks the editor's
 * schema actually has; anything else stays literal text and is reported as `degraded`, so the
 * user and the host learn what did not survive.
 *
 * Most rules are inert outside an import session, so stored Markdown values keep plain CommonMark
 * semantics and round-trip. The editor dialect is the exception: syntax the editor also writes
 * back (GitHub alerts for callouts, `==highlight==`, ```mermaid diagrams and `$…$`/`$$…$$`/```math
 * math) is parsed on every path — value load, `insertContent`, the page source and imports —
 * whenever the schema has its node or mark. Without it the syntax stays CommonMark, so the
 * markdown-safe profile keeps plain quotes and a literal `==`. Reporting stays import-only.
 */

// Minimal structural markdown-it surface; tiptap-markdown does not ship its types.
export interface MdToken {
  type: string; tag: string; nesting: number; level: number; content: string; markup: string; info: string
  block: boolean; hidden: boolean; map: [number, number] | null; meta: unknown; children: MdToken[] | null
  attrSet(name: string, value: string): void
}
export interface MdInlineState {
  src: string; pos: number; posMax: number; pending: string; md: MarkdownIt; env: unknown
  push(type: string, tag: string, nesting: number): MdToken
}
export interface MdBlockState {
  src: string; bMarks: number[]; eMarks: number[]; tShift: number[]; sCount: number[]; blkIndent: number; line: number; md: MarkdownIt
  push(type: string, tag: string, nesting: number): MdToken
}
export interface MdCoreState { src: string; tokens: MdToken[]; md: MarkdownIt; env: unknown; Token: new (type: string, tag: string, nesting: number) => MdToken }
export interface MdRenderer { renderToken(tokens: MdToken[], index: number, options: MdOptions): string }
export interface MdOptions { breaks?: boolean }
export type MdRender = (tokens: MdToken[], index: number, options: MdOptions, env: unknown, self: MdRenderer) => string
type InlineRule = (state: MdInlineState, silent: boolean) => boolean
type BlockRule = (state: MdBlockState, start: number, end: number, silent: boolean) => boolean
type CoreRule = (state: MdCoreState) => void
interface Ruler<R> { before(name: string, rule: string, fn: R, options?: { alt: string[] }): void; after(name: string, rule: string, fn: R): void; push(rule: string, fn: R): void }
export interface MarkdownIt {
  inline: { ruler: Ruler<InlineRule>; tokenize(state: MdInlineState): void; skipToken(state: MdInlineState): void; parse(src: string, md: MarkdownIt, env: unknown, out: MdToken[]): void }
  block: { ruler: Ruler<BlockRule> }
  core: { ruler: Ruler<CoreRule> }
  renderer: { rules: Record<string, MdRender | undefined> }
  render(src: string): string
  utils: { escapeHtml(value: string): string }
  helpers: { parseLinkLabel(state: MdInlineState, start: number, disableNested?: boolean): number }
  options: { linkify?: boolean }
  linkify: { match(text: string): readonly { index: number; lastIndex: number }[] | null }
}

// ---------------------------------------------------------------------------
// Import session
// ---------------------------------------------------------------------------

export interface MarkdownImportSession {
  readonly source: MarkdownImportInfo['source']
  readonly nodes: ReadonlySet<string>
  readonly marks: ReadonlySet<string>
  readonly resolveWikiLink?: MarkdownWikiLinkResolver
  readonly resolveEmbed?: MarkdownEmbedResolver
  readonly degraded: Map<MarkdownImportDegradedKind, { count: number; excerpt: string }>
  /** Footnote definitions by label, and labels in order of first reference. */
  readonly footnotes: Map<string, string>
  readonly footnoteOrder: string[]
  /** Plain text, not Markdown source: a line break stays one (a hard break), not CommonMark's space. */
  readonly lineBreaks?: boolean
}

let active: MarkdownImportSession | null = null
/** The import being parsed; null while a stored value is loaded. */
export const currentImportSession = (): MarkdownImportSession | null => active

/**
 * True while pasted or dropped Markdown (a foreign wiki, a `.md` file) is parsed, false while a stored value or
 * the page source is parsed. Plugin syntax that must never come from an import (for example a secret
 * reference) checks it in its markdown-it rule and stays literal text during an import.
 */
export const isMarkdownImportActive = (): boolean => active !== null

export function createImportSession(schema: Schema, options: {
  source: MarkdownImportInfo['source']; resolveWikiLink?: MarkdownWikiLinkResolver; resolveEmbed?: MarkdownEmbedResolver; lineBreaks?: boolean
}): MarkdownImportSession {
  return {
    source: options.source, nodes: new Set(Object.keys(schema.nodes)), marks: new Set(Object.keys(schema.marks)),
    resolveWikiLink: options.resolveWikiLink, resolveEmbed: options.resolveEmbed, lineBreaks: options.lineBreaks,
    degraded: new Map(), footnotes: new Map(), footnoteOrder: [],
  }
}

/** Parsing is synchronous; the session scopes import-only syntax to one parse call. */
export function withImportSession<T>(session: MarkdownImportSession, run: () => T): T {
  const previous = active
  active = session
  try { return run() } finally { active = previous }
}

const EXCERPT_LENGTH = 80
export function recordDegraded(session: MarkdownImportSession, kind: MarkdownImportDegradedKind, text: string): void {
  const entry = session.degraded.get(kind)
  if (entry) { entry.count++; return }
  const flat = text.replace(/\s+/g, ' ').trim()
  session.degraded.set(kind, { count: 1, excerpt: flat.length > EXCERPT_LENGTH ? `${flat.slice(0, EXCERPT_LENGTH - 1)}…` : flat })
}

export function degradedParts(session: MarkdownImportSession): readonly MarkdownImportDegradedPart[] {
  return Object.freeze([...session.degraded].map(([kind, { count, excerpt }]) => Object.freeze({ kind, count, excerpt })))
}

// ---------------------------------------------------------------------------
// Editor dialect
// ---------------------------------------------------------------------------

interface MarkdownSchemaNames { readonly nodes: ReadonlySet<string>; readonly marks: ReadonlySet<string> }
/** tiptap-markdown creates one markdown-it instance per editor; each knows its editor's schema. */
const dialects = new WeakMap<object, MarkdownSchemaNames>()

/** Records the node and mark names of the editor that parses with `md` (called from the parse setup hook). */
export function registerMarkdownDialect(md: MarkdownIt, schema: Schema): void {
  if (!dialects.has(md)) dialects.set(md, { nodes: new Set(Object.keys(schema.nodes)), marks: new Set(Object.keys(schema.marks)) })
}

/** Whether the schema parsing with `md` has the node or mark. */
export function schemaHas(md: MarkdownIt, kind: 'nodes' | 'marks', name: string): boolean {
  return (active ?? dialects.get(md))?.[kind].has(name) ?? false
}

/**
 * Whether an editor-dialect rule for `name` runs: in every import (which maps or reports it), otherwise
 * only where the schema has the target, so a stored value without it keeps plain CommonMark semantics.
 */
export function markdownDialectActive(md: MarkdownIt, kind: 'nodes' | 'marks', name: string): boolean {
  return active !== null || schemaHas(md, kind, name)
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

/** Whether a character is whitespace or past either end of the text (`undefined`). */
export const whitespace = (char: string | undefined) => char === undefined || /\s/.test(char)

/** A host-resolved URL: trimmed, at most 2048 characters, then the `set-link` rule. */
function safeUrl(value: unknown, protocols: readonly string[]): string | null {
  const url = typeof value === 'string' ? value.trim() : ''
  return url.length <= 2048 ? safeMarkdownUrl(url, protocols) : null
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() && value.length <= 500 ? value.trim() : undefined
}

function callHost<T>(hook: ((request: MarkdownWikiLinkRequest) => T) | undefined, request: MarkdownWikiLinkRequest): T | null {
  if (!hook) return null
  // A host callback must not break the user's paste or drop.
  try { return hook(request) ?? null } catch (error) { console.error(error); return null }
}

function pushText(state: MdInlineState, content: string): void {
  state.push('text', '', 0).content = content
}

// ---------------------------------------------------------------------------
// Callouts: GitHub/Obsidian alerts (editor dialect) and Wiki.js blockquote classes (imports)
// ---------------------------------------------------------------------------

type CalloutVariant = 'note' | 'tip' | 'warning' | 'important' | 'caution'
interface Alert { variant: CalloutVariant; title?: string; foldable?: boolean }
const calloutLabels: Record<CalloutVariant, string> = {
  get note() { return inputStrings.calloutNote },
  get tip() { return inputStrings.calloutTip },
  get warning() { return inputStrings.calloutWarning },
  get important() { return inputStrings.calloutImportant },
  get caution() { return inputStrings.calloutCaution },
}

/**
 * GitHub's five alert types and Obsidian's callout types onto the five callout variants. A callout is
 * written back as its GitHub type (ContentBlocks.ts), so every GitHub alert returns unchanged.
 */
function alertVariant(type: string): CalloutVariant {
  const name = type.toLowerCase()
  if (['tip', 'hint', 'success', 'check', 'done'].includes(name)) return 'tip'
  if (['warning', 'attention'].includes(name)) return 'warning'
  if (name === 'important') return 'important'
  if (['caution', 'danger', 'error', 'bug', 'failure', 'fail', 'missing'].includes(name)) return 'caution'
  return 'note'
}
const wikiJsVariants: Record<string, CalloutVariant> = { info: 'note', success: 'tip', warning: 'warning', danger: 'caution' }

const ALERT_MARKER = /^\[!([A-Za-z][\w-]*)\]([+-]?)(?:[ \t]+([^\n]*))?[ \t]*(?:\n|$)/
const WIKIJS_CLASS_TAIL = /(?:^|\n)[ \t]*\{[ \t]*\.is-(info|success|warning|danger)[ \t]*\}[ \t]*$/
const WIKIJS_CLASS_LINE = /^\{[ \t]*\.is-(info|success|warning|danger)[ \t]*\}$/

function closingIndex(tokens: MdToken[], open: number): number {
  const level = tokens[open]!.level
  for (let index = open + 1; index < tokens.length; index++) {
    if (tokens[index]!.type === 'blockquote_close' && tokens[index]!.level === level) return index
  }
  return -1
}

function githubAlert(tokens: MdToken[], open: number): Alert | null {
  const paragraph = tokens[open + 1], inline = tokens[open + 2]
  if (paragraph?.type !== 'paragraph_open' || inline?.type !== 'inline') return null
  const match = ALERT_MARKER.exec(inline.content)
  if (!match) return null
  inline.content = inline.content.slice(match[0].length)
  if (!inline.content.trim()) tokens.splice(open + 1, 3)
  const title = text(match[3])
  return { variant: alertVariant(match[1]!), ...(title ? { title } : {}), ...(match[2] ? { foldable: true } : {}) }
}

function wikiJsAlert(tokens: MdToken[], open: number, close: number): Alert | null {
  const level = tokens[open]!.level
  const inline = tokens[close - 2], paragraphClose = tokens[close - 1]
  // `{.is-warning}` directly below the quote is a lazy continuation of its last paragraph.
  if (paragraphClose?.type === 'paragraph_close' && paragraphClose.level === level + 1 && inline?.type === 'inline') {
    const match = WIKIJS_CLASS_TAIL.exec(inline.content)
    if (match) {
      inline.content = inline.content.slice(0, match.index)
      if (!inline.content.trim()) tokens.splice(close - 3, 3)
      return { variant: wikiJsVariants[match[1]!]! }
    }
  }
  const next = tokens[close + 1], nextInline = tokens[close + 2], nextClose = tokens[close + 3]
  const match = next?.type === 'paragraph_open' && nextInline?.type === 'inline' && nextClose?.type === 'paragraph_close'
    ? WIKIJS_CLASS_LINE.exec(nextInline.content.trim()) : null
  if (!match) return null
  tokens.splice(close + 1, 3)
  return { variant: wikiJsVariants[match[1]!]! }
}

function mapAlerts(state: MdCoreState): void {
  if (!markdownDialectActive(state.md, 'nodes', 'callout')) return
  const { tokens } = state
  // Innermost first: edits only touch tokens after the quote being processed.
  for (let open = tokens.length - 1; open >= 0; open--) {
    if (tokens[open]!.type !== 'blockquote_open') continue
    const close = closingIndex(tokens, open)
    if (close < 0) continue
    const alert = githubAlert(tokens, open) ?? (active ? wikiJsAlert(tokens, open, close) : null)
    if (!alert) continue
    tokens[open]!.meta = { alert }
    tokens[closingIndex(tokens, open)]!.meta = { alert }
  }
}

function alertOf(token: MdToken): Alert | undefined {
  return (token.meta as { alert?: Alert } | null)?.alert
}

// ---------------------------------------------------------------------------
// Wiki links and embeds
// ---------------------------------------------------------------------------

function wikiRequest(raw: string, source: MarkdownImportInfo['source']): MarkdownWikiLinkRequest {
  const pipe = raw.indexOf('|')
  const reference = pipe < 0 ? raw : raw.slice(0, pipe)
  const alias = pipe < 0 ? undefined : raw.slice(pipe + 1).trim() || undefined
  const hash = reference.indexOf('#')
  const target = (hash < 0 ? reference : reference.slice(0, hash)).trim()
  const heading = hash < 0 ? undefined : reference.slice(hash + 1).trim() || undefined
  return Object.freeze({ target, ...(heading ? { heading } : {}), ...(alias ? { alias } : {}), raw, source })
}

function defaultLabel(request: MarkdownWikiLinkRequest): string {
  if (request.alias) return request.alias
  if (request.heading) return request.target ? `${request.target} › ${request.heading}` : request.heading
  return request.target
}

function pushLink(state: MdInlineState, href: string, label: string): void {
  state.push('md_import_link', 'a', 0).meta = { href, label }
}

function wikiLink(state: MdInlineState, silent: boolean): boolean {
  const session = active
  if (!session) return false
  const { src, pos } = state
  const embed = src.charCodeAt(pos) === 0x21 /* ! */
  const start = embed ? pos + 1 : pos
  if (src.charCodeAt(start) !== 0x5B || src.charCodeAt(start + 1) !== 0x5B) return false
  const close = src.indexOf(']]', start + 2)
  if (close < 0 || close + 2 > state.posMax) return false
  const raw = src.slice(start + 2, close)
  if (!raw.trim() || raw.length > 500 || /[[\]\n]/.test(raw)) return false
  state.pos = close + 2
  if (silent) return true
  const literal = src.slice(pos, close + 2)
  const request = wikiRequest(raw, session.source)
  if (!embed && !request.heading && !request.alias && /^_?toc_?$/i.test(request.target)) {
    recordDegraded(session, 'toc', literal); pushText(state, literal)
    return true
  }
  if (embed) {
    const result = callHost(session.resolveEmbed, request)
    const src = result && 'src' in result ? safeUrl(result.src, MARKDOWN_IMAGE_PROTOCOLS) : null
    const href = result && 'href' in result ? safeUrl(result.href, MARKDOWN_LINK_PROTOCOLS) : null
    if (src) state.push('md_import_image', 'img', 0).meta = { src, alt: text((result as { alt?: unknown }).alt) ?? request.alias ?? request.target }
    else if (href) pushLink(state, href, text((result as { label?: unknown }).label) ?? defaultLabel(request))
    else { recordDegraded(session, 'embed', literal); pushText(state, literal) }
    return true
  }
  const result = callHost(session.resolveWikiLink, request)
  const href = result ? safeUrl(result.href, MARKDOWN_LINK_PROTOCOLS) : null
  if (href) pushLink(state, href, text(result!.label) ?? defaultLabel(request))
  else { recordDegraded(session, 'wiki-link', literal); pushText(state, literal) }
  return true
}

// ---------------------------------------------------------------------------
// Footnotes → superscript references and an endnote list
// ---------------------------------------------------------------------------

const FOOTNOTE_LABEL = /^\[\^([^\s[\]]{1,100})\]/
const superscriptDigits = '⁰¹²³⁴⁵⁶⁷⁸⁹'
const footnotesSupported = (session: MarkdownImportSession) => !session.nodes.has('footnote') && !session.nodes.has('footnoteReference')
const BLOCK_START = /^(?:#{1,6}[ \t]|>|[-*+][ \t]|\d{1,9}[.)][ \t]|```|~~~|\|)/

function footnoteDefinition(state: MdBlockState, startLine: number, endLine: number, silent: boolean): boolean {
  const session = active
  if (!session || !footnotesSupported(session) || state.sCount[startLine]! - state.blkIndent >= 4) return false
  const lineText = (line: number) => state.src.slice(state.bMarks[line]! + state.tShift[line]!, state.eMarks[line]!)
  const first = lineText(startLine)
  const match = /^\[\^([^\s[\]]{1,100})\]:[ \t]?/.exec(first)
  if (!match) return false
  if (silent) return true
  const parts = [first.slice(match[0].length)]
  let line = startLine + 1
  for (; line < endLine; line++) {
    const content = lineText(line)
    const indented = state.sCount[line]! - state.blkIndent >= 4
    if (!content.trim()) {
      // A blank line continues the note only when an indented paragraph follows.
      let next = line + 1
      while (next < endLine && !lineText(next).trim()) next++
      if (next < endLine && state.sCount[next]! - state.blkIndent >= 4) continue
      break
    }
    if (!indented && (FOOTNOTE_LABEL.test(content) || BLOCK_START.test(content))) break
    parts.push(content)
  }
  if (!session.footnotes.has(match[1]!)) session.footnotes.set(match[1]!, parts.join('\n').trim())
  state.line = line
  return true
}

function footnoteReference(state: MdInlineState, silent: boolean): boolean {
  const session = active
  if (!session || !footnotesSupported(session) || state.src.charCodeAt(state.pos) !== 0x5B || state.src.charCodeAt(state.pos + 1) !== 0x5E) return false
  const match = FOOTNOTE_LABEL.exec(state.src.slice(state.pos, Math.min(state.posMax, state.pos + 104)))
  if (!match) return false
  state.pos += match[0].length
  if (silent) return true
  const label = match[1]!
  if (!session.footnotes.has(label)) { recordDegraded(session, 'footnote', match[0]); pushText(state, match[0]); return true }
  let index = session.footnoteOrder.indexOf(label)
  if (index < 0) index = session.footnoteOrder.push(label) - 1
  state.push('md_import_footnote_ref', 'sup', 0).meta = { number: index + 1 }
  return true
}

/** GitHub renders notes as a rule and a numbered list at the end; referenced notes first. */
function appendEndnotes(state: MdCoreState): void {
  const session = active
  if (!session || !session.footnotes.size) return
  const labels = [...session.footnoteOrder, ...[...session.footnotes.keys()].filter(label => !session.footnoteOrder.includes(label))]
  const token = (type: string, tag: string, nesting: number) => {
    const next = new state.Token(type, tag, nesting)
    next.block = true
    state.tokens.push(next)
    return next
  }
  token('hr', 'hr', 0).markup = '---'
  token('ordered_list_open', 'ol', 1)
  for (const label of labels) {
    token('list_item_open', 'li', 1)
    token('paragraph_open', 'p', 1)
    const inline = token('inline', '', 0)
    inline.content = session.footnotes.get(label)!
    inline.children = []
    state.md.inline.parse(inline.content, state.md, state.env, inline.children)
    token('paragraph_close', 'p', -1)
    token('list_item_close', 'li', -1)
  }
  token('ordered_list_close', 'ol', -1)
}

// ---------------------------------------------------------------------------
// Inline marks: ==highlight== (editor dialect), ~sub~, ^sup^ (imports)
// ---------------------------------------------------------------------------

/**
 * The last `==` scan that found no closer. A later opener inside its range cannot find one either (closers
 * do not depend on the opener), so text with many unmatched `==` stays linear, like `$` in markdownInput.ts.
 */
const unclosed = { src: '', max: 0, from: 0, to: 0 }
/** Bare links (linkify) of the last word looked at, so the `==` runs of one long word are matched once. */
const linkWord: { src: string; from: number; to: number; links: readonly { index: number; lastIndex: number }[] } = { src: '', from: 0, to: 0, links: [] }
const WORD_LIMIT = 2048

/**
 * Whether the `==` at `pos` is part of a bare URL or e-mail (`?a==b==c`), which linkify turns into a link;
 * the page source reads it as text (`readMarkdownLiterally`), and the address stays whole there too.
 */
function inBareLink(state: MdInlineState, pos: number): boolean {
  const { src, md } = state
  if (linkWord.src !== src || pos < linkWord.from || pos + 2 > linkWord.to) {
    let from = pos, to = pos
    while (from > 0 && !whitespace(src[from - 1]) && pos - from < WORD_LIMIT) from--
    while (to < src.length && !whitespace(src[to]) && to - pos < WORD_LIMIT) to++
    const word = src.slice(from, to)
    Object.assign(linkWord, { src, from, to, links: /[.:@]/.test(word) ? md.linkify.match(word) ?? [] : [] })
  }
  return linkWord.links.some(link => linkWord.from + link.index < pos + 2 && pos < linkWord.from + link.lastIndex)
}

function highlight(state: MdInlineState, silent: boolean): boolean {
  const { src, pos: start, posMax: max, md } = state
  if (src.charCodeAt(start) !== 0x3D || src.charCodeAt(start + 1) !== 0x3D || src.charCodeAt(start - 1) === 0x3D) return false
  if (src[start + 2] === '=' || whitespace(src[start + 2]) || !markdownDialectActive(md, 'marks', 'highlight')) return false
  if (unclosed.src === src && unclosed.max === max && start > unclosed.from && start < unclosed.to) return false
  // Code spans, autolinks and link destinations are whole tokens; a bare URL is text until linkify runs.
  if (inBareLink(state, start)) return false
  // Skip nested constructs (code spans, links) like markdown-it's link label scanner.
  let end = -1
  state.pos = start + 2
  while (state.pos < max) {
    if (src.charCodeAt(state.pos) === 0x3D && src.charCodeAt(state.pos + 1) === 0x3D && !whitespace(src[state.pos - 1])) { end = state.pos; break }
    state.md.inline.skipToken(state)
  }
  const scanned = state.pos
  state.pos = start
  if (end < 0 || end + 2 > max) { Object.assign(unclosed, { src, max, from: start, to: scanned }); return false }
  if (inBareLink(state, end)) return false
  if (!schemaHas(md, 'marks', 'highlight')) {
    if (!silent && active) recordDegraded(active, 'highlight', src.slice(start, end + 2))
    return false
  }
  if (!silent) {
    state.pos = start + 2
    state.posMax = end
    state.push('mark_open', 'mark', 1).markup = '=='
    state.md.inline.tokenize(state)
    state.push('mark_close', 'mark', -1).markup = '=='
    state.posMax = max
  }
  state.pos = end + 2
  return true
}

function script(char: '~' | '^', mark: 'subscript' | 'superscript', tag: 'sub' | 'sup'): InlineRule {
  return (state, silent) => {
    const session = active
    const { src, pos, posMax } = state
    if (!session || src[pos] !== char) return false
    if (char === '~' && (src[pos + 1] === '~' || src[pos - 1] === '~')) return false
    let end = pos + 1
    while (end < posMax && src[end] !== char) {
      if (whitespace(src[end])) return false
      end += src[end] === '\\' ? 2 : 1
    }
    if (end >= posMax || end === pos + 1 || (char === '~' && src[end + 1] === '~')) return false
    if (!session.marks.has(mark)) {
      if (!silent) recordDegraded(session, mark, src.slice(pos, end + 1))
      return false
    }
    if (!silent) {
      state.push(`${tag}_open`, tag, 1).markup = char
      pushText(state, src.slice(pos + 1, end).replace(/\\(.)/g, '$1'))
      state.push(`${tag}_close`, tag, -1).markup = char
    }
    state.pos = end + 1
    return true
  }
}

// ---------------------------------------------------------------------------
// Raw HTML and comments (html: false keeps them literal; report them)
// ---------------------------------------------------------------------------

const HTML_TAG = /^<(?:(\/?)([A-Za-z][A-Za-z0-9-]*)(?:\s[^<>]*?)?\s*\/?>|!--([\s\S]*?)-->)/

function htmlLiteral(state: MdInlineState, silent: boolean): boolean {
  const session = active
  if (!session || state.src.charCodeAt(state.pos) !== 0x3C) return false
  const match = HTML_TAG.exec(state.src.slice(state.pos, state.posMax))
  if (!match) return false
  state.pos += match[0].length
  if (silent) return true
  // GitHub and Wiki.js pages use `<br>` for a line break inside a paragraph or table cell.
  if (!match[1] && match[2]?.toLowerCase() === 'br') { state.push('hardbreak', 'br', 0); return true }
  if (match[3] !== undefined) recordDegraded(session, 'comment', match[0])
  else if (!match[1]) recordDegraded(session, 'html', match[0])
  state.pending += match[0]
  return true
}

/** Obsidian `%%comment%%`, hidden in its reading view. */
function obsidianComment(state: MdInlineState, silent: boolean): boolean {
  const session = active
  const { src, pos } = state
  if (!session || src.charCodeAt(pos) !== 0x25 || src.charCodeAt(pos + 1) !== 0x25) return false
  const end = src.indexOf('%%', pos + 2)
  if (end < 0 || end + 2 > state.posMax || end === pos + 2) return false
  state.pos = end + 2
  if (silent) return true
  recordDegraded(session, 'comment', src.slice(pos, end + 2))
  state.pending += src.slice(pos, end + 2)
  return true
}

// ---------------------------------------------------------------------------
// Installation
// ---------------------------------------------------------------------------

const installed = new WeakSet<object>()

/** One level below the nearest top-level heading before `index` (2–6), 3 with none: the rule of a disclosure's heading (legacyDocument.ts). */
function foldLevel(tokens: MdToken[], index: number): number {
  for (let at = index - 1; at >= 0; at--) {
    const token = tokens[at]!
    if (token.type === 'heading_open' && token.level === 0) return Math.min(6, Math.max(2, Number(token.tag.slice(1)) + 1))
  }
  return 3
}

export function installImportSyntax(md: MarkdownIt): void {
  if (installed.has(md)) return
  installed.add(md)
  const escape = (value: string) => md.utils.escapeHtml(value)
  md.core.ruler.after('block', 'import_alerts', mapAlerts)
  md.core.ruler.after('inline', 'import_endnotes', appendEndnotes)
  // The `a-` component rules join once the dialect loads (markdownInput); until then Markdown with a component fails.
  md.core.ruler.before('normalize', 'dialect_gate', state => { if (MARKDOWN_DIALECT_SOURCE.test(state.src)) markdownDialect() })
  md.block.ruler.before('reference', 'import_footnote_definition', footnoteDefinition)
  md.inline.ruler.before('link', 'import_footnote_reference', footnoteReference)
  // One rule for `[[…]]` and `![[…]]`: it runs before both `link` and `image`.
  md.inline.ruler.before('link', 'import_wiki_link', wikiLink)
  md.inline.ruler.before('emphasis', 'import_highlight', highlight)
  md.inline.ruler.before('emphasis', 'import_superscript', script('^', 'superscript', 'sup'))
  md.inline.ruler.before('strikethrough', 'import_subscript', script('~', 'subscript', 'sub'))
  md.inline.ruler.before('entity', 'import_html_literal', htmlLiteral)
  md.inline.ruler.before('entity', 'import_obsidian_comment', obsidianComment)

  const rules = md.renderer.rules
  rules.md_import_link = (tokens, index) => {
    const { href, label } = tokens[index]!.meta as { href: string; label: string }
    return `<a href="${escape(href)}">${escape(label)}</a>`
  }
  rules.md_import_image = (tokens, index) => {
    const { src, alt } = tokens[index]!.meta as { src: string; alt: string }
    return `<img src="${escape(src)}" alt="${escape(alt)}">`
  }
  rules.md_import_footnote_ref = (tokens, index) => {
    const { number } = tokens[index]!.meta as { number: number }
    return active?.marks.has('superscript') ? `<sup>${number}</sup>` : [...String(number)].map(digit => superscriptDigits[Number(digit)]).join('')
  }

  const quoteOpen = rules.blockquote_open, quoteClose = rules.blockquote_close
  const fallback = (rule: MdRender | undefined): MdRender => (tokens, index, options, env, self) => rule ? rule(tokens, index, options, env, self) : self.renderToken(tokens, index, options)
  // An alert only exists where mapAlerts ran: in an import, or for a schema with callouts.
  rules.blockquote_open = (tokens, index, options, env, self) => {
    const alert = alertOf(tokens[index]!)
    if (!alert) return fallback(quoteOpen)(tokens, index, options, env, self)
    const title = alert.title ?? calloutLabels[alert.variant]
    // Markdown-safe has no callout: a quote headed by the label keeps the meaning in plain Markdown.
    if (!schemaHas(md, 'nodes', 'callout')) return `<blockquote><p><strong>${escape(title)}</strong></p>`
    // A foldable one (`> [!tip]- Title`) is a heading over the box (every heading folds), at the level a stored disclosure converts to.
    const fold = alert.foldable, level = foldLevel(tokens, index)
    return (fold ? `<h${level}>${escape(title)}</h${level}>` : '')
      + `<aside data-callout="" data-variant="${alert.variant}">`
      + (alert.title && !fold ? `<p><strong>${escape(alert.title)}</strong></p>` : '')
  }
  rules.blockquote_close = (tokens, index, options, env, self) => {
    const alert = alertOf(tokens[index]!)
    if (!alert) return fallback(quoteClose)(tokens, index, options, env, self)
    if (!schemaHas(md, 'nodes', 'callout')) return '</blockquote>'
    return '</aside>'
  }

  // ```math, ```mermaid and the dialect's ```a-<name> fences carry their source for a plugin node's parse rule;
  // without the plugin (or when its validation rejects the source) the wrapped code block is imported.
  // Outside an import only a schema with the plugin node wraps them (editor dialect). ```a-code <language> is a
  // code block in that language: the `a-` namespace is reserved for components.
  const fence = fallback(rules.fence)
  rules.fence = (tokens, index, options, env, self) => {
    const token = tokens[index]!
    const name = token.info.trim().split(/\s+/)[0] ?? ''
    if (name.startsWith('a-')) return markdownDialect().renderDialectFence(md, token, () => fence(tokens, index, options, env, self))
    const html = fence(tokens, index, options, env, self)
    const language = name.toLowerCase()
    if (language === 'math' && markdownDialectActive(md, 'nodes', 'mathBlock')) return `<div data-md-math="block" data-md-expression="${escape(token.content.trim())}">${html}</div>`
    if (language === 'mermaid' && markdownDialectActive(md, 'nodes', 'diagram')) return `<div data-md-fence="mermaid" data-md-source="${escape(token.content.replace(/\n$/, ''))}">${html}</div>`
    return html
  }
}
