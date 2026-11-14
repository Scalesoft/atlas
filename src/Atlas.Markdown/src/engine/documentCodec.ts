import { Extension, getSchema, getText, getTextSerializersFromSchema, type AnyExtension, type Editor, type JSONContent } from '@tiptap/core'
import type { Node, ParseOptions } from '@tiptap/pm/model'
import { Plugin } from '@tiptap/pm/state'
import { createMarkdownContentKit, type MarkdownContentKitOptions } from '../core/kit'
import { isRemoteChange, tableMarkdownLossy, tableMarkdownStructurallyLossy } from '../core/tableMarkdown'
import type { MarkdownPluginRegistry } from '../core/plugins/registry'
import type { MarkdownProfile, MarkdownProjection, MarkdownRichValue, MarkdownValue } from '../core/types'
import { RICH_FORMAT } from '../core/types'
import { validateParsedDocument, validateValue } from './valueValidation'
import type { MarkdownLossyOptions, MarkdownSchemaPlugin } from '../core/plugins/types'
import { resolveSchemaOwnership, usedSchemaPlugins } from './schemaOwnership'
import { validateBuiltinDocument } from './builtinDocumentValidation'
import { clonePlainData } from '../core/plainData'
import { storedDocumentJson } from './storedDocument'
import { repairLegacyValue } from './legacyDocument'
import { createMarkdownReferenceExtension } from '../features/references'
import { PasteInputGuard } from './pasteInputGuard'
import { createRuntimeDocumentDataValidator } from './runtimeDocumentData'
import { parseInputDocument } from './markdownInput'
import type { MarkdownDialectIssue } from './markdownDialect'
import { loadedMarkdownDialect, withMarkdownSerialization } from './markdownDialectLoader'
import { isDefaultImagePresentation } from '../features/images/presentation'

// tiptap-markdown does not augment the engine's Storage type.
function markdownStorage(editor: Editor) {
  return (editor.storage as unknown as {
    markdown: { serializer: { serialize(document: Node): string }
      parser: { parse(content: string | JSONContent): string | JSONContent; md: { set(options: { linkify: boolean }): unknown } } }
  }).markdown
}

/** Parse using the editor's installed Markdown codec and validate the resulting document. */
export function parseMarkdownContent(editor: Editor, markdown: string, options?: ParseOptions): Node {
  return validateParsedDocument(parseInputDocument(markdownStorage(editor).parser.parse(markdown), editor.schema, options))
}

/**
 * Makes this editor read Markdown text literally, for the page source: a bare address stays text (a link is written
 * `<url>` or `[text](url)`), as the server reads Markdown (MarkdownToRich). Otherwise text that only looks like an
 * address would come back as a link.
 */
export function readMarkdownLiterally(editor: Editor): void {
  // `pageSource`: a host's reader of a form only the page source reads (a heading's `{#id …}` block) looks for it.
  markdownStorage(editor).parser.md.set({ linkify: false, pageSource: true } as { linkify: boolean })
}

export interface MarkdownDialectResult {
  /** The parsed document; absent when strict parsing found errors. */
  readonly document?: Node
  /** Strict mode: every unknown or invalid `a-` component, with its position. Always empty in lenient mode. */
  readonly errors: readonly MarkdownDialectIssue[]
  /** Constructs read with a change that keeps their meaning, and in lenient mode every component kept as code or text. */
  readonly warnings: readonly MarkdownDialectIssue[]
}

/**
 * Parses Markdown with the editor dialect's two modes (dialect spec §2.6). Strict (writes through an API): an unknown
 * or invalid `a-` component is an error and no document is returned. Lenient (older Markdown): the component stays a
 * code block or literal text and is reported as a warning. Both validate the document like `parseMarkdownContent`.
 * Markdown with `a-` components needs the loaded dialect (`loadMarkdownDialect`); without any, nothing is reported.
 */
export function parseMarkdownDialect(editor: Editor, markdown: string, options: { strict?: boolean } = {}): MarkdownDialectResult {
  const dialect = loadedMarkdownDialect()
  if (!dialect) return { document: parseMarkdownContent(editor, markdown), errors: [], warnings: [] }
  const { result, errors, warnings } = dialect.withMarkdownDialectSession(!!options.strict, () => parseMarkdownContent(editor, markdown))
  return errors.length ? { errors, warnings } : { document: result, errors, warnings }
}

/** Serialize a document or block using the editor's installed Markdown codec. */
export function serializeMarkdownContent(editor: Editor, node: Node): string {
  const document = node.type === node.type.schema.topNodeType ? node : editor.schema.topNodeType.create(null, [node])
  return withMarkdownSerialization(document, () => markdownStorage(editor).serializer.serialize(document))
}
/** One content configuration for construction, replacement and all projections. */
export class MarkdownDocumentCodec<P extends MarkdownProfile> {
  readonly extensions: AnyExtension[]
  readonly schema: ReturnType<typeof getSchema>
  private document: Node | null = null
  private markdown: string | undefined
  private text: string | undefined
  private rich: MarkdownRichValue | undefined
  private lossy: boolean | undefined
  /** The plugin owning each schema field ("nodes:type", "nodes:type:attribute", "marks:…"). */
  readonly schemaOwners = new Map<string, MarkdownSchemaPlugin>()

