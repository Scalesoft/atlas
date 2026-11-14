import type { MarkdownProfile } from './kit'
import type { AnyExtension } from '@tiptap/core'
import type { ImageUploadService } from '../features/images/uploadService'
import type { MarkdownResourcePolicy } from '../features/images/resourcePolicy'
import type { MarkdownAction, MarkdownPlugin, MarkdownPluginPanel, MarkdownSchemaPlugin } from './plugins/types'
import type { CommandState, MarkdownCommand, MarkdownDispatchOptions } from './commandTypes'
export type { CommandState, MarkdownCommand, MarkdownDispatchOptions } from './commandTypes'
export type { MarkdownProfile }

export type JsonValue = null | boolean | number | string | readonly JsonValue[] | { readonly [key: string]: JsonValue }
/** The format id every rich document is written with. */
export const RICH_FORMAT = 'atlas-rich' as const
// legacy id of documents stored before 5.0
export const LEGACY_RICH_FORMAT = 'scalesoft-rich' as const
export type MarkdownRichFormat = typeof RICH_FORMAT | typeof LEGACY_RICH_FORMAT
/** Whether `value` is a rich format id this editor reads (the current one or the legacy one). */
export const isRichFormat = (value: unknown): value is MarkdownRichFormat => value === RICH_FORMAT || value === LEGACY_RICH_FORMAT
export interface MarkdownRichValue {
  readonly format: typeof RICH_FORMAT
  readonly version: 1
  readonly plugins?: readonly MarkdownSchemaPlugin[]
  readonly document: { readonly [key: string]: JsonValue }
}
/** A rich value as read: documents stored before 5.0 carry the legacy format id. */
export type MarkdownRichInput = Omit<MarkdownRichValue, 'format'> & { readonly format: MarkdownRichFormat }
export type MarkdownValue<P extends MarkdownProfile = MarkdownProfile> = P extends 'markdown-safe' ? string : MarkdownRichValue
export type MarkdownValueInput<P extends MarkdownProfile = MarkdownProfile> = P extends 'markdown-safe' ? string : MarkdownRichInput
export interface MarkdownDocumentSchema {
  /** Built-in content schema revision, independent of the rich JSON envelope version. */
  readonly version: 1 | 2 | 3 | 4 | 5 | 6 | 7
  readonly profile: MarkdownProfile
  readonly plugins: readonly MarkdownSchemaPlugin[]
}
export interface MarkdownProjection {
  readonly markdown: string
  readonly text: string
  readonly rich: MarkdownRichValue
  readonly markdownLossy: boolean
}
export interface MarkdownContentStamp { readonly sessionId: string; readonly revision: number }
export interface MarkdownContentChange extends MarkdownContentStamp { readonly origin: 'local' | 'remote' }
export interface MarkdownValueSnapshot<P extends MarkdownProfile = MarkdownProfile> extends MarkdownContentStamp {
  readonly value: MarkdownValue<P>
}
export interface MarkdownContentCapability<P extends MarkdownProfile = MarkdownProfile> {
  /** Lightweight identity only; does not serialize document content. */
  getStamp(): MarkdownContentStamp
  /** Content batches only, without an initial synthetic event. */
  subscribe(listener: (change: MarkdownContentChange) => void): () => void
  /** Explicit, detached and deeply immutable value with the identity actually captured. */
  getSnapshot(): MarkdownValueSnapshot<P>
}
export interface MarkdownEditorState {
  readonly contentRevision: number
  readonly changeOrigin: 'local' | 'remote'
  readonly lifecycle: 'idle' | 'starting' | 'ready' | 'failed' | 'destroyed'
  readonly binding: 'local' | 'collaborative'
  readonly editable: boolean
  readonly focused: boolean
  readonly connection: 'local' | 'connecting' | 'online' | 'reconnecting' | 'offline' | 'failed'
  readonly documentSave: 'local' | 'pending' | 'saved' | 'degraded'
  readonly projectionSave: 'local' | 'pending' | 'current' | 'degraded'
  readonly pendingChanges: number
  readonly error: null | { readonly kind: 'configuration' | 'content' | 'collaboration' | 'projection'; readonly message: string; readonly recoverable: boolean }
  readonly peers: readonly { readonly clientId: number; readonly id?: string; readonly name: string; readonly color: string }[]
  /** Measured quality of the collaboration link; null for a local document. */
  readonly link: null | MarkdownLinkQuality
}
export interface MarkdownLinkQuality {
  /** Median server acknowledgement round-trip of recent local edits; null until the first sample. */
  readonly latencyMs: number | null
  /** Connection drops observed since this editor session started. */
  readonly drops: number
  /** Local edits have been waiting for an acknowledgement longer than a healthy link takes. */
  readonly stalled: boolean
}
/** What a Markdown paste or `.md` file drop brought along besides its content. */
export interface MarkdownImportInfo {
  /** `paste` covers Markdown text pasted (or dragged in as text); `file` a dropped `.md` file. */
  readonly source: 'paste' | 'file'
  readonly fileName?: string
  /** Trimmed string `title` from a leading YAML front matter block. */
  readonly title?: string
  /** Top-level front matter keys (strings, numbers, booleans, null and flat lists); the block is never inserted. */
  readonly frontMatter?: { readonly [key: string]: JsonValue }
  /** Text of the imported content's first block when that block is an H1. */
  readonly leadingHeading?: string
  /** Constructs inserted as literal text because the document has no equivalent, one entry per kind. */
  readonly degraded?: readonly MarkdownImportDegradedPart[]
}
/** Foreign-wiki syntax that an import kept as literal text. */
export type MarkdownImportDegradedKind =
  | 'wiki-link' | 'embed' | 'html' | 'comment' | 'footnote' | 'highlight' | 'subscript' | 'superscript' | 'math' | 'toc'
