import { getText, getTextSerializersFromSchema, type Editor, type JSONContent } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import type { MarkdownPlugin } from '../core/plugins/types'
import type { MarkdownRichValue } from '../core/types'
import { RICH_FORMAT } from '../core/types'
import { clonePlainData, deepFreezePlainData } from '../core/plainData'
import { MAX_MARKDOWN_TEXT_LENGTH } from '../core/documentConstraints'
import { MarkdownDocumentCodec, readMarkdownLiterally } from '../engine/documentCodec'
import { loadedMarkdownDialect, loadMarkdownDialect } from '../engine/markdownDialectLoader'
import { createTiptapEditor } from '../engine/editorFactory'
import { destroyEditor, initializationFailure } from '../engine/editorLifecycle'
import { isDefaultImagePresentation } from '../features/images/presentation'
import { sourceStrings } from '../ui/strings/source'
import type { SourceDiagnostic } from './index'
import { PROTECTED_CLOSE, PROTECTED_ID_PATTERN, PROTECTED_LABEL_PREFIX, PROTECTED_OPEN, protectedFenceLines, sameProtectedLine } from './protectedFence'

/**
 * Headless Markdown source session (docs/page-source-editor.md, phase 2).
 *
 * The rich document is shown as Markdown; every top-level block that cannot pass
 * through Markdown without loss is replaced by a protected fence:
 *
 *     ```md-protected <id>
 *     Chráněný blok: Graf „Tržby 2025“
 *     …the block's text as the Markdown writer writes it, to read and search…
 *     ```
 *
 * `apply` swaps the fences back for the original nodes, aligns the edited
 * Markdown blocks with the original ones and reuses the original JSON of every
 * untouched block, so an unedited text returns exactly the original value.
 *
 * What Markdown cannot say but its text still identifies is carried instead of protected (`restore`):
 * an image keeps its presentation while its address stays, a page link with the same address and
 * title comes back as the page reference. A heading that holds more than its text says names itself,
 * `## Title {#id …}` (the server's form; a requirement's state is the Atlas plugin's part of it), so
 * it is itself wherever it is moved: its aliases come back by that id, an id the page does not have or
 * a second heading with the same id is an error. The text is read literally: runs of spaces stay and a bare
 * address is text, not a link. Lists of one kind that follow each other are kept apart by a
 * `<!-- -->` line, which Markdown would otherwise read as one list.
 */

export type MarkdownSourceProtectionReason =
  /** `builtinMarkdownLossy` or a plugin `markdownLossy` check on the block alone. */
  | 'lossy'
  /** Serializing and parsing the block alone does not reproduce it. */
  | 'round-trip'
  /** An existing code block whose language starts with `md-protected` (reserved fence syntax). */
  | 'reserved-code'
  /** The block survives alone but not in the whole text (e.g. adjacent lists merge). */
  | 'merge'

export interface MarkdownSourceBlock {
  readonly id: string
  /** The exact label line of the fence. */
  readonly label: string
  /** The block's text shown under the label: the writer's (lossy) Markdown of the node. A view; `apply` never reads it. */
  readonly text: string
  /** Node type name, e.g. `chart` or `paragraph`. */
  readonly kind: string
  readonly reason: MarkdownSourceProtectionReason
  /** Deep-frozen original node JSON. */
  readonly node: Readonly<JSONContent>
}

export interface MarkdownSourceProtectedRange {
  readonly id: string
  /** 1-based line of the opening fence in `text`. */
  readonly startLine: number
  /** 1-based line of the closing fence in `text`. */
  readonly endLine: number
  readonly label: string
  /** The block's text between the label and the closing fence. */
  readonly text: string
}

/**
 * A problem found by `apply` (1-based line in the applied text, column when known). The same shape the
 * source editor shows, so it passes straight to `SourceEditor.setDiagnostics`; the session reports only
 * `error` and `warning`.
 */
export type MarkdownSourceDiagnostic = SourceDiagnostic

export type MarkdownSourceApplyResult =
  | { readonly ok: true; readonly value: MarkdownRichValue; readonly changed: boolean; readonly diagnostics: readonly MarkdownSourceDiagnostic[] }
  | { readonly ok: false; readonly diagnostics: readonly MarkdownSourceDiagnostic[] }

export interface MarkdownSourceSessionOptions {
  readonly value: MarkdownRichValue
  /** The same content plugins the document is edited with. */
  readonly plugins?: readonly MarkdownPlugin[]
}

export interface MarkdownSourceSession {
  readonly text: string
  readonly blocks: ReadonlyMap<string, MarkdownSourceBlock>
  readonly protectedBlocks: readonly MarkdownSourceProtectedRange[]
  apply(text: string): MarkdownSourceApplyResult
  destroy(): void
}

