import { createDocument, elementFromString, Extension, type Editor, type JSONContent } from '@tiptap/core'
import { DOMParser, Fragment, type Node, type ParseOptions, type ResolvedPos, type Schema, type Slice } from '@tiptap/pm/model'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import type {
  JsonValue, MarkdownEmbedResolver, MarkdownImportDecision, MarkdownImportHandler, MarkdownImportInfo, MarkdownWikiLinkResolver,
} from '../core/types'
import { dispatchMarkdownImportNotice, dispatchMarkdownInputError } from '../core/inputErrors'
import { MAX_MARKDOWN_TEXT_LENGTH } from '../core/documentConstraints'
import { markdownStrings } from '../ui/strings'
import {
  createImportSession, currentImportSession, degradedParts, installImportSyntax, markdownDialectActive, recordDegraded,
  registerMarkdownDialect, withImportSession, whitespace, type MarkdownIt, type MdBlockState, type MdInlineState,
} from './markdownImportSyntax'
import { loadedMarkdownDialect } from './markdownDialectLoader'

/**
 * The one Markdown input boundary shared by paste, `.md` file drop and every
 * other string parsed by tiptap-markdown (value load, insertContent):
 *
 * - CommonMark soft line breaks become spaces on every path, except a paste of plain text (no Markdown in it), whose lines stay.
 * - The editor dialect is parsed on every path where the schema has its node or
 *   mark: `$…$`/`$$…$$` TeX source (kept out of Markdown escape/emphasis
 *   processing), GitHub alerts, `==highlight==` and ```mermaid/```math fences
 *   (markdownImportSyntax.ts). The editor writes these nodes back the same way.
 * - Imports (paste/drop) additionally strip a leading YAML front matter block,
 *   report it to the host, map foreign-wiki syntax and keep the dialect's TeX
 *   and alerts meaningful where the schema lacks their nodes. Otherwise value
 *   loading keeps plain CommonMark semantics, because the serializer escapes
 *   backslashes in text and must round-trip.
 */

interface MarkdownStorage { parser: { parse(content: string, options?: { inline?: boolean }): string } }
interface MarkdownInputStorage { onImport?: MarkdownImportHandler; resolveWikiLink?: MarkdownWikiLinkResolver; resolveEmbed?: MarkdownEmbedResolver }

const installed = new WeakSet<object>()

function markdownStorage(editor: Editor): MarkdownStorage | undefined {
  return (editor.storage as unknown as { markdown?: MarkdownStorage }).markdown
}

// ---------------------------------------------------------------------------
// Front matter
// ---------------------------------------------------------------------------

const FRONT_MATTER_MAX_LENGTH = 64 * 1024
const FRONT_MATTER_MAX_KEYS = 200
const unsafeKeys = new Set(['__proto__', 'constructor', 'prototype'])

export interface MarkdownFrontMatterSplit {
  readonly body: string
  readonly frontMatter?: { readonly [key: string]: JsonValue }
  readonly title?: string
}

function unquote(value: string): string | undefined {
  if (value.length >= 2 && value.startsWith("'") && value.endsWith("'")) return value.slice(1, -1).replace(/''/g, "'")
  if (value.length >= 2 && value.startsWith('"') && value.endsWith('"')) {
    try { const parsed: unknown = JSON.parse(value); return typeof parsed === 'string' ? parsed : undefined }
    catch { return value.slice(1, -1) }
  }
  return undefined
}

