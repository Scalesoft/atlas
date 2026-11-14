import type { Node as PMNode } from '@tiptap/pm/model'
import { inputStrings } from '../ui/strings/input'
import { chartSourceRectangle, tableById } from '../core/tableMarkdown'
import { MARKDOWN_DIALECT_ID, serializedMarkdownDocument } from './markdownDialectLoader'
import { schemaHas, type MarkdownIt, type MdCoreState, type MdInlineState, type MdToken } from './markdownImportSyntax'
import '../ui/strings/inputDialect'

/**
 * The `a-` part of the editor's Markdown dialect (Atlas dialect spec §2): block fences ```` ```a-<name> ```` with an
 * a-YAML body and inline links `[label](a-<name>:<path>?<query>)`. Only `a-contact`, `a-chart`, `a-overview` and `a-table` (core tables) exist; any other
 * `a-` name is unknown. Plugins own their data: they write and read these forms through the helpers below and validate
 * with their own normalizers. Reading has two modes: lenient (every parse; an unknown or invalid construct stays a code
 * block or literal text) and strict (`parseMarkdownDialect`; the same construct is an error with its position).
 * This module loads on demand (./markdownDialectLoader), with its markdown-it rules and serializer parts at the end.
 */

// `a-overview` is Atlas's own overview block (the host's `overview` node, outside this package): the host's parse rule reads its `kind`.
/** Block fences of the dialect and the node each becomes. */
export const MARKDOWN_DIALECT_FENCES: Readonly<Record<string, string>> = Object.freeze({ 'a-contact': 'contactCard', 'a-chart': 'chart', 'a-overview': 'overview', 'a-table': 'table' })
/** Inline link schemes of the dialect and the node each becomes. */
export const MARKDOWN_DIALECT_LINKS: Readonly<Record<string, string>> = Object.freeze({ 'a-contact': 'contactInline' })
/** A name in the reserved `a-` namespace; `a-code <language>` is a code block whose language starts with `a-`. */
export const MARKDOWN_DIALECT_NAME = /^a-[a-z][a-z0-9-]*$/

// ---------------------------------------------------------------------------
// Diagnostics
// ---------------------------------------------------------------------------

export interface MarkdownDialectIssue {
  /** 1-based line and column of the construct's start in the parsed Markdown. */
  readonly line: number
  readonly column: number
  /** The construct, e.g. `a-chart`. */
  readonly construct: string
  readonly message: string
}
interface DialectSession { readonly strict: boolean; readonly errors: MarkdownDialectIssue[]; readonly warnings: MarkdownDialectIssue[] }
let session: DialectSession | null = null

/** Runs one synchronous parse collecting dialect diagnostics; outside it nothing is collected. */
export function withMarkdownDialectSession<T>(strict: boolean, run: () => T): { result: T; errors: MarkdownDialectIssue[]; warnings: MarkdownDialectIssue[] } {
  const previous = session
  const current: DialectSession = { strict, errors: [], warnings: [] }
  session = current
  try { return { result: run(), errors: current.errors, warnings: current.warnings } } finally { session = previous }
}

/**
 * `invalid`: an unknown or invalid component (an error in strict mode, a warning in lenient mode). `error`: a construct its
 * reader refuses in every mode (a heading's attribute block with an unknown state), kept as text.
 */
export function reportMarkdownDialectIssue(kind: 'invalid' | 'warning' | 'error', issue: MarkdownDialectIssue): void {
  if (!session) return
  ;(kind === 'error' || (kind === 'invalid' && session.strict) ? session.errors : session.warnings).push(Object.freeze({ ...issue }))
}

function elementIssue(element: Element, message: string): MarkdownDialectIssue | null {
  const construct = element.getAttribute('data-md-fence') ?? element.getAttribute('data-md-link')
  if (!construct) return null
  return { line: Number(element.getAttribute('data-md-line')) || 1, column: Number(element.getAttribute('data-md-column')) || 1, construct, message }
}
/** A dialect element (fence or link) whose data failed validation; it stays a code block or literal text. */
export function reportMarkdownDialectFailure(element: Element, error: unknown): void {
  const issue = elementIssue(element, error instanceof Error ? error.message : String(error))
  if (issue) reportMarkdownDialectIssue('invalid', { ...issue, message: inputStrings.dialectInvalidComponent(issue.construct, issue.message) })
}
/** A warning about a dialect element that is otherwise read, e.g. a chart grid overridden by its linked table. */
export function reportMarkdownDialectWarning(element: Element, message: string): void {
  const issue = elementIssue(element, message)
  if (issue) reportMarkdownDialectIssue('warning', issue)
}