const MAX_LABEL_LENGTH = 80
const FENCE_OPEN = /^([ \t]*)(`{3,}|~{3,})(.*)$/
const FENCE_ID_MENTION = new RegExp(`md-protected[ \\t]+(${PROTECTED_ID_PATTERN})`, 'gi')
/** The line between two blocks Markdown would read as one (two lists of a kind): an empty HTML comment, as Markdown writers use. */
const BLOCK_SEPARATOR = '<!-- -->'
const LISTS = new Set(['bulletList', 'orderedList', 'taskList'])

// Core and built-in plugin nodes; any other content module (`defineMarkdownBlockModule`) brings its own
// label in the node's storage. Getters so a host's setMarkdownStrings (which runs after this module's
// imports) is still honored.
const KIND_LABELS: Readonly<Record<string, string>> = {
  get paragraph() { return sourceStrings.kindParagraph },
  get heading() { return sourceStrings.kindHeading },
  get bulletList() { return sourceStrings.kindBulletList },
  get orderedList() { return sourceStrings.kindOrderedList },
  get taskList() { return sourceStrings.kindTaskList },
  get blockquote() { return sourceStrings.kindBlockquote },
  get codeBlock() { return sourceStrings.kindCodeBlock },
  get table() { return sourceStrings.kindTable },
  get image() { return sourceStrings.kindImage },
  get horizontalRule() { return sourceStrings.kindHorizontalRule },
  get callout() { return sourceStrings.kindCallout },
  get chart() { return sourceStrings.kindChart },
  get contactCard() { return sourceStrings.kindContactCard },
  get attachment() { return sourceStrings.kindAttachment },
  get openapi() { return sourceStrings.kindOpenapi },
  get mathBlock() { return sourceStrings.kindMathBlock },
  get diagram() { return sourceStrings.kindDiagram },
  get tableOfContents() { return sourceStrings.kindTableOfContents },
}

type JsonRecord = Record<string, unknown>
const isRecord = (value: unknown): value is JsonRecord => !!value && typeof value === 'object' && !Array.isArray(value)

/** Structural JSON equality, independent of key order and prototypes. */
function sameJson(left: unknown, right: unknown): boolean {
  if (left === right) return true
  if (Array.isArray(left)) return Array.isArray(right) && left.length === right.length && left.every((item, index) => sameJson(item, right[index]))
  if (!isRecord(left) || !isRecord(right) || Array.isArray(right)) return false
  const keys = Object.keys(left).filter(key => left[key] !== undefined)
  if (keys.length !== Object.keys(right).filter(key => right[key] !== undefined).length) return false
  return keys.every(key => Object.hasOwn(right, key) && sameJson(left[key], right[key]))
}

/**
 * Round-trip comparison ignores heading identities (carried separately), list tightness, a default
 * image presentation (absent from stored JSON, filled in when Markdown is parsed) and a code block's
 * empty language (the same as none).
 */
function comparable(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(comparable)
  if (!isRecord(value)) return value
  const result: JsonRecord = {}
  for (const [key, item] of Object.entries(value)) {
    if (key === 'attrs' && isRecord(item)) {
      const { headingId: _headingId, tight: _tight, ...attrs } = item
      if (value.type === 'image' && isDefaultImagePresentation(attrs.presentation)) delete attrs.presentation
      if (value.type === 'codeBlock' && !attrs.language) delete attrs.language
      result.attrs = comparable(attrs)
    } else result[key] = comparable(item)
  }
  return result
}

/** A page link by its address and text; Markdown reads an address back percent-encoded, so both sides are compared decoded. */
function pageKey(url: unknown, title: unknown): string {
  let address = String(url ?? '')
  try { address = decodeURI(address) } catch { /* not an encoded address: compared as written */ }
  return `${address}\n${String(title ?? '')}`
}

function randomId(used: ReadonlySet<string>): string {
  const alphabet = '0123456789abcdefghijklmnopqrstuvwxyz'
  for (let length = 4; ; length++) {
    for (let attempt = 0; attempt < 32; attempt++) {
      const bytes = new Uint8Array(length)
      globalThis.crypto.getRandomValues(bytes)
      const id = `b${[...bytes].map(byte => alphabet[byte % alphabet.length]).join('')}`
      if (!used.has(id)) return id
    }
  }
}

function sanitizeLabelText(value: string): string {
  return value.replace(/`/g, '\'').replace(/[\u0000-\u001f\u007f]+/g, ' ').replace(/\s+/g, ' ').trim()
}

function formatLabel(kind: string, title: string): string {
  const prefix = `${PROTECTED_LABEL_PREFIX}${sanitizeLabelText(kind)}`
  const clean = sanitizeLabelText(title)
  if (!clean) return prefix.slice(0, MAX_LABEL_LENGTH)
  const room = MAX_LABEL_LENGTH - prefix.length - 3
  const fitted = clean.length <= room ? clean : `${clean.slice(0, Math.max(1, room - 1)).trimEnd()}…`
  return `${prefix} „${fitted}“`
}

/** Whether a heading's Markdown already names it, `## Title {#id …}` (RequirementHeadingMarkdown on the server; the id may be any token of the block). */
const namesHeading = (markdown: string, id: string) => markdown.endsWith('}') && (markdown.includes(`#${id} `) || markdown.includes(`#${id}}`))
const HEADING_LINE = /^#{1,6}(?:[ \t]|$)/

export interface SessionItem {
  readonly kind: 'protected' | 'markdown'
  /** A heading the text names: its id was read from its line (`{#id}`), which is what it is known by wherever it stands. */
  named?: boolean
  /** A top-level heading's 1-based line in the text, for diagnostics. */
  readonly line?: number
  /** The same as an offset in its parsed segment (cached with it). */
  readonly offset?: number
  /** Protected fence id. */
  readonly id?: string
  /** Canonical Markdown of an editable block. */
  readonly key?: string
  readonly json: JSONContent
}
interface OriginalItem extends SessionItem {
  readonly index: number
  /** Empty paragraphs that precede this block; Markdown cannot represent them. */
  readonly blanks: readonly JSONContent[]
  /** Reads as one block with a neighbour of its type unless a separator line stands between them. */
  readonly separate?: boolean
}
interface ScannedText {
  readonly items: SessionItem[]
  readonly errors: MarkdownSourceDiagnostic[]
  readonly warnings: MarkdownSourceDiagnostic[]
}