export interface MarkdownImportDegradedPart {
  readonly kind: MarkdownImportDegradedKind
  /** Occurrences of this kind in the import. */
  readonly count: number
  /** The first occurrence, whitespace collapsed and shortened to at most 80 characters. */
  readonly excerpt: string
}
export interface MarkdownImportDecision {
  /** Drop the leading H1 (for example when the host used it or the front matter title as the page title). */
  readonly omitLeadingHeading?: boolean
}
/**
 * Called synchronously before insertion when an import carries front matter, starts with an H1
 * or kept constructs as literal text (`degraded`).
 */
export type MarkdownImportHandler = (info: MarkdownImportInfo) => MarkdownImportDecision | void
/**
 * A wiki link `[[target#heading|alias]]` (Obsidian order) or embed `![[…]]` found in imported Markdown.
 * GitHub wikis write `[[Link text|Page-Name]]`; `raw` keeps the unsplit inner text for such hosts.
 */
export interface MarkdownWikiLinkRequest {
  readonly target: string
  readonly heading?: string
  readonly alias?: string
  readonly raw: string
  readonly source: MarkdownImportInfo['source']
}
export interface MarkdownWikiLinkResolution {
  /** `http(s):`, `mailto:`, `tel:` or a relative URL; anything else keeps the literal text. */
  readonly href: string
  /** Link text; defaults to the alias, else the target (and heading). */
  readonly label?: string
}
/** Synchronous; return null to keep the literal `[[…]]` text. */
export type MarkdownWikiLinkResolver = (request: MarkdownWikiLinkRequest) => MarkdownWikiLinkResolution | null | undefined
/** An image (`src`, `http(s):` or relative) or, for a page transclusion or file, a link. */
export type MarkdownEmbedResolution =
  | { readonly src: string; readonly alt?: string; readonly href?: never }
  | { readonly href: string; readonly label?: string; readonly src?: never }