// ---------------------------------------------------------------------------
// Fences and links
// ---------------------------------------------------------------------------

/** A backtick fence longer than any backtick run in the body (§2.2). */
export function markdownDialectFence(info: string, body: string): string {
  const ticks = '`'.repeat(Math.max(3, ...(body.match(/`+/g) ?? []).map(run => run.length + 1)))
  return `${ticks}${info}\n${body}\n${ticks}`
}

// IRI encoding (§2.3): spaces, controls, `%&=#?()<>\` and quotes are percent-encoded; `|` too, so a link inside a
// GFM table cell keeps its target. Other characters, non-ASCII letters included, stay readable.
const ENCODED = /[\s\p{Cc}%&=#?()<>\\"'|]/gu
const encode = (value: string) => value.replace(ENCODED, char => [...new TextEncoder().encode(char)].map(byte => `%${byte.toString(16).toUpperCase().padStart(2, '0')}`).join(''))

/** `a-<name>:<path>?k=v&…`; a repeated key is one list item each. */
export function writeMarkdownDialectLink(name: string, path: string, params: readonly (readonly [string, string])[]): string {
  return `${name}:${encode(path)}${params.length ? `?${params.map(([key, value]) => `${encode(key)}=${encode(value)}`).join('&')}` : ''}`
}
/** The link label: the reader ignores it, so only brackets, backslashes and line breaks need care. */
export const markdownDialectLabel = (text: string) => text.replace(/\s+/g, ' ').replace(/[\\[\]]/g, '\\$&')

export interface MarkdownDialectLink { name: string; path: string; params: [string, string][] }
export function parseMarkdownDialectLink(target: string): MarkdownDialectLink {
  const match = /^(a-[a-z][a-z0-9-]*):([^?]*)(?:\?(.*))?$/s.exec(target)
  if (!match) throw new TypeError(inputStrings.dialectInvalidLink)
  try {
    const params = (match[3] ? match[3].split('&') : []).map(part => {
      const equals = part.indexOf('=')
      if (equals < 1) throw new TypeError(inputStrings.dialectInvalidLink)
      return [decodeURIComponent(part.slice(0, equals)), decodeURIComponent(part.slice(equals + 1))] as [string, string]
    })
    return { name: match[1]!, path: decodeURIComponent(match[2]!), params }
  } catch { throw new TypeError(inputStrings.dialectInvalidLink) }
}

// ---------------------------------------------------------------------------
// a-YAML (§2.2): a strict subset of YAML 1.2 (core schema)
// ---------------------------------------------------------------------------

export type MarkdownDialectYaml = null | boolean | number | string | MarkdownDialectYaml[] | { [key: string]: MarkdownDialectYaml }
const MAX_DEPTH = 4
const fail = (line: number, message: string): never => { throw new TypeError(inputStrings.dialectYamlAt(line, message)) }

/** Core schema resolution of a plain scalar. */
function resolvePlain(text: string): MarkdownDialectYaml {
  if (/^(?:null|Null|NULL|~)?$/.test(text)) return null
  if (/^(?:true|True|TRUE)$/.test(text)) return true
  if (/^(?:false|False|FALSE)$/.test(text)) return false
  if (/^0o[0-7]+$/.test(text)) return parseInt(text.slice(2), 8)
  if (/^0x[0-9a-fA-F]+$/.test(text)) return parseInt(text.slice(2), 16)
  if (/^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?$/.test(text)) return Number(text)
  if (/^[-+]?\.(?:inf|Inf|INF)$/.test(text)) return text.startsWith('-') ? -Infinity : Infinity
  if (/^\.(?:nan|NaN|NAN)$/.test(text)) return NaN
  return text
}

interface Line { index: number; indent: number; text: string }
/** Keys and item indexes from the top of the body to a value, e.g. `[0, 'phones', 1]`. */
export type MarkdownDialectYamlPath = readonly (string | number)[]
/** The string fields of an `a-chart` header: `title` and `source.table`. */
export const isChartHeaderText = (path: MarkdownDialectYamlPath) => ['title', 'source.table'].includes(path.join('.'))
const SEQUENCE_ITEM = /^-(?: |$)/
const MAP_ENTRY = /^([A-Za-z_][A-Za-z0-9_.-]*)[ ]*:(?: +(.*))?$/

class YamlReader {
  private next = 0
  constructor(private readonly lines: readonly string[], private readonly text: (path: MarkdownDialectYamlPath) => boolean) {}

  /** The next line with content; blank and comment lines are skipped (outside literal blocks). */
  private peek(): Line | null {
    for (let index = this.next; index < this.lines.length; index++) {
      const raw = this.lines[index]!
      if (!raw.trim() || /^\s*#/.test(raw)) continue
      if (/^ *\t/.test(raw)) fail(index + 1, inputStrings.dialectYamlIndentation)
      const indent = raw.length - raw.trimStart().length
      return { index, indent, text: raw.slice(indent).trimEnd() }
    }
    return null
  }

  document(): MarkdownDialectYaml {
    const first = this.peek()
    if (!first) return null
    const value = this.block(first.indent, 1, [])
    const rest = this.peek()
    if (rest) fail(rest.index + 1, inputStrings.dialectYamlIndentation)
    return value
  }

  private block(indent: number, depth: number, path: MarkdownDialectYamlPath): MarkdownDialectYaml {
    const line = this.peek()!
    if (depth > MAX_DEPTH) fail(line.index + 1, inputStrings.dialectYamlTooDeep)
    return SEQUENCE_ITEM.test(line.text) ? this.sequence(indent, depth, path) : this.map(indent, depth, path)
  }

  /** The value of a key or item with nothing after it: a nested block, a compact sequence at the same indent, or null. */
  private nested(indent: number, depth: number, sequenceAtSameIndent: boolean, path: MarkdownDialectYamlPath): MarkdownDialectYaml {
    const line = this.peek()
    if (line && (line.indent > indent || (sequenceAtSameIndent && line.indent === indent && SEQUENCE_ITEM.test(line.text)))) return this.block(line.indent, depth, path)
    return null
  }

  private sequence(indent: number, depth: number, path: MarkdownDialectYamlPath): MarkdownDialectYaml[] {
    const items: MarkdownDialectYaml[] = []
    for (let line = this.peek(); line && line.indent === indent && SEQUENCE_ITEM.test(line.text); line = this.peek()) {
      this.next = line.index + 1
      const rest = line.text.slice(1).trimStart()
      const column = indent + line.text.length - rest.length
      const item = [...path, items.length]
      if (!rest) items.push(this.nested(indent, depth + 1, false, item))
      else if (SEQUENCE_ITEM.test(rest)) fail(line.index + 1, inputStrings.dialectYamlUnsupported)
      else if (MAP_ENTRY.test(rest) && !/^["']/.test(rest)) {
        if (depth + 1 > MAX_DEPTH) fail(line.index + 1, inputStrings.dialectYamlTooDeep)
        items.push(this.map(column, depth + 1, item, { index: line.index, indent: column, text: rest }))
      } else items.push(this.scalar(rest, line.index, indent, depth + 1, item))
    }
    const after = this.peek()
    if (after && after.indent > indent) fail(after.index + 1, inputStrings.dialectYamlIndentation)
    return items
  }

  private map(indent: number, depth: number, path: MarkdownDialectYamlPath, first?: Line): { [key: string]: MarkdownDialectYaml } {
    const entries = new Map<string, MarkdownDialectYaml>()
    for (let line = first ?? this.peek(); line && line.indent === indent && !SEQUENCE_ITEM.test(line.text); line = this.peek()) {
      this.next = line.index + 1
      const match = MAP_ENTRY.exec(line.text)
      if (!match) fail(line.index + 1, inputStrings.dialectYamlUnsupported)
      const key = match![1]!
      if (entries.has(key)) fail(line.index + 1, inputStrings.dialectYamlDuplicateKey(key))
      const value = match![2]?.trim()
      entries.set(key, value ? this.scalar(value, line.index, indent, depth + 1, [...path, key]) : this.nested(indent, depth + 1, true, [...path, key]))
    }
    const after = this.peek()
    if (after && after.indent > indent) fail(after.index + 1, inputStrings.dialectYamlIndentation)
    return Object.fromEntries(entries)
  }

  private scalar(text: string, index: number, indent: number, depth: number, path: MarkdownDialectYamlPath): MarkdownDialectYaml {
    const line = index + 1
    if (/^[&*!{>%@`]/.test(text)) fail(line, inputStrings.dialectYamlUnsupported)
    const literal = /^\|([+-]?)[ ]*(?:#.*)?$/.exec(text)
    if (literal) return this.literal(indent, literal[1] as '' | '+' | '-')
    if (text.startsWith('[')) {
      if (depth > MAX_DEPTH) fail(line, inputStrings.dialectYamlTooDeep)
      const close = text.lastIndexOf(']')
      if (close < 0 || !/^\s*(?:#.*)?$/.test(text.slice(close + 1))) fail(line, inputStrings.dialectYamlUnsupported)
      return this.flow(text.slice(1, close), line, path)
    }
    return this.single(text, line, false, path)
  }

  /** A one-line scalar: double-quoted (JSON escapes), single-quoted, or plain (core schema, or its exact text where the component reads a string). */
  private single(text: string, line: number, flow: boolean, path: MarkdownDialectYamlPath): MarkdownDialectYaml {
    if (text.startsWith('"')) {
      const end = /^"(?:[^"\\]|\\.)*"/.exec(text)
      if (!end || !/^\s*(?:#.*)?$/.test(text.slice(end[0].length))) return fail(line, inputStrings.dialectYamlUnsupported)
      try { return JSON.parse(end[0]) as string } catch { return fail(line, inputStrings.dialectYamlUnsupported) }
    }
    if (text.startsWith("'")) {
      const end = /^'(?:[^']|'')*'/.exec(text)
      if (!end || !/^\s*(?:#.*)?$/.test(text.slice(end[0].length))) return fail(line, inputStrings.dialectYamlUnsupported)
      return end[0].slice(1, -1).replace(/''/g, "'")
    }
    const plain = text.replace(/\s+#.*$/, '').trim()
    if (/^[&*!{}[\]|>%@`,?]/.test(plain) || /:(?: |$)/.test(plain) || (flow && /[[\]{}]/.test(plain))) fail(line, inputStrings.dialectYamlUnsupported)
    return this.text(path) ? plain : resolvePlain(plain)
  }

  /** A short flow sequence of scalars: `[a, "b, c"]`. */
  private flow(inner: string, line: number, path: MarkdownDialectYamlPath): MarkdownDialectYaml[] {
    const items: string[] = []
    let current = '', quote = ''
    for (let index = 0; index < inner.length; index++) {
      const char = inner[index]!
      if (quote) {
        current += char
        if (quote === '"' && char === '\\') current += inner[++index] ?? ''
        else if (char === quote) quote = ''
        continue
      }
      if (char === '"' || char === "'") quote = char
      if (char === ',') { items.push(current); current = ''; continue }
      current += char
    }
    if (quote) fail(line, inputStrings.dialectYamlUnsupported)
    if (current.trim() || items.length) items.push(current)
    return items.map((item, index) => {
      if (!item.trim()) fail(line, inputStrings.dialectYamlUnsupported)
      return this.single(item.trim(), line, true, [...path, index])
    })
  }

  /** `|`, `|-`, `|+`: lines indented past the key, blank lines included. */
  private literal(indent: number, chomp: '' | '+' | '-'): string {
    const content: string[] = []
    let blockIndent = -1
    while (this.next < this.lines.length) {
      const raw = this.lines[this.next]!
      if (!raw.trim()) { content.push(''); this.next++; continue }
      const own = raw.length - raw.trimStart().length
      if (blockIndent < 0) {
        if (own <= indent) break
        if (/^ *\t/.test(raw)) fail(this.next + 1, inputStrings.dialectYamlIndentation)
        blockIndent = own
      }
      if (own < blockIndent) break
      content.push(raw.slice(blockIndent)); this.next++
    }
    let trailing = 0
    while (content.length && content[content.length - 1] === '') { content.pop(); trailing++ }
    // Blank lines after the block belong to the next construct, not to this scalar.
    if (chomp !== '+') this.next -= trailing
    const text = content.join('\n')
    if (chomp === '-') return text
    if (chomp === '+') return `${text}${content.length ? '\n' : ''}${'\n'.repeat(trailing)}`
    return content.length ? `${text}\n` : ''
  }
}

/**
 * Parses an a-YAML body; anchors, aliases, tags, flow maps, merge keys, folded blocks and several documents are errors.
 * Typing is schema-directed: a plain scalar at a path where `text` says the component reads a string is that string,
 * exactly as written (`+420777123456`, `2026`, `1.50`, `true`); everywhere else the YAML core schema applies.
 */
export function parseMarkdownDialectYaml(source: string, text: (path: MarkdownDialectYamlPath) => boolean = () => false): MarkdownDialectYaml {
  return new YamlReader(source.replace(/\r\n?/g, '\n').split('\n'), text).document()
}

const isScalar = (value: unknown): value is null | boolean | number | string => value === null || ['boolean', 'number', 'string'].includes(typeof value)
const literalSafe = (value: string) => value.includes('\n') && !/[\x00-\x08\x0b-\x1f\x7f]/.test(value) && !/^[ \n]/.test(value) && !/(?:^|\n)[ \t]+(?:\n|$)/.test(value)

/** A string reads back as itself when written plain; otherwise it gets JSON double quotes. A leading `+` is quoted too. */
function scalarText(value: null | boolean | number | string, flow: boolean): string {
  if (typeof value !== 'string') return value === null ? 'null' : String(value)
  const plain = value !== '' && resolvePlain(value) === value && value.trim() === value
    && !/^[-?:,[\]{}#&*!|>'"%@\x60+]/.test(value) && !/:(?: |$)| #|[\p{Cc}\p{Zl}\p{Zp}]/u.test(value)
    && !(flow && /[,[\]{}]/.test(value))
  return plain ? value : JSON.stringify(value)
}

function literalLines(value: string, pad: string): string[] {
  const trailing = /\n*$/.exec(value)![0].length
  const header = trailing === 0 ? '|-' : trailing === 1 ? '|' : '|+'
  const body = trailing === 0 ? value : value.slice(0, -1)
  return [header, ...body.split('\n').map(line => line ? `${pad}${line}` : '')]
}

function writeEntry(prefix: string, value: MarkdownDialectYaml, indent: number): string[] {
  const pad = ' '.repeat(indent + 2)
  if (typeof value === 'string' && literalSafe(value)) {
    const [header, ...rest] = literalLines(value, pad)
    return [`${prefix} ${header}`, ...rest]
  }
  if (isScalar(value)) return [`${prefix} ${scalarText(value, false)}`]
  if (Array.isArray(value) && value.every(item => isScalar(item) && !(typeof item === 'string' && item.includes('\n')))) {
    return [`${prefix} [${value.map(item => scalarText(item as string, true)).join(', ')}]`]
  }
  return [prefix, ...writeNode(value, indent + 2)]
}

function writeNode(value: MarkdownDialectYaml, indent: number): string[] {
  const pad = ' '.repeat(indent)
  if (Array.isArray(value)) {
    return value.flatMap(item => {
      if (item && typeof item === 'object' && !Array.isArray(item)) {
        const lines = writeNode(item, indent + 2)
        return lines.length ? [`${pad}- ${lines[0]!.slice(indent + 2)}`, ...lines.slice(1)] : [`${pad}- null`]
      }
      return writeEntry(`${pad}-`, item, indent)
    })
  }
  if (value && typeof value === 'object') {
    return Object.entries(value).flatMap(([key, item]) => item === undefined ? [] : writeEntry(`${pad}${key}:`, item, indent))
  }
  return [`${pad}${scalarText(value, false)}`]
}

/** a-YAML for a map or sequence, keys in the given order; `undefined` values are skipped. No trailing newline. */
export function writeMarkdownDialectYaml(value: MarkdownDialectYaml): string {
  return writeNode(value, 0).join('\n')
}

// ---------------------------------------------------------------------------
// markdown-it: ```a-<name> fences and [label](a-<name>:…) links (installed into each parser once the dialect loads)
// ---------------------------------------------------------------------------

/** The first word of a fence's info string. */
const fenceName = (token: MdToken) => token.info.trim().split(/\s+/)[0] ?? ''
let cellIds = 0
const newCellId = () => `chart-${globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${++cellIds}-${Math.random().toString(36).slice(2)}`}`

/**
 * A table a chart links to carries its id on a `{#id}` paragraph right after it (§4.5). Only an id some ```a-chart
 * fence of the same Markdown names is read; any other `{#…}` paragraph stays text. The table keeps the id and its
 * cells get fresh ids, which the chart's parse rule reads back from the DOM.
 */
function linkChartTables(state: MdCoreState): void {
  if (!schemaHas(state.md, 'nodes', 'chart')) return
  const { tokens } = state
  const linked = new Set<string>()
  for (const token of tokens) {
    if (token.type !== 'fence' || fenceName(token) !== 'a-chart') continue
    const separator = /^---$/m.exec(token.content)
    try {
      const header = parseMarkdownDialectYaml(separator ? token.content.slice(0, separator.index) : '', isChartHeaderText) as { source?: { table?: unknown } } | null
      if (typeof header?.source?.table === 'string') linked.add(header.source.table)
    } catch { /* the chart's own parse rule reports it */ }
  }
  if (!linked.size) return
  for (let close = 0; close < tokens.length; close++) {
    if (tokens[close]!.type !== 'table_close' || tokens[close + 1]?.type !== 'paragraph_open' || tokens[close + 3]?.type !== 'paragraph_close') continue
    const id = /^\{#([^\s{}]+)\}$/.exec(tokens[close + 2]?.content ?? '')?.[1]
    if (!id || !MARKDOWN_DIALECT_ID.test(id) || !linked.delete(id)) continue
    tokens.splice(close + 1, 3)
    let open = close
    while (open > 0 && !(tokens[open]!.type === 'table_open' && tokens[open]!.level === tokens[close]!.level)) open--
    tokens[open]!.attrSet('data-chart-ref', id)
    for (let index = open; index < close; index++) {
      if (tokens[index]!.type === 'th_open' || tokens[index]!.type === 'td_open') tokens[index]!.attrSet('data-chart-ref', newCellId())
    }
  }
}

/** The next `](a-` at or after a position, remembered per source so text full of `[` stays linear. */
const nextDialectLink = { src: '', from: 0, at: -1 }
const LINK_TARGET = /^(a-[a-z][a-z0-9-]*):([^()\s]*)\)/

function dialectLink(state: MdInlineState, silent: boolean): boolean {
  const { src, pos, posMax } = state
  if (src.charCodeAt(pos) !== 0x5B /* [ */) return false
  if (nextDialectLink.src !== src || pos < nextDialectLink.from || (nextDialectLink.at >= 0 && pos > nextDialectLink.at)) {
    Object.assign(nextDialectLink, { src, from: pos, at: src.indexOf('](a-', pos) })
  }
  if (nextDialectLink.at < 0 || nextDialectLink.at >= posMax) return false
  const labelEnd = state.md.helpers.parseLinkLabel(state, pos, true)
  if (labelEnd < 0 || src.slice(labelEnd, labelEnd + 4) !== '](a-') return false
  const target = LINK_TARGET.exec(src.slice(labelEnd + 2, Math.min(posMax, labelEnd + 2 + 16_384)))
  if (!target) return false
  const end = labelEnd + 2 + target[0].length
  if (!silent) state.push('md_dialect_link', 'span', 0).meta = { name: target[1], target: `${target[1]}:${target[2]}`, literal: src.slice(pos, end), offset: pos }
  state.pos = end
  return true
}

/** 1-based line and column of dialect constructs, for diagnostics; runs after every other core rule. */
function dialectPositions(state: MdCoreState): void {
  let lines: string[] | undefined
  const sourceLine = (index: number) => (lines ??= state.src.split('\n'))[index] ?? ''
  let lastMap: [number, number] | null = null
  for (const token of state.tokens) {
    if (token.map) lastMap = token.map
    if (token.type === 'fence' && fenceName(token).startsWith('a-') && token.map) {
      token.meta = { line: token.map[0] + 1, column: Math.max(0, sourceLine(token.map[0]).indexOf(token.markup)) + 1 }
    }
    if (token.type !== 'inline' || !token.children?.some(child => child.type === 'md_dialect_link')) continue
    const first = (token.map ?? lastMap)?.[0] ?? 0
    const content = token.content.split('\n')
    for (const child of token.children) {
      if (child.type !== 'md_dialect_link') continue
      const meta = child.meta as { offset: number }
      const before = token.content.slice(0, meta.offset).split('\n')
      const index = before.length - 1
      const start = sourceLine(first + index).indexOf(content[index]!.trimEnd())
      Object.assign(meta, { line: first + index + 1, column: Math.max(0, start) + before[index]!.length + 1 })
    }
  }
}

const position = (token: MdToken) => token.meta as { line?: number; column?: number } | null
function reportUnknown(token: MdToken, construct: string): void {
  reportMarkdownDialectIssue('invalid', { line: position(token)?.line ?? 1, column: position(token)?.column ?? 1, construct, message: inputStrings.dialectUnknownComponent(construct) })
}

const installed = new WeakSet<object>()
/** Adds the component rules to an editor's parser at the positions they had among the import rules. */
export function installMarkdownDialectSyntax(md: MarkdownIt): void {
  if (installed.has(md)) return
  installed.add(md)
  md.core.ruler.before('import_alerts', 'dialect_chart_tables', linkChartTables)
  md.core.ruler.push('dialect_positions', dialectPositions)
  md.inline.ruler.before('import_footnote_reference', 'dialect_link', dialectLink)
  // A known component the schema has is a span its node's parse rule claims (data failing validation leaves the
  // literal text); anything else in the `a-` namespace is literal text.
  md.renderer.rules.md_dialect_link = (tokens, index) => {
    const token = tokens[index]!
    const { name, target, literal } = token.meta as { name: string; target: string; literal: string }
    const node = MARKDOWN_DIALECT_LINKS[name]
    const escape = md.utils.escapeHtml
    if (!node || !schemaHas(md, 'nodes', node)) { reportUnknown(token, name); return escape(literal) }
    return `<span data-md-link="${name}" data-md-target="${escape(target)}" data-md-line="${position(token)?.line ?? 1}" data-md-column="${position(token)?.column ?? 1}">${escape(literal)}</span>`
  }
}

/**
 * An ```a-<name> fence that `render` writes as a code block: a known component the schema has is wrapped with its
 * source for the node's parse rule; ```a-code <language> is a code block in that language (the `a-` namespace is
 * reserved for components).
 */
export function renderDialectFence(md: MarkdownIt, token: MdToken, render: () => string): string {
  const name = fenceName(token)
  if (name === 'a-code') {
    token.info = token.info.trim().slice('a-code'.length).trim()
    return render()
  }
  const node = MARKDOWN_DIALECT_FENCES[name]
  if (!node || !MARKDOWN_DIALECT_NAME.test(name) || !schemaHas(md, 'nodes', node)) { const html = render(); reportUnknown(token, name); return html }
  const escape = md.utils.escapeHtml, line = position(token)?.line ?? 1, column = position(token)?.column ?? 1
  const source = token.content.replace(/\n$/, '')
  if (name === 'a-table') {
    try { return tableFenceHtml(md, source) } catch (error) {
      reportMarkdownDialectIssue('invalid', { line, column, construct: name, message: inputStrings.dialectInvalidComponent(name, error instanceof Error ? error.message : String(error)) })
      return render()
    }
  }
  // One contact per block: a fence listing several people becomes one block each, as the server reads it (MarkdownToRich).
  const parts = name === 'a-contact' ? sequenceItems(source) : [{ text: source, line: 0 }]
  return parts.map(part => {
    if (parts.length > 1) token.content = `${part.text}\n`
    return `<div data-md-fence="${name}" data-md-source="${escape(part.text)}" data-md-line="${line + part.line}" data-md-column="${column}">${render()}</div>`
  }).join('')
}

/** The entries of a map without nulls; anything else, or a key outside `keys`, is an error (MarkdownDialect.cs `Known`). */
function knownEntries(value: MarkdownDialectYaml, keys: readonly string[]): Record<string, MarkdownDialectYaml> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError(inputStrings.dialectInvalidShape)
  const unknownKey = Object.keys(value).find(key => !keys.includes(key))
  if (unknownKey) throw new TypeError(inputStrings.dialectUnknownKey(unknownKey))
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null))
}

/**
 * A ```a-table (§4.12) as the HTML the table nodes parse: a row per item, each cell its blocks rendered from its Markdown.
 * MarkdownDialect.cs `ReadTable` checks the same; the document validator bounds the table (1000 cells, spans included).
 */
function tableFenceHtml(md: MarkdownIt, source: string): string {
  const rows = parseMarkdownDialectYaml(source, path => path[path.length - 1] === 'content')
  if (!Array.isArray(rows) || !rows.length) throw new TypeError(inputStrings.dialectInvalidShape)
  return `<table><tbody>${rows.map(row => {
    const { cells } = knownEntries(row, ['cells'])
    if (!Array.isArray(cells)) throw new TypeError(inputStrings.dialectInvalidShape)
    return `<tr>${cells.map(item => {
      const cell = knownEntries(item, ['header', 'colspan', 'rowspan', 'align', 'content'])
      const span = (key: string) => {
        const value = cell[key]
        if (value === undefined) return ''
        if (!Number.isInteger(value) || (value as number) < 1 || (value as number) > 1000) throw new TypeError(inputStrings.dialectInvalidShape)
        return ` ${key}="${value}"`
      }
      const attributes = span('colspan') + span('rowspan')
      if ((cell.align !== undefined && !['left', 'center', 'right'].includes(cell.align as string))
        || (cell.header !== undefined && typeof cell.header !== 'boolean') || (cell.content !== undefined && typeof cell.content !== 'string')) {
        throw new TypeError(inputStrings.dialectInvalidShape)
      }
      const tag = cell.header ? 'th' : 'td'
      return `<${tag}${attributes}${cell.align ? ` style="text-align:${cell.align as string}"` : ''}>${md.render((cell.content as string | undefined) ?? '')}</${tag}>`
    }).join('')}</tr>`
  }).join('')}</tbody></table>`
}

/**
 * The top-level items of a YAML block sequence, each with its lines up to the next item (the first also with what precedes
 * it, e.g. a comment) and the offset of its `-` line from the fence line, where its errors point. One part (offset 0, the
 * fence) when there are fewer than two items.
 * Items are the `-` lines at the first item's indentation (`  - name: A` is a root sequence too), as the server splits them
 * (ContactCardSplit.SequenceItems); a body whose first line of content is no item (a map) stays one part.
 * shortcut: only block-style items are split; a flow list `[…]` stays one block, which the validators accept.
 */
export function sequenceItems(source: string): { text: string; line: number }[] {
  const lines = source.split('\n')
  const indent = /^( *)-(\s|$)/.exec(lines.find(text => text.trim() && !/^\s*#/.test(text)) ?? '')?.[1]
  const item = indent === undefined ? undefined : new RegExp(`^ {${indent.length}}-(\\s|$)`)
  const starts = item ? lines.flatMap((text, index) => (item.test(text) ? [index] : [])) : []
  if (starts.length < 2) return [{ text: source, line: 0 }]
  return starts.map((start, index) => {
    const from = index ? start : 0
    return { text: lines.slice(from, starts[index + 1] ?? lines.length).join('\n').replace(/\n+$/, ''), line: start + 1 }
  })
}

// ---------------------------------------------------------------------------
// Serializer parts: the `{#id}` of a table a chart links to (§4.5)
// ---------------------------------------------------------------------------

/** Chart sources (`data.source`) in the document that the dialect writes with a link to `table`. */
function chartLinksTable(document: PMNode, table: PMNode): boolean {
  const id = table.attrs.chartRef as unknown
  if (typeof id !== 'string' || tableById(document, id) !== table) return false
  let linked = false
  document.descendants(node => {
    const source = (node.attrs.data as { source?: { tableId?: unknown; cells?: unknown } } | undefined)?.source
    if (!linked && node.type.name === 'chart' && source?.tableId === id && Array.isArray(source.cells)
      && chartSourceRectangle(document, source as { tableId: string; cells: string[][] })) linked = true
  })
  return linked
}

/** After a GFM table: the `{#id}` paragraph when a chart of the document being written links to it. */
export function writeChartTableId(state: { write(content: string): void; closeBlock(node: PMNode): void }, table: PMNode): void {
  const document = serializedMarkdownDocument()
  if (document && chartLinksTable(document, table)) {
    state.write(`{#${table.attrs.chartRef as string}}`)
    state.closeBlock(table)
  }
}

/** Whether a paragraph's text reads as the id a chart of the document being written names, so it is written escaped. */
export function namesChartTable(paragraph: PMNode): boolean {
  const id = paragraph.childCount === 1 && paragraph.firstChild!.isText && !paragraph.firstChild!.marks.length ? /^\{#([^\s{}]+)\}$/.exec(paragraph.textContent)?.[1] : undefined
  let found = false
  if (id) serializedMarkdownDocument()?.descendants(node => {
    if (!found && node.type.name === 'chart' && (node.attrs.data as { source?: { tableId?: unknown } } | undefined)?.source?.tableId === id) found = true
  })
  return found
}