/**
 * Beyond this many LCS cells (16 MiB of table) only the common prefix and suffix are aligned: the
 * blocks in between count as replaced, which still applies correctly but reuses less original JSON.
 */
export const MAX_ALIGNMENT_CELLS = 4 * 1024 * 1024

/** Longest common subsequence of item pairs, with common prefix/suffix trimmed first. */
export function alignItems(left: readonly SessionItem[], right: readonly SessionItem[]): Array<[number, number]> {
  const equal = (a: SessionItem, b: SessionItem) => a.kind === b.kind && (a.kind === 'protected' ? a.id === b.id : a.key === b.key)
  let start = 0
  while (start < left.length && start < right.length && equal(left[start]!, right[start]!)) start++
  let leftEnd = left.length, rightEnd = right.length
  while (leftEnd > start && rightEnd > start && equal(left[leftEnd - 1]!, right[rightEnd - 1]!)) { leftEnd--; rightEnd-- }
  const pairs: Array<[number, number]> = []
  for (let index = 0; index < start; index++) pairs.push([index, index])
  const rows = leftEnd - start, columns = rightEnd - start
  if (rows && columns && rows * columns <= MAX_ALIGNMENT_CELLS) {
    const table = Array.from({ length: rows + 1 }, () => new Uint32Array(columns + 1))
    for (let i = rows - 1; i >= 0; i--) {
      for (let j = columns - 1; j >= 0; j--) {
        table[i]![j] = equal(left[start + i]!, right[start + j]!) ? table[i + 1]![j + 1]! + 1 : Math.max(table[i + 1]![j]!, table[i]![j + 1]!)
      }
    }
    let i = 0, j = 0
    while (i < rows && j < columns) {
      if (equal(left[start + i]!, right[start + j]!)) { pairs.push([start + i, start + j]); i++; j++ }
      else if (table[i + 1]![j]! >= table[i]![j + 1]!) i++
      else j++
    }
  }
  for (let offset = 0; offset < left.length - leftEnd; offset++) pairs.push([leftEnd + offset, rightEnd + offset])
  return pairs
}

class SourceSession implements MarkdownSourceSession {
  text = ''
  protectedBlocks: readonly MarkdownSourceProtectedRange[] = []
  private readonly protectedMap = new Map<string, MarkdownSourceBlock>()
  private readonly original: MarkdownRichValue
  private readonly normalized: MarkdownRichValue
  private readonly registry: MarkdownPluginRegistry
  private readonly codec: MarkdownDocumentCodec<'rich'>
  private editor: Editor | null = null
  private items: OriginalItem[] = []
  private trailingBlanks: JSONContent[] = []
  private segmentCache = new Map<string, readonly SessionItem[]>()
  /** Carried image presentations by address, and page references by address and title (see `restore`). */
  private readonly presentations = new Map<string, unknown>()
  private readonly pageReferences = new Map<string, unknown>()
  /** The top-level headings' attributes by id, and the ids the stored page has twice (not this session's to judge). */
  private readonly headings = new Map<string, Record<string, unknown>>()
  private readonly repeatedIds = new Set<string>()
  /** What a heading read from plain `## Title` holds. */
  private plainHeading: Record<string, unknown> = {}
  /** Headings an apply would strip of their name: errors of the last `merge`. */
  private stripped: MarkdownSourceDiagnostic[] = []
  private destroyed = false

  constructor(options: MarkdownSourceSessionOptions) {
    if (!isRecord(options)) throw new TypeError('Source session options are required.')
    this.original = clonePlainData(options.value)
    this.registry = new MarkdownPluginRegistry(options.plugins ?? [], 'rich')
    try {
      this.codec = new MarkdownDocumentCodec('rich', this.registry, { profile: 'rich' })
      this.normalized = this.codec.normalize(this.original)
      this.editor = createTiptapEditor({ element: null, editable: false, extensions: this.codec.extensions,
        content: { type: 'doc', content: [{ type: 'paragraph' }] } })
      readMarkdownLiterally(this.editor)
      this.analyze()
    } catch (error) {
      const errors = destroyEditor(this.editor)
      this.editor = null
      try { this.registry.destroy() } catch (cleanupError) { errors.push(cleanupError) }
      throw initializationFailure(error, errors)
    }
  }

  get blocks(): ReadonlyMap<string, MarkdownSourceBlock> { return this.protectedMap }