  constructor(readonly profile: P, private readonly plugins: MarkdownPluginRegistry, options: MarkdownContentKitOptions) {
    const base = createMarkdownContentKit(options).extensions
    const jsonGuardedBase = [...base, jsonSafeDocumentGuard(), PasteInputGuard]
    const guardedBase = profile === 'markdown-safe'
      ? [...jsonGuardedBase, markdownSafePluginGuard(plugins, this.schemaOwners)]
      : jsonGuardedBase
    const ownership = resolveSchemaOwnership(guardedBase, plugins.extensionContributions)
    plugins.initializeSchema(ownership.pluginIds)
    const schemas = new Map(plugins.schemaPlugins.map(schema => [schema.id, schema]))
    for (const [field, id] of ownership.fieldOwners) this.schemaOwners.set(field, schemas.get(id)!)
    this.extensions = [...guardedBase, ...plugins.extensions,
      ...(plugins.references.length ? [createMarkdownReferenceExtension(plugins.references)] : []),
    ]
    this.schema = getSchema(this.extensions)
    for (const provider of plugins.references) {
      const node = this.schema.nodes[provider.nodeType]
      if (!node?.isInline || !node.isAtom || node.isText) {
        throw new TypeError(`Reference provider ${provider.id} must describe an installed inline atom.`)
      }
    }
  }

  normalize(value: unknown): MarkdownValue<P> {
    const migrated = repairLegacyValue(this.plugins.migrate(value))
    validateValue(this.profile, migrated, this.schema, this.plugins.installedSchemaVersions, this.schemaOwners)
    if (typeof migrated === 'string') return migrated as MarkdownValue<P>
    const envelope = migrated as MarkdownRichValue
    return structuredClone({
      format: RICH_FORMAT, version: envelope.version, document: envelope.document,
      ...(this.plugins.schemaPlugins.length ? { plugins: this.plugins.schemaPlugins } : {}),
    }) as MarkdownValue<P>
  }

  /** Strict JSON input under this codec's schema, without migrations or Markdown reparsing. */
  parseRichDocument(value: unknown): Node {
    const owned = clonePlainData(value)
    validateValue('rich', owned, this.schema, this.plugins.installedSchemaVersions, this.schemaOwners)
    return this.schema.nodeFromJSON(owned.document)
  }

  parse(editor: Editor, value: MarkdownValue<P>): Node {
    const content = (typeof value === 'string' ? value : value.document) as string | JSONContent
    return this.validateParsedDocument(parseInputDocument(markdownStorage(editor).parser.parse(content), editor.schema))
  }

  /** Internal compatibility boundary for the Markdown-valued legacy rich editor. */
  parseMarkdown(editor: Editor, markdown: string, options?: ParseOptions): Node {
    return parseMarkdownContent(editor, markdown, options)
  }

  /** Also used after initial Tiptap construction, before publishing or mounting it. */
  validateParsedDocument(document: Node): Node {
    return validateParsedDocument(document)
  }

  getValue(editor: Editor, document: Node): MarkdownValue<P> {
    validateBuiltinDocument(document)
    this.select(document)
    if (this.profile === 'markdown-safe' && this.markdownLossy(document)) {
      throw new Error('The markdown-safe document contains content that cannot round-trip through Markdown.')
    }
    if (this.profile === 'markdown-safe') return this.getMarkdown(editor, document) as MarkdownValue<P>
    // What a save stores: without attributes at their default. The projection's rich JSON (getRich) stays toJSON, getJSON's form.
    return { format: RICH_FORMAT, version: 1, document: clonePlainData(storedDocumentJson(document)),
      ...(this.plugins.schemaPlugins.length ? { plugins: structuredClone(this.plugins.schemaPlugins) } : {}) } as MarkdownValue<P>
  }

  /**
   * Read fidelity without materializing Markdown, plain text or rich JSON.
   * `ignoreIdentity` skips what the caller carries separately: heading ids, image presentation and page references.
   */
  getMarkdownLossy(document: Node, options: MarkdownLossyOptions = {}): boolean {
    validateBuiltinDocument(document)
    const lossy = () => builtinMarkdownLossy(document, options) || this.plugins.isLossy(document, usedSchemaPlugins(document, this.schemaOwners), options)
    if (options.ignoreIdentity) return lossy()
    this.select(document)
    return this.lossy ??= lossy()
  }

