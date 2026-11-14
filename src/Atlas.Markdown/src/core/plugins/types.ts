import type { AnyExtension, Editor, JSONContent } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import type { MarkdownProfile } from '../kit'
import type { JsonValue } from '../types'
import type { CommandState, MarkdownCommand, MarkdownDispatchOptions, MarkdownPluginCommandCall, MarkdownPluginCommandDefinition } from '../commandTypes'
export type { MarkdownPluginCommand, MarkdownPluginCommandCall, MarkdownPluginCommandDefinition, MarkdownPluginCommandOptions } from '../commandTypes'
import type { MarkdownIntentionProvider } from '../../features/intentions/intentions'
export type { MarkdownIntention, MarkdownIntentionContext, MarkdownIntentionProvider } from '../../features/intentions/intentions'

export interface MarkdownPluginController {
  readonly profile: MarkdownProfile
  readonly state: { readonly lifecycle: string; readonly editable: boolean }
  focus(): boolean
  blur(): void
  setEditable(editable: boolean): void
  dispatch(command: MarkdownCommand, options?: MarkdownDispatchOptions): boolean
  query(command: MarkdownCommand, options?: MarkdownDispatchOptions): CommandState
  destroy(): void
}

export interface MarkdownAction {
  readonly id: string
  readonly label: string
  readonly description?: string
  readonly icon?: string
  /** Framework-neutral host icon; cleanup runs when its surface unmounts. */
  readonly renderIcon?: (container: HTMLElement) => void | (() => void)
  readonly group?: string
  readonly priority?: number
  /** Extra slash search terms. Matching ignores case and diacritics ("zminka" finds "Zmínka"). */
  readonly keywords?: readonly string[]
  readonly variants?: readonly ('document' | 'compact')[]
  /** Omit unavailable contextual actions from every selection surface (default false). */
  readonly hideWhenDisabled?: boolean
  readonly command: MarkdownCommand
  /**
   * The slash menu continues after a space following the action's label or one of its keywords
   * (`/kontakt Petr`): it offers "{label}: vyhledat „Petr“" and dispatches `command(text)` instead of `command`.
   * Slash only; toolbar and selection surfaces ignore it. Without a space the action behaves as before.
   */
  readonly parameter?: MarkdownActionParameter
}
export interface MarkdownActionParameter {
  /** Names the parameter in the menu row: "{label}: vyhledat „text“". */
  readonly label: string
  /** The command for the typed text, which is non-empty and trimmed; nothing when the text names no choice (no row). */
  command(text: string): MarkdownCommand | undefined
  /** The row's title for the text in place of "{label}: vyhledat „text“" („Graf: Koláčový“). */
  title?(text: string): string
}
export type MarkdownPanelPlacement = 'inspector' | 'status'
export interface MarkdownPluginPanel {
  readonly id: string
  readonly label: string
  readonly placement: MarkdownPanelPlacement
  readonly priority?: number
  readonly variants?: readonly ('document' | 'compact')[]
  /** Mount framework-neutral UI into the supplied host. */
  readonly mount: (context: {
    readonly container: HTMLElement
    readonly controller: MarkdownPluginController
    readonly signal: AbortSignal
  }) => void | (() => void)
}
export interface MarkdownPluginContext {
  readonly editor: Editor
  readonly controller: MarkdownPluginController
  /** Aborted when the controller is destroyed, including failed initialization. */
  readonly signal: AbortSignal
}
/** Deeply immutable, JSON-safe ProseMirror document supplied to static render hooks. */
export interface MarkdownReadonlyJSONContent {
  readonly type?: string
  readonly attrs?: Readonly<Record<string, JsonValue>>
  readonly content?: readonly MarkdownReadonlyJSONContent[]
  readonly marks?: readonly MarkdownReadonlyJSONContent[]
  readonly text?: string
}
export interface MarkdownPluginRenderContext {
  /** The host already contains the serialized, validated document when this hook runs. */
  readonly container: HTMLElement
  readonly document: MarkdownReadonlyJSONContent
  readonly ownerDocument: Document
  /** Aborted before render cleanup runs, on replacement, failure or unmount. */
  readonly signal: AbortSignal
}
export interface MarkdownLossyOptions {
  /**
   * Identity attributes (e.g. heading ids) are not content: a caller that carries them
   * separately, such as the source session checking one block, asks to ignore their loss.
   * The session carries an image's presentation and a page reference the same way.
   */
  readonly ignoreIdentity?: boolean
}
/** A saved label and safe navigation URL; neither is an authorization decision. */
export interface MarkdownReferenceValue {
  readonly url: string
  readonly label: string
}
/** Optional inline-reference capability. Core owns UI, target mapping and document writes. */
export interface MarkdownReferenceProvider {
  readonly id: string
  readonly label: string
  /** Existing inline atom schema node. No new persisted shape is introduced. */
  readonly nodeType: string
  /** Pure synchronous projection; return null for unrecognized content. */
  read(node: MarkdownReadonlyJSONContent): MarkdownReferenceValue | null
  /** Pure local URL policy. No requests; host owns origin and route recognition. */
  matchUrl?(url: string): boolean
  /** Explicit user choice only. null means unavailable; errors preserve the original link. */
  resolveUrl?(url: string, context: { readonly signal: AbortSignal }): Promise<JSONContent | null>
  /** Targets by name for the link field (a page by its title); `detail` is display only. */
  search?(query: string, context: { readonly signal: AbortSignal }): Promise<readonly (MarkdownReferenceValue & { readonly detail?: string })[]>
}