  apply(text: string): MarkdownSourceApplyResult {
    if (this.destroyed) throw new Error('Source session is destroyed.')
    if (typeof text !== 'string') throw new TypeError('Source text must be a string.')
    if (text.length > MAX_MARKDOWN_TEXT_LENGTH) {
      return { ok: false, diagnostics: [{ line: 1, message: sourceStrings.sourceTooLarge, severity: 'error' }] }
    }
    const scanned = this.scan(text)
    // A heading named in the text is a heading of the page, and the only one of that name.
    const named = scanned.items.filter(item => item.named)
    for (const item of named) {
      const id = item.json.attrs!.headingId as string
      if (!this.headings.has(id)) scanned.errors.push({ line: item.line ?? 1, message: sourceStrings.unknownHeadingId(id), severity: 'error' })
    }
    if (scanned.errors.length) return { ok: false, diagnostics: this.sorted([...scanned.errors, ...scanned.warnings]) }
    const content = this.merge(scanned.items)
    if (this.stripped.length) return { ok: false, diagnostics: this.sorted([...this.stripped, ...scanned.warnings]) }
    const owners = new Map<unknown, number>()
    for (const node of content) if (node.type === 'heading' && node.attrs?.headingId != null) owners.set(node.attrs.headingId, (owners.get(node.attrs.headingId) ?? 0) + 1)
    const twice = named.filter(item => owners.get(item.json.attrs!.headingId)! > 1 && !this.repeatedIds.has(item.json.attrs!.headingId as string))
    if (twice.length) {
      const last = new Map(twice.map(item => [item.json.attrs!.headingId as string, item.line ?? 1]))
      return { ok: false, diagnostics: this.sorted([...scanned.warnings, ...[...last].map(([id, line]) => ({ line, message: sourceStrings.duplicateHeadingId(id), severity: 'error' as const }))]) }
    }
    const originalContent = (this.normalized.document.content ?? []) as unknown[]
    if (sameJson(content, originalContent)) {
      return { ok: true, value: structuredClone(this.original), changed: false, diagnostics: this.sorted(scanned.warnings) }
    }
    const schemaPlugins = this.registry.schemaPlugins
    const value = structuredClone({
      format: RICH_FORMAT, version: 1,
      document: { ...this.normalized.document, content },
      ...(schemaPlugins.length ? { plugins: schemaPlugins.map(plugin => ({ id: plugin.id, version: plugin.version })) } : {}),
    }) as MarkdownRichValue
    // The codec's strict rich input boundary: plugin metadata, schema, content and builtin constraints.
    try { this.codec.parseRichDocument(value) }
    catch (error) {
      return { ok: false, diagnostics: this.sorted([...scanned.warnings, {
        line: 1, message: sourceStrings.invalidResultDocument(error instanceof Error ? error.message : String(error)), severity: 'error',
      }]) }
    }
    return { ok: true, value, changed: true, diagnostics: this.sorted(scanned.warnings) }
  }

  destroy(): void {
    if (this.destroyed) return
    this.destroyed = true
    this.segmentCache.clear()
    const errors = destroyEditor(this.editor)
    this.editor = null
    try { this.registry.destroy() } catch (error) { errors.push(error) }
    if (errors.length) throw new AggregateError(errors, 'Source session cleanup failed.')
  }

  // -------------------------------------------------------------------------
  // Construction
  // -------------------------------------------------------------------------