/** Synchronous; return null to keep the literal `![[…]]` text. */
export type MarkdownEmbedResolver = (request: MarkdownWikiLinkRequest) => MarkdownEmbedResolution | null | undefined
declare const collaborationAdapterBrand: unique symbol
export interface MarkdownCollaborationAdapter { readonly [collaborationAdapterBrand]: true }
interface ControllerOptions<P extends MarkdownProfile> {
  readonly profile: P
  readonly editable?: boolean
  readonly placeholder?: string
  readonly compact?: boolean
  /** Optional codeBlock replacement loaded before constructing the controller. */
  readonly codeBlock?: AnyExtension
  readonly plugins?: readonly MarkdownPlugin[]
  readonly imageUpload?: ImageUploadService
  readonly resourcePolicy?: MarkdownResourcePolicy
  /** Listener exceptions are isolated; observe them here without interrupting committed edits. */
  readonly onSubscriberError?: (error: unknown, state: MarkdownEditorState) => void
  /** Observe front matter and a leading H1 of pasted/dropped Markdown, e.g. to fill an empty page title. */
  readonly onMarkdownImport?: MarkdownImportHandler
  /** Map `[[Page]]`, `[[Page|alias]]` and `[[Page#heading]]` in pasted/dropped Markdown to links. */
  readonly resolveWikiLink?: MarkdownWikiLinkResolver
  /** Map Obsidian `![[file.png]]` / `![[Page#heading]]` embeds in pasted/dropped Markdown to images or links. */
  readonly resolveEmbed?: MarkdownEmbedResolver
  /** What the place knows by name (people, pages, YouTrack projects) for linking while typing (P15); read from memory, never awaited. */
  readonly lexicon?: import('./recognition/lexicon').MarkdownLexiconSource
}
export interface LocalControllerOptions<P extends MarkdownProfile> extends ControllerOptions<P> {
  readonly source: { readonly kind: 'local'; readonly value: MarkdownValueInput<P>; readonly adapter?: never }
}
export interface CollaborativeControllerOptions<P extends MarkdownProfile> extends ControllerOptions<P> {
  readonly source: { readonly kind: 'collaborative'; readonly adapter: MarkdownCollaborationAdapter; readonly value?: never }
}
export type MarkdownControllerOptions<P extends MarkdownProfile> = ControllerOptions<P> & {
  readonly source: LocalControllerOptions<P>['source'] | CollaborativeControllerOptions<P>['source']
}
export interface MarkdownEditorController<P extends MarkdownProfile = MarkdownProfile> {
  readonly profile: P
  readonly schema: MarkdownDocumentSchema
  readonly state: MarkdownEditorState
  readonly actions: Readonly<{ slash: readonly MarkdownAction[]; toolbar: readonly MarkdownAction[]; selection: readonly MarkdownAction[] }>
  readonly panels: Readonly<{ inspector: readonly MarkdownPluginPanel[]; status: readonly MarkdownPluginPanel[] }>
  /** `beforeMount` runs once the engine exists, right before its view is built into `element` (a host's node-view context). */
  attachView(element: HTMLElement, options: { readonly ariaLabel: string; readonly beforeMount?: () => void }): () => void
  focus(): boolean
  blur(): void
  setEditable(editable: boolean): void
  dispatch(command: MarkdownCommand, options?: MarkdownDispatchOptions): boolean
  query(command: MarkdownCommand, options?: MarkdownDispatchOptions): CommandState
  /** Collaborative reads require an attached view; a detached session continues receiving updates. */
  getValue(): MarkdownValue<P>
  getProjection(): MarkdownProjection
  /** Optional cheap fidelity read with the same view requirements as getProjection; no serialization. */
  getMarkdownLossy?(): boolean
  subscribe(listener: (state: MarkdownEditorState) => void): () => void
  destroy(): void
}
export interface LocalMarkdownEditorController<P extends MarkdownProfile = MarkdownProfile> extends MarkdownEditorController<P> {
  setValue(value: MarkdownValueInput<P>): void
}