export interface MarkdownPluginInstance {
  readonly references?: readonly MarkdownReferenceProvider[]
  readonly extensions?: readonly AnyExtension[]
  readonly commands?: readonly MarkdownPluginCommandDefinition[]
  readonly actions?: {
    readonly slash?: readonly MarkdownAction[]
    readonly toolbar?: readonly MarkdownAction[]
    /** Offered in the formatting bubble over a text selection, after the block tools (quote, code block). */
    readonly selection?: readonly MarkdownAction[]
  }
  readonly panels?: readonly MarkdownPluginPanel[]
  /** Extensions supply serializers. Without this check, using custom content is conservatively marked lossy. */
  readonly markdownLossy?: (document: Node, options?: MarkdownLossyOptions) => boolean
  /** Called once per engine, with cleanup on destruction. */
  readonly onCreate?: (context: MarkdownPluginContext) => void | (() => void)
  /** Called for each attached view, with cleanup before detaching that view. */
  readonly onAttach?: (context: MarkdownPluginContext) => void | (() => void)
  /** Enhance a static read-only rendering without depending on Vue or an editor view. */
  readonly onRender?: (context: MarkdownPluginRenderContext) => void | (() => void)
  /** Rows of the Ctrl+Space / Alt+Enter menu („Co s tím jde dělat“) for the caret or selection of an editable editor. */
  readonly intentions?: MarkdownIntentionProvider
}
export interface MarkdownPluginMigration {
  readonly from: number
  readonly to: number
  /** Pure, synchronous document transform. The runner clones input and validates the final schema. */
  readonly migrate: (document: Readonly<import('@tiptap/core').JSONContent>) => import('@tiptap/core').JSONContent
}
/** A kind of entity shown as an inline chip; declare it with defineMarkdownEntityKind. */
export interface MarkdownEntityKind {
  /** `person`, `issue`, `page`…: unique among the plugins of an editor. */
  readonly kind: string
  /** The plugin's inline atom node that shows one entity in text. */
  readonly inline: string
  /** How plain text becomes this kind; an id is unique among the plugins of an editor. */
  readonly recognizers?: readonly MarkdownEntityRecognizer[]
  /** Regular expression sources by name; a name is unique among the plugins of an editor. */
  readonly patterns?: Readonly<Record<string, string>>
}
export interface MarkdownEntityRecognizer {
  readonly id: string
  /** The name of one of the kind's `patterns` the text matches. */
  readonly pattern: string
}
export interface MarkdownPlugin {
  readonly id: string
  readonly apiVersion: 1
  /** The kinds of entity the plugin's inline atoms show: every inline atom of a plugin is the chip of one. */
  readonly entities?: readonly MarkdownEntityKind[]
  /** Required for a plugin that contributes document nodes, marks or attributes. */
  readonly schemaVersion?: number
  /** Ordered one-version steps used before a persisted rich value is validated by the new schema. */
  readonly migrations?: readonly MarkdownPluginMigration[]
  readonly requires?: readonly string[]
  /** Content plugins must explicitly include markdown-safe and guarantee its Markdown round-trip. */
  readonly profiles?: readonly MarkdownProfile[]
  /** Allocate mutable state here; the definition can be reused by multiple editors. */
  readonly create: (context: { readonly profile: MarkdownProfile; readonly signal: AbortSignal }) => MarkdownPluginInstance
}
export interface MarkdownSchemaPlugin {
  readonly id: string
  readonly version: number
}