  /** Markdown of a document or of one top-level block, without fidelity checks. */
  serializeMarkdown(editor: Editor, node: Node): string {
    return serializeMarkdownContent(editor, node)
  }

  project(editor: Editor, document: Node): MarkdownProjection {
    const markdownLossy = this.getMarkdownLossy(document)
    this.text ??= getText(document, { blockSeparator: '\n\n', textSerializers: getTextSerializersFromSchema(editor.schema) })
    return { markdown: this.getMarkdown(editor, document), text: this.text, rich: structuredClone(this.getRich(document)), markdownLossy }
  }

  private select(document: Node): void {
    if (document === this.document) return
    this.document = document
    this.markdown = this.text = undefined
    this.rich = undefined
    this.lossy = undefined
  }
  private getMarkdown(editor: Editor, document: Node): string {
    return this.markdown ??= this.serializeMarkdown(editor, document)
  }
  private getRich(document: Node): MarkdownRichValue {
    return this.rich ??= {
      format: RICH_FORMAT, version: 1,
      document: clonePlainData(document.toJSON()) as MarkdownRichValue['document'],
      ...(this.plugins.schemaPlugins.length ? { plugins: this.plugins.schemaPlugins } : {}),
    }
  }
  private markdownLossy(document: Node): boolean {
    return builtinMarkdownLossy(document) || this.plugins.isSchemaLossy(document, usedSchemaPlugins(document, this.schemaOwners))
  }
}

function jsonSafeDocumentGuard(): AnyExtension {
  const validate = createRuntimeDocumentDataValidator()
  return Extension.create({
    name: 'jsonSafeDocumentGuard',
    addProseMirrorPlugins: () => [new Plugin({
      filterTransaction(transaction) {
        if (!transaction.docChanged) return true
        // Only the top-level blocks the transaction replaced: the rest are the same immutable nodes the guard already let in, and
        // the whole document cost ~3 ms a keystroke on a 2 MB page (UX EP2-2).
        const kept = new Set<Node>()
        transaction.before.forEach(block => { kept.add(block) })
        try {
          validate(transaction.doc.copy())
          transaction.doc.forEach(block => { if (!kept.has(block)) validate(block) })
          return true
        } catch { return false }
      },
    })],
  })
}

function markdownSafePluginGuard(plugins: MarkdownPluginRegistry, owners: ReadonlyMap<string, MarkdownSchemaPlugin>): AnyExtension {
  return Extension.create({
    name: 'markdownSafePluginRoundTripGuard',
    addProseMirrorPlugins: () => {
      const lossy = (document: Node) => builtinMarkdownTransactionLossy(document) || plugins.isSchemaLossy(document, usedSchemaPlugins(document, owners))
      return [new Plugin({
        // A merge of concurrent edits cannot be refused (the editor would fall behind Yjs and its next edit would revert the
        // merge), and a document a merge left lossy (a table one collaborator repairs) keeps accepting edits.
        filterTransaction: transaction => !transaction.docChanged || !lossy(transaction.doc) || isRemoteChange(transaction) || lossy(transaction.before),
      })]
    },
  })
}

/**
 * Built-in content without a faithful Markdown form in the rich profile. Callouts (GitHub alerts) and
 * highlight (`==text==`) are part of the editor dialect and round-trip; their content still decides.
 */
export function builtinMarkdownLossy(document: Node, options: MarkdownLossyOptions = {}): boolean {
  let lossy = false
  document.descendants(node => {
    if (node.type.name === 'image' && !options.ignoreIdentity && !isDefaultImagePresentation(node.attrs.presentation)) lossy = true
    if (node.type.name === 'table' && tableMarkdownLossy(node)) lossy = true
    if (node.marks.some(mark => mark.type.name === 'underline')
      || node.attrs.backgroundColor || node.attrs.height
      || (node.attrs.textAlign && node.type.name !== 'tableCell' && node.type.name !== 'tableHeader')
      || (node.attrs.colspan ?? 1) > 1 || (node.attrs.rowspan ?? 1) > 1 || node.attrs.colwidth) lossy = true
  })
  return lossy
}

/** The markdown-safe guard: its schema has no callout or highlight, so either would come from elsewhere and stay lossy. */
function builtinMarkdownTransactionLossy(document: Node): boolean {
  let lossy = false
  document.descendants(node => {
    if ((node.type.name === 'image' && !isDefaultImagePresentation(node.attrs.presentation)) || node.type.name === 'callout') lossy = true
    if (node.type.name === 'table' && tableMarkdownStructurallyLossy(node)) lossy = true
    if (node.marks.some(mark => ['underline', 'highlight'].includes(mark.type.name))
      || node.attrs.backgroundColor || node.attrs.height
      || (node.attrs.textAlign && node.type.name !== 'tableCell' && node.type.name !== 'tableHeader')
      || node.attrs.colwidth) lossy = true
  })
  return lossy
}