function scalar(raw: string): JsonValue {
  const value = raw.trim()
  const quoted = unquote(value)
  if (quoted !== undefined) return quoted
  const plain = value.replace(/\s+#.*$/, '')
  if (/^(?:true|false)$/i.test(plain)) return plain.toLowerCase() === 'true'
  if (/^(?:null|~)?$/i.test(plain)) return null
  if (/^-?(?:0|[1-9]\d{0,14})(?:\.\d+)?$/.test(plain)) return Number(plain)
  return plain
}

/** A title is text even when it looks like a number or boolean (`title: 2024`, `title: 1.10`). */
function textScalar(raw: string): JsonValue {
  const value = raw.trim()
  const text = unquote(value) ?? value.replace(/\s+#.*$/, '')
  return text || null
}

function flowSequence(value: string): JsonValue[] | undefined {
  const inner = value.trim().slice(1, -1)
  if (/[[\]{}]/.test(inner)) return undefined
  const items: string[] = []
  let current = '', quote = ''
  for (const char of inner) {
    if (quote) { current += char; if (char === quote) quote = ''; continue }
    if (char === '"' || char === "'") { quote = char; current += char; continue }
    if (char === ',') { items.push(current); current = ''; continue }
    current += char
  }
  if (current.trim() || items.length) items.push(current)
  return items.map(scalar)
}

/**
 * A deliberately small reader for the flat `key: value` front matter written by
 * GitHub/Jekyll, Obsidian and Wiki.js. Nested maps and anchors are skipped; any
 * top-level line that is not a key means the block is not front matter at all.
 * The `yaml` package is an optional peer used only by the OpenAPI plugin, and
 * the clipboard parser is synchronous, so core cannot load it here.
 */
function parseFrontMatterBlock(block: string): Record<string, JsonValue> | null {
  const data = new Map<string, JsonValue>()
  let key: string | null = null
  let list: JsonValue[] | null = null
  let folded: { lines: string[]; join: string } | null = null
  let skipped = false
  const finish = () => {
    if (key === null) return
    if (folded) data.set(key, folded.lines.join(folded.join).trim())
    else if (list) data.set(key, list)
    else if (skipped) data.delete(key)
    key = null; list = null; folded = null; skipped = false
  }
  for (const line of block.split(/\r?\n/)) {
    if (folded && (/^\s/.test(line) || !line.trim())) { folded.lines.push(line.trim()); continue }
    if (!line.trim() || /^\s*#/.test(line)) continue
    const item = /^\s*-(?:\s+(.*))?$/.exec(line)
    if (item && key !== null && !folded && (list || !data.has(key) || data.get(key) === null)) {
      list ??= []
      list.push(scalar(item[1] ?? ''))
      continue
    }
    if (/^\s/.test(line)) {
      if (key === null) return null
      skipped = true; list = null
      continue
    }
    // Greedy value, trimmed below: a lazy `(.*?)` before `[ \t]*$` backtracks quadratically on long runs of blanks.
    const entry = /^([A-Za-z_][\w.-]*(?: [\w.-]+)*)[ \t]*:(?:[ \t]+(.*))?$/.exec(line)
    if (!entry) return null
    finish()
    if (data.size >= FRONT_MATTER_MAX_KEYS) return null
    const name = entry[1]!, value = (entry[2] ?? '').trimEnd()
    if (unsafeKeys.has(name)) { key = null; continue }
    key = name
    if (/^[|>][-+]?$/.test(value)) { folded = { lines: [], join: value.startsWith('|') ? '\n' : ' ' }; data.set(name, ''); continue }
    if (/^[[].*[\]]$/.test(value)) { const sequence = flowSequence(value); if (sequence) data.set(name, sequence); else skipped = true; continue }
    if (/^[{&*!]/.test(value)) { skipped = true; data.set(name, null); continue }
    data.set(name, name === 'title' ? textScalar(value) : scalar(value))
  }
  finish()
  return data.size ? Object.fromEntries(data) : null
}

/** Splits a leading `---` YAML front matter block from Markdown source. */
export function splitMarkdownFrontMatter(text: string): MarkdownFrontMatterSplit {
  const source = text.charCodeAt(0) === 0xFEFF ? text.slice(1) : text
  const open = /^---[ \t]*\r?\n/.exec(source)
  if (!open) return { body: text }
  const rest = source.slice(open[0].length, open[0].length + FRONT_MATTER_MAX_LENGTH)
  const close = /^(?:---|\.\.\.)[ \t]*(?:\r?\n|$)/m.exec(rest)
  if (!close) return { body: text }
  const frontMatter = parseFrontMatterBlock(rest.slice(0, close.index))
  if (!frontMatter) return { body: text }
  const title = typeof frontMatter.title === 'string' && frontMatter.title.trim() ? frontMatter.title.trim() : undefined
  return { body: source.slice(open[0].length + close.index + close[0].length), frontMatter, ...(title ? { title } : {}) }
}

// ---------------------------------------------------------------------------
// markdown-it rules
// ---------------------------------------------------------------------------

const DOLLAR = 0x24
const LETTER_OR_DIGIT = /[\p{L}\p{N}]/u

/**
 * The last scan per mode (inline, display) that found no closing delimiter. markdown-it tries this rule
 * at every `$`; a later `$` of the same source inside a failed scan's range cannot find a closer either
 * (closers do not depend on the opener), so text with many unmatched `$` stays linear.
 */
const unmatched = [{ src: '', max: 0, from: 0, to: 0 }, { src: '', max: 0, from: 0, to: 0 }]

function mathInline(state: MdInlineState, silent: boolean): boolean {
  if (state.src.charCodeAt(state.pos) !== DOLLAR || !markdownDialectActive(state.md, 'nodes', 'mathInline')) return false
  const { src, pos: start, posMax } = state
  const display = src.charCodeAt(start + 1) === DOLLAR
  const width = display ? 2 : 1
  // Pandoc rule: `$` must hug its content, and a closing `$` never precedes a digit ("$5 and $10"); nor a
  // letter here, so shell variables such as `$HOME/$USER` stay text.
  if (!display && whitespace(src[start + 1])) return false
  const miss = unmatched[width - 1]!
  if (miss.src === src && miss.max === posMax && start > miss.from && start < miss.to) return false
  let pos = start + width
  let end = -1
  while (pos < posMax) {
    const char = src[pos]
    if (char === '\\') { pos += 2; continue }
    if (char === '\n' && !display) break
    if (char === '$' && (display ? src.charCodeAt(pos + 1) === DOLLAR && pos + 1 < posMax
      : !whitespace(src[pos - 1]) && !LETTER_OR_DIGIT.test(src[pos + 1] ?? ''))) { end = pos; break }
    pos++
  }
  if (end < 0) { Object.assign(miss, { src, max: posMax, from: start, to: pos }); return false }
  const content = src.slice(start + width, end)
  if (!content.trim()) return false
  if (!silent) {
    const token = state.push('math_inline', 'span', 0)
    token.content = content
    token.markup = display ? '$$' : '$'
  }
  state.pos = end + width
  return true
}

function mathBlock(state: MdBlockState, startLine: number, endLine: number, silent: boolean): boolean {
  if (!markdownDialectActive(state.md, 'nodes', 'mathBlock')) return false
  const lineText = (line: number) => state.src.slice(state.bMarks[line]! + state.tShift[line]!, state.eMarks[line]!)
  if (state.sCount[startLine]! - state.blkIndent >= 4) return false
  const first = lineText(startLine)
  if (!first.startsWith('$$')) return false
  const opening = first.slice(2).trimEnd()
  let closeLine = startLine
  let content: string
  if (opening.endsWith('$$') && opening.length > 2) {
    content = opening.slice(0, -2)
    if (content.includes('$$')) return false
  } else {
    if (opening.includes('$$')) return false
    const lines = [opening]
    let found = false
    for (let line = startLine + 1; line < endLine; line++) {
      const text = lineText(line)
      // Pandoc: display math cannot contain a blank line, so an unclosed `$$` stays text.
      if (!text.trim()) break
      // A non-empty line outdented past the current container ends the search.
      if (state.sCount[line]! < state.blkIndent) break
      const trimmed = text.trimEnd()
      if (trimmed.endsWith('$$')) { lines.push(trimmed.slice(0, -2)); closeLine = line; found = true; break }
      lines.push(text)
    }
    if (!found) return false
    content = lines.join('\n')
  }
  if (!content.trim()) return false
  if (silent) return true
  const source: string[] = []
  for (let line = startLine; line <= closeLine; line++) source.push(lineText(line).trimEnd())
  state.line = closeLine + 1
  const token = state.push('math_block', 'div', 0)
  token.block = true
  token.content = content
  token.markup = '$$'
  token.map = [startLine, state.line]
  token.meta = source
  return true
}

/** tiptap-markdown runs its parse hooks with the parsing editor as `this`. */
interface MarkdownParseHookContext { readonly editor: Editor }

function installMarkdownInputRules(this: MarkdownParseHookContext, md: MarkdownIt): void {
  // CommonMark: a soft line break is rendered as a space. tiptap-markdown keeps
  // "\n", which ProseMirror's whitespace-preserving paste turns into hardBreak.
  // Pasted plain text (a signature „S pozdravem\nPetr“) keeps its lines.
  md.renderer.rules.softbreak = (_tokens, _index, options) => options.breaks || currentImportSession()?.lineBreaks ? '<br>' : ' '
  registerMarkdownDialect(md, this.editor.schema)
  if (!installed.has(md)) installInputSyntax(md)
  // After the rules the dialect's own are ordered against.
  loadedMarkdownDialect()?.installMarkdownDialectSyntax(md)
}

function installInputSyntax(md: MarkdownIt): void {
  installed.add(md)
  const escape = (value: string) => md.utils.escapeHtml(value)
  // The wrapper carries the TeX source for a math node's parse rule; without
  // one, the literal source (delimiters included) is imported as text.
  md.inline.ruler.before('escape', 'math_inline', mathInline)
  md.block.ruler.before('fence', 'math_block', mathBlock, { alt: ['paragraph', 'reference', 'blockquote', 'list'] })
  // Without the math plugin the literal source stays in the document; report it.
  const reportLiteral = (node: string, literal: string) => {
    const session = currentImportSession()
    if (session && !session.nodes.has(node)) recordDegraded(session, 'math', literal)
  }
  md.renderer.rules.math_inline = (tokens, index) => {
    const token = tokens[index]!
    const literal = `${token.markup}${token.content}${token.markup}`.replace(/\s*\r?\n\s*/g, ' ')
    reportLiteral('mathInline', literal)
    return `<span data-md-math="inline" data-md-expression="${escape(token.content.trim())}">${escape(literal)}</span>`
  }
  md.renderer.rules.math_block = (tokens, index) => {
    const token = tokens[index]!
    const source = Array.isArray(token.meta) ? token.meta as string[] : [`$$${token.content}$$`]
    reportLiteral('mathBlock', source.join(' '))
    return `<div data-md-math="block" data-md-expression="${escape(token.content.trim())}"><p>${source.map(escape).join('<br>')}</p></div>`
  }
  // markdown-it's alternative text leaves escaped characters out (`![a\_b](…)` read as "ab"): they are text like any other.
  const image = md.renderer.rules.image!
  md.renderer.rules.image = (tokens, index, options, env, self) => {
    for (const child of tokens[index]!.children ?? []) if (child.type === 'text_special') child.type = 'text'
    return image(tokens, index, options, env, self)
  }
  installImportSyntax(md)
}

// ---------------------------------------------------------------------------
// Import entry points
// ---------------------------------------------------------------------------

const inlineTags = new Set(['A', 'ABBR', 'B', 'BR', 'CODE', 'DEL', 'EM', 'I', 'KBD', 'MARK', 'S', 'SPAN', 'STRONG', 'SUB', 'SUP', 'U'])
const isBlockBoundary = (node: ChildNode | null) => !node || (node.nodeType === 1 && !inlineTags.has((node as Element).tagName))

/**
 * The paste parser preserves whitespace, so renderer newlines next to block
 * tags (tight list items, task items, images extracted from paragraphs) would
 * become hardBreak nodes or stray spaces. Trim text at block edges; top-level
 * text of an inline paste keeps its spaces, and code stays literal.
 */
function normalizeImportWhitespace(element: HTMLElement): void {
  const walker = element.ownerDocument.createTreeWalker(element, 4 /* NodeFilter.SHOW_TEXT */)
  const texts: Text[] = []
  while (walker.nextNode()) texts.push(walker.currentNode as Text)
  for (const text of texts) {
    const parent = text.parentElement
    if (!parent || parent.closest('pre, code')) continue
    let value = text.data.replace(/[ \t]*\r?\n[ \t]*/g, ' ')
    // The edges of the whole inline paste are the user's; block siblings are not.
    const topLevel = parent === element
    if (!inlineTags.has(parent.tagName)) {
      if (isBlockBoundary(text.previousSibling) && !(topLevel && !text.previousSibling)) value = value.replace(/^\s+/, '')
      if (isBlockBoundary(text.nextSibling) && !(topLevel && !text.nextSibling)) value = value.replace(/\s+$/, '')
    }
    if (value) text.data = value
    else text.remove()
  }
  element.querySelectorAll('p').forEach(paragraph => { if (!paragraph.firstChild) paragraph.remove() })
}

function leadingHeading(element: HTMLElement): HTMLElement | null {
  for (const child of element.childNodes) {
    if (child.nodeType === 3 && !child.textContent!.trim()) continue
    return child.nodeType === 1 && (child as HTMLElement).tagName === 'H1' ? child as HTMLElement : null
  }
  return null
}

export interface MarkdownImportContext {
  readonly source: MarkdownImportInfo['source']
  readonly fileName?: string
  /** Paste into running text (tiptap-markdown inline normalization). */
  readonly inline?: boolean
  /** Plain text, not Markdown source: each line break stays a hard break. */
  readonly lineBreaks?: boolean
}

function inputStorage(editor: Editor): MarkdownInputStorage | undefined {
  return (editor.storage as unknown as { markdownInput?: MarkdownInputStorage }).markdownInput
}

function notifyDegraded(editor: Editor, detail: Parameters<typeof dispatchMarkdownImportNotice>[1]): void {
  let dom: HTMLElement | undefined
  try { dom = editor.view.dom } catch { return }
  if (typeof dom?.dispatchEvent === 'function') dispatchMarkdownImportNotice(dom, detail)
}

// A history line „- 30. 10. 2026 — text“: CommonMark reads "30." and "10." as two nested ordered lists.
const LEADING_DATE = /^([ \t]*(?:[-*+][ \t]+)?)(\d{1,2})\.([ \t]+\d{1,2}\.[ \t]+\d{4})/
const FENCE = /^[ \t]*(`{3,}|~{3,})/

/** A Czech date starting a line or a list item outside fenced code with its day escaped, as the writer stores it (MarkdownToRich.EscapeLeadingDates). */
function escapeLeadingDates(markdown: string): string {
  let fence: string | undefined
  return markdown.split('\n').map(line => {
    const open = FENCE.exec(line)?.[1]
    if (open && (!fence || open.startsWith(fence))) {
      fence = fence ? undefined : open
      return line
    }
    return fence ? line : line.replace(LEADING_DATE, '$1$2\\.$3')
  }).join('\n')
}

/** Parses imported Markdown into the DOM that paste/drop hand to ProseMirror. */
export function parseMarkdownImport(editor: Editor, text: string, context: MarkdownImportContext): HTMLElement {
  const markdown = markdownStorage(editor)
  if (!markdown) throw new Error('Editor has no Markdown parser.')
  const { body, frontMatter, title } = splitMarkdownFrontMatter(text)
  const storage = inputStorage(editor)
  const session = createImportSession(editor.schema, { source: context.source, resolveWikiLink: storage?.resolveWikiLink, resolveEmbed: storage?.resolveEmbed, lineBreaks: context.lineBreaks })
  const html = withImportSession(session, () => markdown.parser.parse(escapeLeadingDates(body), { inline: context.inline }))
  const element = elementFromString(html)
  const heading = leadingHeading(element)
  const degraded = degradedParts(session)
  const onImport = storage?.onImport
  if (onImport && (frontMatter || heading || degraded.length)) {
    let decision: MarkdownImportDecision | void = undefined
    try {
      decision = onImport(Object.freeze({
        source: context.source,
        ...(context.fileName ? { fileName: context.fileName } : {}),
        ...(title ? { title } : {}),
        ...(frontMatter ? { frontMatter: Object.freeze(frontMatter) } : {}),
        ...(heading ? { leadingHeading: heading.textContent?.trim() ?? '' } : {}),
        ...(degraded.length ? { degraded } : {}),
      }))
    } catch (error) {
      // A host callback must not break the user's paste or drop.
      console.error(error)
    }
    if (decision?.omitLeadingHeading && heading) heading.remove()
  }
  normalizeImportWhitespace(element)
  if (degraded.length) notifyDegraded(editor, { source: context.source, ...(context.fileName ? { fileName: context.fileName } : {}), degraded })
  return element
}

// Table section wrappers are emitted by Markdown/browser table parsing but are
// not document nodes; math, fence and dialect link wrappers fall back to their
// literal, code block or text content. Tiptap's strict unknown-element check otherwise rejects
// even a valid GFM table. Keep strict checking for all other elements.
export function parseInputDocument(content: string | JSONContent | HTMLElement, schema: Schema, options?: ParseOptions): Node {
  if (typeof content !== 'string' && !isElement(content)) return createDocument(content, schema, {}, { errorOnInvalidContent: true })
  const parser = new DOMParser(schema, [
    ...DOMParser.fromSchema(schema).rules,
    { tag: 'table > thead, table > tbody, table > tfoot', skip: true },
    { tag: '[data-md-math]', skip: true },
    { tag: '[data-md-fence]', skip: true },
    { tag: '[data-md-link]', skip: true },
    { tag: '*', getAttrs() { throw new Error('[tiptap error]: Invalid HTML content') } },
  ])
  const element = typeof content === 'string' ? elementFromString(content) : content
  // With whitespace kept, the line feeds markdown-it puts between tags (`<li>text\n<ul>`) would read as line breaks; they are not content.
  if (options?.preserveWhitespace) {
    const texts = element.ownerDocument.createTreeWalker(element, 4 /* NodeFilter.SHOW_TEXT */)
    for (let text = texts.nextNode(); text; text = texts.nextNode()) {
      if (text.nodeValue!.includes('\n') && !text.parentElement?.closest('pre')) text.nodeValue = text.nodeValue!.replace(/\n/g, '')
    }
  }
  const document = parser.parse(element, options)
  return options?.preserveWhitespace ? trimBlockEdges(document) : document
}

/**
 * Whitespace is kept inside a line only: a block's text starts and ends without spaces, as the collapsing reader
 * leaves it (the space after a task item's checkbox, the spaces around an image lifted out of its paragraph).
 */
function trimBlockEdges(node: Node): Node {
  if (node.isLeaf || node.type.spec.code) return node
  let content = node.content
  if (!node.isTextblock) {
    const children: Node[] = []
    node.forEach(child => { children.push(trimBlockEdges(child)) })
    return children.every((child, index) => child === node.child(index)) ? node : node.copy(Fragment.from(children))
  }
  for (const [edge, blanks] of [['firstChild', /^[ \t]+/], ['lastChild', /[ \t]+$/]] as const) {
    for (let child = content[edge]; child?.isText && blanks.test(child.text!); child = content[edge]) {
      const text = child.text!.replace(blanks, '')
      const index = edge === 'firstChild' ? 0 : content.childCount - 1
      content = text ? content.replaceChild(index, node.type.schema.text(text, child.marks))
        : edge === 'firstChild' ? content.cut(child.nodeSize) : content.cut(0, content.size - child.nodeSize)
    }
  }
  return content === node.content ? node : node.copy(content)
}

function isElement(value: unknown): value is HTMLElement {
  return !!value && typeof value === 'object' && (value as { nodeType?: unknown }).nodeType === 1
}

/** Parses a dropped Markdown file into top-level document content. */
export function importMarkdownFile(editor: Editor, text: string, fileName: string): JSONContent[] {
  const element = parseMarkdownImport(editor, text, { source: 'file', fileName })
  return parseInputDocument(element, editor.schema).toJSON().content ?? []
}

function markdownSlice(editor: Editor, text: string, context: ResolvedPos, lineBreaks = false): Slice {
  const element = parseMarkdownImport(editor, text, { source: 'paste', inline: true, lineBreaks })
  return DOMParser.fromSchema(editor.schema).parseSlice(element, { preserveWhitespace: true, context })
}

// ---------------------------------------------------------------------------
// Code-editor copies and the explicit "paste as Markdown" command
// ---------------------------------------------------------------------------

const MONOSPACE = /monospace|consolas|menlo|monaco|courier|jetbrains mono|fira code|cascadia|source code pro|sf mono/i
const MARKDOWN_HINT = /^(?:#{1,6}[ \t]|>|[-*+][ \t]|\d{1,9}[.)][ \t]|```|~~~|\|.*\|[ \t]*$|---[ \t]*$)|\[[^\]\n]+\]\([^)\s]+\)|\*\*[^*\n]+\*\*|\[\[[^\]\n]+\]\]|`[^`\n]+`|\\$/m
const RICH_STRUCTURE = 'img, table, a[href], ul, ol, h1, h2, h3, h4, h5, h6, blockquote, hr, strong, b, em, i'

/**
 * VS Code (`vscode-editor-data`), JetBrains IDEs and other code editors put a syntax-coloured
 * HTML rendering of the plain text on the clipboard. Parsing that HTML inserts the Markdown
 * source as coloured paragraphs; the plain text is the real content. Rendered pages (a browser
 * copy of a wiki page) have real structure in their HTML and keep the HTML paste.
 */
export function isCodeEditorCopy(doc: Document, data: Pick<DataTransfer, 'getData'> & { readonly types: ArrayLike<string> }, text: string, html: string): boolean {
  if (Array.from(data.types ?? []).includes('vscode-editor-data')) {
    let mode: unknown
    try { mode = (JSON.parse(data.getData('vscode-editor-data')) as { mode?: unknown } | null)?.mode } catch { /* unreadable metadata */ }
    if (mode === 'markdown') return true
    return (mode === undefined || mode === null || mode === 'plaintext') && MARKDOWN_HINT.test(text)
  }
  if (!MONOSPACE.test(html) && !/<pre[\s>]/i.test(html)) return false
  // An inert template: nothing is loaded or executed while inspecting the markup.
  const template = doc.createElement('template')
  template.innerHTML = html
  template.content.querySelectorAll('style, script, meta, title, link').forEach(element => element.remove())
  if (template.content.querySelector(RICH_STRUCTURE)) return false
  const flat = (value: string) => value.replace(/\s+/g, '')
  return flat(template.content.textContent ?? '') === flat(text) && MARKDOWN_HINT.test(text)
}

/**
 * Ctrl+Shift+Alt+V (Cmd+Shift+Option+V): Ctrl/Cmd+Shift+V stays the literal plain-text paste. AltGr is
 * reported as Ctrl+Alt on Windows; AltGr+Shift+V types a character on many layouts and is not ours.
 */
export function isPasteMarkdownShortcut(event: KeyboardEvent): boolean {
  const altGraph = typeof event.getModifierState === 'function' && event.getModifierState('AltGraph')
  return ((event.ctrlKey && !altGraph) || event.metaKey) && event.altKey && event.shiftKey && (event.code === 'KeyV' || event.key?.toLowerCase() === 'v')
}

/** The "paste as Markdown" shortcut as the user's platform writes it (⌘/⌥ on Apple devices, iPadOS included). */
export function pasteMarkdownShortcutLabel(view: Window | null | undefined = globalThis.window): string {
  return /Mac|iPhone|iPad|iPod/.test(view?.navigator?.userAgent ?? '') ? '⌥⇧⌘V' : 'Ctrl+Shift+Alt+V'
}

/** Reads the clipboard text and inserts it through the Markdown paste boundary. */
export async function pasteMarkdownFromClipboard(editor: Editor): Promise<boolean> {
  if (editor.isDestroyed) return false
  const dom = editor.view.dom
  let text: string
  try {
    const clipboard = dom.ownerDocument.defaultView?.navigator.clipboard
    if (!clipboard?.readText) throw new Error('Clipboard text is not available.')
    text = await clipboard.readText()
  } catch {
    if (!editor.isDestroyed) dispatchMarkdownInputError(dom, { source: 'paste', message: markdownStrings.clipboardReadFailed })
    return false
  }
  // Permission may change while the browser asks for clipboard access.
  if (editor.isDestroyed || !editor.isEditable || !text) return false
  return editor.commands.pasteMarkdown(text)
}

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    markdownInput: {
      /** Inserts Markdown text at the selection through the paste import boundary, as one undo step. */
      pasteMarkdown: (text: string) => ReturnType
    }
  }
}

export interface MarkdownInputOptions {
  onImport?: MarkdownImportHandler
  resolveWikiLink?: MarkdownWikiLinkResolver
  resolveEmbed?: MarkdownEmbedResolver
}

export const MarkdownInput = Extension.create<MarkdownInputOptions>({
  name: 'markdownInput',
  addOptions: () => ({ onImport: undefined, resolveWikiLink: undefined, resolveEmbed: undefined }),
  addStorage() {
    return {
      onImport: this.options.onImport,
      resolveWikiLink: this.options.resolveWikiLink,
      resolveEmbed: this.options.resolveEmbed,
      // tiptap-markdown runs these hooks for every string it parses.
      markdown: { parse: { setup: installMarkdownInputRules } },
    }
  },
  addCommands() {
    return {
      pasteMarkdown: text => ({ tr, dispatch, editor }) => {
        if (typeof text !== 'string' || !text || text.length > MAX_MARKDOWN_TEXT_LENGTH || !editor.isEditable || !markdownStorage(editor)) return false
        if (!dispatch) return true
        const { $from } = tr.selection
        // Like a native paste, a code block receives the source literally.
        if ($from.parent.type.spec.code) tr.insertText(text.replace(/\r\n?/g, '\n'))
        else {
          let slice: Slice
          try { slice = markdownSlice(editor, text, $from) }
          catch {
            // Structure the document cannot hold: say so and leave the document untouched.
            dispatchMarkdownInputError(editor.view.dom, { source: 'paste', message: markdownStrings.markdownImportFailed })
            return false
          }
          tr.replaceSelection(slice)
        }
        // Paste metadata: the paste guard validates it and closes one undo step.
        tr.scrollIntoView().setMeta('paste', true).setMeta('uiEvent', 'paste')
        return true
      },
    }
  },
  addProseMirrorPlugins() {
    const editor = this.editor
    // ProseMirror's own Shift+paste detection (its input state is internal).
    let shift = false
    let lastKey = ''
    let forceMarkdown = false
    return [new Plugin({
      key: new PluginKey('markdownInput'),
      props: {
        // Replaces tiptap-markdown's clipboard parser (transformPastedText is off).
        // Code blocks never reach this; ProseMirror pastes into them literally.
        clipboardTextParser(text, context, plainText) {
          // Above the input limit the text is pasted literally, like a plain-text paste.
          if ((plainText && !forceMarkdown) || text.length > MAX_MARKDOWN_TEXT_LENGTH || !markdownStorage(editor)) return null as unknown as Slice
          // Text without a sign of Markdown is plain text (an e-mail, a chat): its line breaks stay; Markdown source (a code editor's copy,
          // „paste as Markdown“, text with Markdown in it) wraps as CommonMark.
          return markdownSlice(editor, text, context, !forceMarkdown && !MARKDOWN_HINT.test(text))
        },
        handleKeyDown(_view, event) {
          if (!isPasteMarkdownShortcut(event) || !editor.isEditable || !markdownStorage(editor)) return false
          event.preventDefault()
          void pasteMarkdownFromClipboard(editor)
          return true
        },
        handleDOMEvents: {
          keydown(_view, event) { shift = event.shiftKey; lastKey = event.key; return false },
          keyup(_view, event) { if (event.key === 'Shift') shift = false; return false },
          paste(view, event) {
            const data = event.clipboardData
            // Shift+paste (not Shift+Insert) stays ProseMirror's literal plain-text paste.
            if (!data || view.composing || (shift && lastKey !== 'Insert') || !editor.isEditable || !markdownStorage(editor)) return false
            if (view.state.selection.$from.parent.type.spec.code || data.files?.length) return false
            const text = data.getData('text/plain'), html = data.getData('text/html')
            if (!text || !html || !isCodeEditorCopy(view.dom.ownerDocument, data, text, html)) return false
            event.preventDefault()
            // ProseMirror's text paste path keeps the paste guards, selection and undo semantics. The
            // original event stays behind: Tiptap's codeBlock turns every `vscode-editor-data` paste
            // into one code block, which is right for source code but not for Markdown.
            forceMarkdown = true
            try { view.pasteText(text) } finally { forceMarkdown = false }
            return true
          },
        },
      },
    })]
  },
})