  private analyze(): void {
    const editor = this.editor!
    const document = this.codec.parseRichDocument(this.normalized)
    const json = (this.normalized.document.content ?? []) as JSONContent[]
    const textSerializers = getTextSerializersFromSchema(editor.schema)
    const usedIds = new Set<string>()
    const pending: Array<{ index: number; node: Node; key?: string; comparable?: unknown; reason?: MarkdownSourceProtectionReason; blanks: JSONContent[]; separate?: boolean }> = []
    let blanks: JSONContent[] = []

    // Only what one text stands for on the whole page is carried: an address shown in two sizes, or a page link that is
    // a reference in one place and a plain link in another, is left as Markdown reads it (the block that loses by it is protected).
    const ambiguous = new Set<string>()
    // `null` is the plain form (a default presentation, an ordinary link), which needs no carrying.
    const carry = (pool: Map<string, unknown>, key: string, value: unknown) => {
      if (pool.has(key) && !sameJson(pool.get(key), value)) ambiguous.add(key)
      else pool.set(key, value)
    }
    document.descendants(node => {
      if (node.type.name === 'image') carry(this.presentations, String(node.attrs.src), isDefaultImagePresentation(node.attrs.presentation) ? null : node.attrs.presentation)
      if (node.type.name === 'pageReference') carry(this.pageReferences, pageKey(node.attrs.url, node.attrs.title), { type: node.type.name, attrs: { ...node.attrs } })
      const link = node.isText ? node.marks.find(mark => mark.type.name === 'link') : undefined
      if (link) carry(this.pageReferences, pageKey(link.attrs.href, node.text), null)
    })
    for (const pool of [this.presentations, this.pageReferences]) {
      for (const [key, value] of pool) if (value === null || ambiguous.has(key)) pool.delete(key)
    }
    document.forEach(node => {
      const id = node.type.name === 'heading' ? node.attrs.headingId as unknown : null
      if (typeof id !== 'string') return
      if (this.headings.has(id)) this.repeatedIds.add(id)
      this.headings.set(id, { ...node.attrs })
    })
    const heading = this.codec.schema.nodes.heading
    if (heading) this.plainHeading = { ...heading.create({ level: 1 }).attrs }
    document.forEach((node, _offset, index) => {
      const reason = this.protectionReason(node)
      if (!reason && node.type.name === 'paragraph' && node.childCount === 0) { blanks.push(json[index]!); return }
      pending.push({ index, node, key: reason ? undefined : this.key(node), reason, blanks })
      blanks = []
    })
    this.trailingBlanks = blanks
    // Never pick an id that already occurs in editable text (e.g. a code sample about fences).
    for (const entry of pending) for (const match of entry.key?.matchAll(FENCE_ID_MENTION) ?? []) usedIds.add(match[1]!)

    const protect =(entry: typeof pending[number], reason: MarkdownSourceProtectionReason) => {
      const id = randomId(usedIds)
      usedIds.add(id)
      entry.reason = reason
      let text = entry.key
      try { text ??= this.key(entry.node) } catch { text = '' }
      this.protectedMap.set(id, deepFreezePlainData({ id, label: this.label(entry.node, textSerializers), text, kind: entry.node.type.name, reason,
        node: deepFreezePlainData(clonePlainData(json[entry.index]!)) }))
      return id
    }
    const ids = new Map<number, string>()
    for (const entry of pending) if (entry.reason) ids.set(entry.index, protect(entry, entry.reason))

    // Actual round trip, checked on the whole text: a block that does not come back as itself
    // (ignoring heading ids and list tightness) is protected. The block alone decides the reason:
    // failing alone is 'round-trip', failing only next to its neighbours (adjacent lists merge) is 'merge'.
    // Parsing the whole text once instead of every block alone keeps large pages fast.
    // A block whose text reads back as the same block written differently (a list's tightness) is shown as read back.
    const reread = new Set<typeof pending[number]>()
    for (let pass = 0; pass <= 2 * pending.length; pass++) {
      this.items = pending.map(entry => ({
        index: entry.index, json: json[entry.index]!, blanks: entry.blanks, separate: entry.separate,
        ...(ids.has(entry.index) ? { kind: 'protected' as const, id: ids.get(entry.index)! } : { kind: 'markdown' as const, key: entry.key! }),
      }))
      this.render()
      const scanned = this.scan(this.text)
      if (scanned.errors.length) throw new Error(`Source session produced an invalid text: ${scanned.errors[0]!.message}`)
      const matched = new Map(alignItems(this.items, scanned.items))
      let rewritten = false
      const failing = pending.filter((entry, position) => {
        if (ids.has(entry.index)) return false
        const counterpart = matched.get(position)
        if (counterpart === undefined) {
          const key = reread.has(entry) ? undefined : this.roundTrip(entry.node, entry.key!)
          reread.add(entry)
          if (key === undefined) return true
          // Reads back alone but not here: it joins a neighbour (two lists of a kind). A separator line keeps them apart.
          if (key === entry.key) {
            if (entry.separate) return true
            entry.separate = true
          } else entry.key = key
          rewritten = true
          return false
        }
        // A heading that holds more than its text says and is not named in it (a requirement without a lasting id) would be
        // known by its place only: it stays protected.
        if (entry.node.type.name === 'heading' && !scanned.items[counterpart]!.named && this.holdsMore(entry.node.attrs)) return true
        entry.comparable ??= comparable(entry.node.toJSON())
        return !sameJson(comparable(scanned.items[counterpart]!.json), entry.comparable)
      })
      if (!failing.length && !rewritten) {
        this.items.forEach((item, position) => { item.named = scanned.items[matched.get(position) ?? -1]?.named })
        return
      }
      for (const entry of failing) ids.set(entry.index, protect(entry, this.roundTrip(entry.node, entry.key!) === undefined ? 'round-trip' : 'merge'))
    }
    throw new Error('Source session could not stabilize protected blocks.')
  }

  private protectionReason(node: Node): MarkdownSourceProtectionReason | undefined {
    if (node.type.name === 'codeBlock' && /^md-protected/i.test(String(node.attrs.language ?? ''))) return 'reserved-code'
    // A table GFM cannot carry is written as ```a-table (dialect §4.12): the round trip decides what that fence keeps.
    if (node.type.name === 'table') return undefined
    // Heading ids (see merge), image presentation and page references (see restore) are carried separately, so their loss does not protect a block.
    return this.codec.getMarkdownLossy(this.codec.schema.topNodeType.create(null, [node]), { ignoreIdentity: true }) ? 'lossy' : undefined
  }

  /** A block's Markdown; `attrs` are a read heading's once `restore` gave it its aliases. */
  private key(node: Node, attrs: Record<string, unknown> = node.attrs): string {
    const markdown = this.codec.serializeMarkdown(this.editor!, node).replace(/\n+$/, '')
    // A heading with aliases names itself, so they follow it by its id. Without a reader of the form it does not read back and stays protected.
    const id = node.type.name === 'heading' && attrs.aliases ? attrs.headingId as unknown : null
    return typeof id === 'string' && !markdown.includes('\n') && !namesHeading(markdown, id) ? `${markdown} {#${id}}` : markdown
  }

  /** Whether a heading holds more than `## Title` says (and its id, carried apart): aliases, a host's attributes. */
  private holdsMore(attrs: Record<string, unknown>): boolean {
    return Object.keys(attrs).some(name => name !== 'level' && name !== 'headingId' && !sameJson(attrs[name] ?? null, this.plainHeading[name] ?? null))
  }

  /** Markdown as the source reads it: runs of spaces inside a line are kept, not collapsed like HTML. */
  private parse(markdown: string): Node {
    return this.codec.parseMarkdown(this.editor!, markdown, { preserveWhitespace: true })
  }

  /**
   * Gives a block read from Markdown back what its text identifies but cannot say: an image with an
   * address the page already shows gets that image's presentation (size, crop, caption), and a link
   * with the address and title of a page reference of the page (and no tooltip of its own) is that reference again.
   */
  private restore(node: JSONContent, top = false): JSONContent {
    // A top-level heading its line names (`{#id}`) is the page's heading of that id: named alone it holds what that one held;
    // with more in its block it holds that, the aliases (which the block does not say) and what the host makes of the change.
    if (top && node.type === 'heading') {
      const id = node.attrs?.headingId as unknown
      const original = typeof id === 'string' && !this.repeatedIds.has(id) ? this.headings.get(id) : undefined
      if (!original) return node
      if (!this.holdsMore(node.attrs!)) return { ...node, attrs: { ...original, level: node.attrs!.level } }
      const attrs = { ...node.attrs, aliases: original.aliases }
      const storages = Object.values(this.editor!.storage as unknown as Record<string, { markdown?: { headingRead?: (attrs: Record<string, unknown>, original: Record<string, unknown>) => Record<string, unknown> } } | undefined>)
      const host = storages.find(storage => typeof storage?.markdown?.headingRead === 'function')?.markdown!.headingRead
      return { ...node, attrs: host ? host(attrs, original) : attrs }
    }
    if (node.type === 'image' && isDefaultImagePresentation(node.attrs?.presentation) && this.presentations.has(String(node.attrs?.src))) {
      return { ...node, attrs: { ...node.attrs, presentation: this.presentations.get(String(node.attrs?.src)) } }
    }
    if (node.type === 'text') {
      const link = node.marks?.find(mark => mark.type === 'link')
      const page = link && !link.attrs?.title && this.pageReferences.get(pageKey(link.attrs?.href, node.text))
      if (!page) return node
      const marks = node.marks!.filter(mark => mark !== link)
      return { ...page as JSONContent, ...(marks.length ? { marks } : {}) }
    }
    return node.content ? { ...node, content: node.content.map(child => this.restore(child)) } : node
  }

  /** The Markdown of the block that `markdown` alone reads back, when that is `node` again. */
  private roundTrip(node: Node, markdown: string): string | undefined {
    try {
      const parsed = this.parse(markdown)
      const read = parsed.childCount === 1 ? this.restore(parsed.child(0).toJSON() as JSONContent, true) : undefined
      return read && sameJson(comparable(read), comparable(node.toJSON())) ? this.key(parsed.child(0), read.attrs) : undefined
    } catch { return undefined }
  }

  private label(node: Node, textSerializers: ReturnType<typeof getTextSerializersFromSchema>): string {
    const moduleLabel = (this.editor!.storage as unknown as Record<string, { label?: unknown } | undefined>)[node.type.name]?.label
    const kind = KIND_LABELS[node.type.name] ?? (typeof moduleLabel === 'string' && moduleLabel ? moduleLabel : sourceStrings.kindUnknown(node.type.name))
    const data = isRecord(node.attrs.data) ? node.attrs.data : undefined
    const text = (value: unknown) => typeof value === 'string' ? value : ''
    let title = text(data?.title) || text(data?.name)
    if (!title && Array.isArray(data?.contacts)) title = data.contacts.map(contact => isRecord(contact) ? text(contact.name) : '').filter(Boolean).join(', ')
    if (!title && node.type.name === 'image') title = text(node.attrs.alt) || text(node.attrs.title) || text(node.attrs.src).split(/[/?#]/).filter(Boolean).pop() || ''
    if (!title && node.type.name === 'codeBlock') title = text(node.attrs.language)
    if (!title && node.type.name !== 'image') title = text(node.attrs.title)
    if (!title) {
      try {
        const plain = getText(this.codec.schema.topNodeType.create(null, [node]), { blockSeparator: '\n', textSerializers })
        title = plain.split('\n').map(line => line.trim()).find(Boolean) ?? ''
        if (node.isAtom) title = title.split(' · ')[0]!
      } catch { title = '' }
    }
    return formatLabel(kind, title)
  }

  private render(): void {
    const chunks: string[] = []
    const ranges: MarkdownSourceProtectedRange[] = []
    let line = 1
    let previous: OriginalItem | undefined
    for (const item of this.items) {
      if (previous && (item.separate || previous.separate) && item.kind === 'markdown' && previous.kind === 'markdown' && item.json.type === previous.json.type) {
        chunks.push(BLOCK_SEPARATOR)
        line += 2
      }
      previous = item
      let chunk: string
      if (item.kind === 'protected') {
        const block = this.protectedMap.get(item.id!)!
        const lines = protectedFenceLines(block.id, block.label, block.text)
        chunk = lines.join('\n')
        ranges.push(Object.freeze({ id: block.id, startLine: line, endLine: line + lines.length - 1, label: block.label, text: block.text }))
      } else chunk = item.key!
      chunks.push(chunk)
      line += chunk.split('\n').length + 1
    }
    this.text = chunks.length ? `${chunks.join('\n\n')}\n` : ''
    this.protectedBlocks = Object.freeze(ranges)
  }

  // -------------------------------------------------------------------------
  // Apply
  // -------------------------------------------------------------------------

  /**
   * Own line scanner: protected fences and reserved `md-protected` fences outside code.
   */
  private scan(text: string): ScannedText {
    const lines = text.replace(/\r\n?/g, '\n').split('\n')
    const items: SessionItem[] = []
    const errors: MarkdownSourceDiagnostic[] = []
    const warnings: MarkdownSourceDiagnostic[] = []
    const seen = new Map<string, number>()
    let segment: string[] = []
    let segmentStart = 1
    let fence: { char: string; length: number; line: number } | null = null

    const cache = new Map<string, readonly SessionItem[]>()
    const isList = (item: SessionItem | undefined) => item?.kind === 'markdown' && LISTS.has(item.json.type!)
    /** Where a separator line stood after a list, until the block after it is known: not a list, and the line is text. */
    let separator: number | undefined
    const settle = () => {
      if (separator === undefined) return
      if (!isList(items[separator])) {
        const node = this.parse(`\\${BLOCK_SEPARATOR}`).child(0)
        items.splice(separator, 0, { kind: 'markdown', key: this.key(node), json: deepFreezePlainData(node.toJSON() as JSONContent) })
      }
      separator = undefined
    }
    const flush = (nextStart: number) => {
      const markdown = segment.join('\n')
      const cached = this.segmentCache.get(markdown) ?? cache.get(markdown)
      const start = segmentStart
      const place = (parsed: readonly SessionItem[]) => items.push(...parsed.map(item => item.offset === undefined ? item : { ...item, line: start + item.offset }))
      if (cached) { place(cached); cache.set(markdown, cached) }
      else if (markdown.trim()) {
        try {
          const parsed: SessionItem[] = []
          const read = () => this.parse(markdown)
          const { result, errors: refused } = loadedMarkdownDialect()?.withMarkdownDialectSession(false, read) ?? { result: read(), errors: [] }
          for (const issue of refused) errors.push({ line: start + issue.line - 1, column: issue.column, message: issue.message, severity: 'error' })
          // Where each top-level heading's line is: the heading lines outside code, in order (when every heading is such a line).
          let code: string | null = null
          const headingLines = segment.flatMap((text, index) => {
            const mark = FENCE_OPEN.exec(text)?.[2]
            if (code ? mark?.startsWith(code) : mark) { code = code ? null : mark!; return [] }
            return !code && HEADING_LINE.test(text) ? [index] : []
          })
          let headings = 0
          result.forEach(node => { if (node.type.name === 'heading') headings++ })
          let heading = 0
          result.forEach(node => {
            if (node.type.name === 'paragraph' && node.childCount === 0 && !node.attrs.textAlign) return
            const json = this.restore(node.toJSON() as JSONContent, true)
            const item: SessionItem = { kind: 'markdown', key: this.key(node, json.attrs), json: deepFreezePlainData(json) }
            if (node.type.name !== 'heading') { parsed.push(item); return }
            // Markdown reads no heading id but from a heading's own block: such a heading is named.
            const offset = headings === headingLines.length ? headingLines[heading++] : undefined
            parsed.push({ ...item, ...(node.attrs.headingId != null ? { named: true } : {}), ...(offset !== undefined ? { offset } : {}) })
          })
          place(parsed)
          // A refused construct is reported again when its text is read again.
          if (!refused.length) cache.set(markdown, parsed)
        } catch (error) {
          errors.push({ line: segmentStart, message: sourceStrings.markdownParseFailed(error instanceof Error ? error.message : String(error)), severity: 'error' })
        }
      }
      if (items.length > (separator ?? Infinity)) settle()
      segment = []
      segmentStart = nextStart
    }

    for (let index = 0; index < lines.length; index++) {
      const line = lines[index]!
      const lineNumber = index + 1
      if (fence) {
        segment.push(line)
        const close = FENCE_OPEN.exec(line)
        if (close && close[2]![0] === fence.char && close[2]!.length >= fence.length && !close[3]!.trim()) fence = null
        else {
          // Session ids are random: a known fence inside code means a broken code fence swallowed it.
          const swallowed = PROTECTED_OPEN.exec(line)
          if (swallowed && this.protectedMap.has(swallowed[1]!)) {
            errors.push({ line: lineNumber, column: 1, severity: 'error',
              message: sourceStrings.protectedBlockInsideUnclosedCode(swallowed[1]!, fence.line, fence.char.repeat(fence.length)) })
          }
        }
        continue
      }
      const open = FENCE_OPEN.exec(line)
      if (open && /^md-protected/i.test(open[3]!.trim())) {
        const strict = PROTECTED_OPEN.exec(line)
        if (!strict) {
          errors.push({ line: lineNumber, column: open[1]!.length + 1, severity: 'error',
            message: sourceStrings.protectedLanguageReserved })
          segment.push(line)
          fence = { char: open[2]![0]!, length: open[2]!.length, line: lineNumber }
          continue
        }
        flush(lineNumber)
        const id = strict[1]!
        const idColumn = line.indexOf(' ') + 2
        const block = this.protectedMap.get(id)
        // The whole block must stand as written: its label and text are a view of the node, not a place to edit it.
        const expected = block ? protectedFenceLines(id, block.label, block.text) : []
        const damaged = expected.findIndex((expectedLine, offset) => index + offset >= lines.length || !sameProtectedLine(lines[index + offset]!, expectedLine))
        if (!block) {
          errors.push({ line: lineNumber, column: idColumn, message: sourceStrings.unknownProtectedBlock(id), severity: 'error' })
        } else if (seen.has(id)) {
          errors.push({ line: lineNumber, column: idColumn, severity: 'error',
            message: sourceStrings.duplicateProtectedBlock(id, seen.get(id)!) })
        } else {
          seen.set(id, lineNumber)
          if (damaged < 0) { items.push({ kind: 'protected', id, json: block.node as JSONContent }); settle() }
          else if (damaged === expected.length - 1) {
            errors.push({ line: Math.min(lineNumber + damaged, lines.length), column: 1, severity: 'error',
              message: sourceStrings.protectedBlockMustClose(id) })
          } else {
            errors.push({ line: Math.min(lineNumber + damaged, lines.length), column: 1, severity: 'error',
              message: sourceStrings.protectedBlockContentChanged(id, expected[damaged]!) })
          }
        }
        index = block && damaged < 0 ? index + expected.length - 1 : this.protectedFenceEnd(lines, index)
        segmentStart = index + 2
        continue
      }
      if (open && !(open[2]![0] === '`' && open[3]!.includes('`'))) {
        fence = { char: open[2]![0]!, length: open[2]!.length, line: lineNumber }
        segment.push(line)
        continue
      }
      // A separator line on its own between two lists: they are read apart. Anywhere else the line is the writer's text
      // (after a list it waits for what follows: `settle`).
      if (line === BLOCK_SEPARATOR && !lines[index - 1]?.trim() && !lines[index + 1]?.trim()) {
        flush(lineNumber)
        // One line separates: a second one right after it is the writer's.
        if (separator === undefined && isList(items[items.length - 1])) {
          separator = items.length
          segmentStart = lineNumber + 1
          continue
        }
      }
      segment.push(line)
    }
    flush(lines.length + 1)
    settle()
    // Only the latest text's segments are kept: an edit reparses just the segments it touched.
    this.segmentCache = cache
    return { items, errors, warnings }
  }

  /** Recovery for a damaged protected fence: skip to its closing line so the rest still scans. */
  private protectedFenceEnd(lines: readonly string[], open: number): number {
    for (let index = open + 1; index < lines.length; index++) {
      if (PROTECTED_CLOSE.test(lines[index]!)) return index
      if (PROTECTED_OPEN.test(lines[index]!)) return index - 1
    }
    return open + 1 < lines.length && !FENCE_OPEN.test(lines[open + 1]!) ? open + 1 : open
  }

  /** Swap fences for original nodes and reuse original JSON for every aligned block. */
  private merge(items: readonly SessionItem[]): JSONContent[] {
    const original = this.items
    const pairs = alignItems(original, items)
    const content: JSONContent[] = []
    const namedNow = new Set(items.filter(item => item.named).map(item => item.json.attrs!.headingId))
    this.stripped = []
    let left = 0, right = 0
    const gap = (leftEnd: number, rightEnd: number) => {
      const removed = original.slice(left, leftEnd)
      const added = items.slice(right, rightEnd)
      // A named heading whose line is still there without its name would silently become another heading (a requirement
      // would be gone): the name comes back, or the whole heading goes.
      for (const old of removed) {
        if (!old.named || namedNow.has(old.json.attrs!.headingId)) continue
        const bare = added.find(item => item.kind === 'markdown' && item.json.type === 'heading' && !item.named
          && item.json.attrs?.level === old.json.attrs?.level && sameJson(item.json.content, old.json.content))
        if (bare) this.stripped.push({ line: bare.line ?? 1, message: sourceStrings.headingNameRemoved(String(old.json.attrs!.headingId)), severity: 'error' })
      }
      // An edited heading in the same slot keeps its identity (share links, TOC).
      const oldHeadings = removed.filter(item => item.kind === 'markdown' && item.json.type === 'heading' && item.json.attrs?.headingId != null && !item.named)
      const newHeadings = added.filter(item => item.kind === 'markdown' && item.json.type === 'heading' && item.json.attrs?.headingId == null)
      const carried = new Map<SessionItem, unknown>()
      newHeadings.slice(0, oldHeadings.length).forEach((item, index) => carried.set(item, oldHeadings[index]!.json.attrs!.headingId))
      const sameSlots = removed.length === added.length
      added.forEach((item, index) => {
        if (item.kind === 'protected') { content.push(item.json); return }
        if (sameSlots) content.push(...removed[index]!.blanks)
        content.push(carried.has(item) ? { ...item.json, attrs: { ...item.json.attrs, headingId: carried.get(item) } } : item.json)
      })
    }
    for (const [leftIndex, rightIndex] of pairs) {
      gap(leftIndex, rightIndex)
      const item = original[leftIndex]!
      content.push(...item.blanks, item.json)
      left = leftIndex + 1
      right = rightIndex + 1
    }
    gap(original.length, items.length)
    content.push(...this.trailingBlanks)
    return content.length ? content : [{ type: 'paragraph' }]
  }

  private sorted(diagnostics: MarkdownSourceDiagnostic[]): readonly MarkdownSourceDiagnostic[] {
    return Object.freeze(diagnostics.sort((a, b) => a.line - b.line || (a.column ?? 0) - (b.column ?? 0)).map(item => Object.freeze(item)))
  }
}

/** Creates a detached source session; call `destroy()` when the source view closes. */
export function createMarkdownSourceSession(options: MarkdownSourceSessionOptions): MarkdownSourceSession {
  return new SourceSession(options)
}

/** The source is Markdown: its dialect loads first (an open editor has usually loaded it already). */
export async function openMarkdownSourceSession(options: MarkdownSourceSessionOptions): Promise<MarkdownSourceSession> {
  await loadMarkdownDialect()
  return new SourceSession(options)
}
