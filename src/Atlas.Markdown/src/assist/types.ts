import type { Editor, JSONContent } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import type { MarkdownCommand, MarkdownDispatchOptions, MarkdownIconName, MarkdownPluginController } from '../core'

/** A document range in ProseMirror positions. */
export interface MarkdownAssistRange { readonly from: number; readonly to: number }

/**
 * Why a block is analysed: `idle` after `idleMs` without typing in a block of at least `minLength`
 * characters, `leave` when the caret leaves a finished block (Enter, arrow, click), `paste` right
 * after a paste (never after Shift+paste) and `request` when Alt+Enter finds nothing to show yet.
 */
export type MarkdownAssistTrigger = 'idle' | 'leave' | 'paste' | 'request'

/** One textblock (paragraph, heading, …) handed to the sources. */
export interface MarkdownAssistBlock {
  /** Position directly before the textblock. */
  readonly pos: number
  readonly node: ProseMirrorNode
  /**
   * The block's text with exactly one character per document position: a hard break is `\n`,
   * any other inline atom (mention, inline component) is U+FFFC. Offset `i` is position `pos + 1 + i`.
   */
  readonly text: string
  readonly trigger: MarkdownAssistTrigger
  /** The pasted range inside this block (`trigger: 'paste'` only). */
  readonly pasted?: MarkdownAssistRange
  /** Document range of the text offsets `[start, end)`. */
  rangeOf(start: number, end: number): MarkdownAssistRange
}

/** What `apply` and secondary actions receive. Everything is scoped to one attempt. */
export interface MarkdownAssistApplyContext {
  readonly editor: Editor
  readonly controller: MarkdownPluginController
  /** Aborted when the editor turns readonly, detaches or is destroyed while the attempt runs. */
  readonly signal: AbortSignal
  /** The suggestion's range mapped to the current document; `null` once its text changed or vanished. */
  readonly range: MarkdownAssistRange | null
  /** `controller.dispatch` as one separate undo step, independent of typing before and after. */
  dispatch(command: MarkdownCommand, options?: MarkdownDispatchOptions): boolean
  /** Before/after preview anchored at the range (defaults to the suggestion's range). Resolves true once written. */
  propose(options: MarkdownAssistProposal): Promise<boolean>
  /** Decoration-only effects that never change the document (they respect reduced motion). */
  readonly effects: {
    /** Scrambles the characters to dots (~700 ms). The dots stay until the range changes or the attempt ends. */
    conceal(range: MarkdownAssistRange): Promise<void>
  }
}

/** `MarkdownProposalOptions` without the parts the apply context already knows. */
export type MarkdownAssistProposal = Omit<MarkdownProposalOptions, 'editor' | 'range' | 'signal' | 'reducedMotion' | 'strings'> & {
  readonly range?: MarkdownAssistRange
}

export interface MarkdownAssistSecondaryAction {
  readonly id: string
  readonly label: string
  run(context: MarkdownAssistApplyContext): void | Promise<void>
}

/**
 * A person (or other avatar) shown in the suggestion's list row instead of its icon, e.g. a contact „JN“. The marker
 * keeps `icon`. Initials are text (at most three characters); `src` must be an http(s) or relative URL without
 * credentials (anything else is ignored), is drawn with an empty `alt` and falls back to the initials, else the icon,
 * when it fails to load.
 */
export interface MarkdownAssistAvatar {
  readonly initials?: string
  readonly src?: string
}

export interface MarkdownAssistSuggestion {
  readonly informational?: false
  /** Stable for the same offer: kind + hash of the span. Dismissing it hides this id for the session. */
  readonly id: string
  /** For example `youtrack.issue`. „Nenabízet v této stránce“ suppresses the whole kind. */
  readonly kind: string
  readonly title: string
  readonly description?: string
  /** Icon of the target component from the registry. The module never draws an AI icon on its own. */
  readonly icon: MarkdownIconName
  /** Shown in the list row instead of `icon` (the marker keeps the icon). */
  readonly avatar?: MarkdownAssistAvatar
  /**
   * `warning`: a persistent marker at its block (also without the caret), the range in the warning tone
   * and a warning bar under the block (`role="status"`) with `applyLabel` and the secondary actions.
   */
  readonly tone?: 'default' | 'warning'
  /** Primary button of the warning bar (default „Použít“). */
  readonly applyLabel?: string
  readonly applyIcon?: MarkdownIconName
  /** Model output only (phase 4): adds the „AI“ tag. Rule-based suggestions never set it. */
  readonly ai?: boolean
  /** The recognised span. Apply re-validates it: mapped, not deleted and with unchanged text. */
  readonly range?: MarkdownAssistRange
  /** „Proč tento návrh?“ */
  readonly reason?: string
  /** Optional fresh host permission/policy gate. Unavailable offers disappear and abort pending apply. */
  readonly available?: () => boolean
  /** Host write. Returns true when it changed the document. Use `context.dispatch` for one undo step. */
  apply(context: MarkdownAssistApplyContext): boolean | Promise<boolean>
  /**
   * Where the caret goes after a successful apply. `keep` (default): when the caret was outside the suggestion's block
   * as the attempt began and the write took it into the block or the written range, it goes back to the author's place,
   * mapped through the write (a selection only: no document change, no history entry). Nothing happens when the host
   * already put it back or the author moved it meanwhile. `follow`: the selection stays where the write left it (a
   * write that opens a picker or hands the text over for editing). „Upravit před použitím“ in `propose` always follows.
   */
  readonly caret?: 'keep' | 'follow'
  /** Extra buttons of the warning bar, e.g. „Je to jen příklad“. */
  readonly secondary?: readonly MarkdownAssistSecondaryAction[]
}

/** An explanation tied to a recognised span when the source cannot safely offer a write. */
export interface MarkdownAssistNotice extends Omit<MarkdownAssistSuggestion, 'apply' | 'informational' | 'tone' | 'applyLabel' | 'applyIcon' | 'secondary' | 'caret' | 'ai'> {
  readonly informational: true
  readonly apply?: never
  readonly tone?: 'default'
  readonly applyLabel?: never
  readonly applyIcon?: never
  readonly secondary?: never
  readonly caret?: never
  readonly ai?: never
}

export type MarkdownAssistResult = MarkdownAssistSuggestion | MarkdownAssistNotice
export interface MarkdownAssistSource {
  readonly id: string
  /** Called with a signal that aborts when the block changes; a late result is ignored either way. */
  suggest(block: MarkdownAssistBlock, context: { readonly signal: AbortSignal }): readonly MarkdownAssistResult[] | Promise<readonly MarkdownAssistResult[]>
}

/** Every user-visible word of the module. Czech defaults; override per plugin or panel. */
export interface AssistStrings {
  /** Accessible name of the marker button. */
  marker(title: string): string
  /** Accessible name of the suggestion list. */
  listLabel: string
  /** Live announcement when the list opens. */
  count(count: number): string
  /** The list after Alt+Enter found nothing. */
  empty: string
  why: string
  suppressKind: string
  stale: string
  /** Label of the module's status panel. */
  statusLabel: string
  aiTag: string
  proposalLabel: string
  loading: string
  loadFailed: string
  retry: string
  helpful: string
  unhelpful: string
  before: string
  after: string
  apply: string
  edit: string
  /** Stops a proposal that is still being prepared. */
  cancel: string
  /** Throws a ready proposal away (P11: Použít / Zahodit). */
  discard: string
  /** Textual alternative of a word diff. */
  summary(removed: number, added: number): string
}

/**
 * What an AI action points at (P11): the selected text, the selected element, the heading's section or the block at the caret,
 * and the whole page. `label` names it in the menu („Sekce „Úvod““).
 */
export type MarkdownAssistTargetKind = 'selection' | 'element' | 'section' | 'block' | 'page'
export interface MarkdownAssistTarget extends MarkdownAssistRange {
  readonly kind: MarkdownAssistTargetKind
  readonly label: string
  /** Offers only the actions the target before it does not (the heading alone when its section is too long). */
  readonly fallback?: boolean
}

export interface AssistPluginOptions {
  readonly sources: readonly MarkdownAssistSource[]
  /**
   * Optional host-labelled AI actions: the „AI“ submenu of the selection toolbar and rows of „Co s tím jde dělat“ for every target
   * an action lists. Omitted entirely when nothing is available.
   */
  readonly selectionMenu?: {
    readonly label: string
    readonly icon?: MarkdownIconName
    readonly renderIcon?: (container: HTMLElement) => void | (() => void)
    /** The menu's own gate for a target (the selection toolbar asks with the selection). */
    available(editor: Editor, range: MarkdownAssistRange, target?: MarkdownAssistTarget): boolean
    readonly actions: readonly {
      readonly id: string
      readonly label: string
      /** A group the filter of the menu also matches, e.g. „Přepsat“. */
      readonly intent?: string
      /** What it acts on; `['selection']` by default. */
      readonly targets?: readonly MarkdownAssistTargetKind[]
      /** The action's own gate on top of the menu's. */
      available?(editor: Editor, range: MarkdownAssistRange, target: MarkdownAssistTarget): boolean
      run(context: { readonly editor: Editor; readonly range: MarkdownAssistRange; readonly signal: AbortSignal; readonly target?: MarkdownAssistTarget }): void | Promise<void>
    }[]
  }
  /** Host action in an otherwise empty document. A decoration: never part of Markdown, JSON or history. */
  readonly emptyAction?: {
    readonly label: string
    readonly renderIcon?: (container: HTMLElement) => void | (() => void)
    available(editor: Editor): boolean
    run(context: { readonly editor: Editor; readonly signal: AbortSignal }): void | Promise<void>
  }
  /** Idle time before a block is analysed. Default 1500 ms. */
  readonly idleMs?: number
  /** Minimum length of a block for the idle trigger. Default 12 characters. */
  readonly minLength?: number
  readonly strings?: Partial<AssistStrings>
  /** Reduced motion beyond `prefers-reduced-motion` (e.g. a profile setting); read on every animation. */
  readonly reducedMotion?: () => boolean
  /** First actual marker/list display of this suggestion id in this editor session. */
  onShown?(suggestion: MarkdownAssistResult): void
  onApplied?(suggestion: MarkdownAssistSuggestion): void
  onDismissed?(suggestion: MarkdownAssistResult, scope: 'suggestion' | 'kind'): void
}

export interface MarkdownProposalOptions {
  readonly editor: Editor
  readonly range: MarkdownAssistRange
  readonly title: string
  /** What the range holds now: plain text or a node (`doc` for several blocks). */
  readonly before: JSONContent | string
  /** What Použít writes into the range: a node, or a `doc` for several blocks. */
  readonly after?: JSONContent
  /** Prepared in the same panel: pending/error/retry never write. Aborted when the panel closes. */
  load?(signal: AbortSignal, range: MarkdownAssistRange): Promise<{ readonly after: JSONContent; readonly before?: JSONContent | string; readonly summary?: string }>
  /** Textual alternative of the difference, e.g. „Odebráno 2 věty, přidáno 1 věta“. Computed for text when omitted. */
  readonly summary?: string
  /** Offer „Upravit před použitím“: writes the proposal and selects it for editing. */
  readonly allowEdit?: boolean
  readonly signal?: AbortSignal
  /** Fresh host authorization, checked before preparing and before every write. */
  readonly available?: () => boolean
  /** Model output only: the „AI“ tag in the header. */
  readonly ai?: boolean
  readonly reducedMotion?: () => boolean
  readonly strings?: Partial<AssistStrings>
  /** Optional local feedback controls; the module never sends data. */
  onFeedback?(value: 'up' | 'down'): void
  /** Custom write instead of replacing the range with `after`; still one undo step. Return false to keep the panel. */
  write?(range: MarkdownAssistRange): boolean
}

export type MarkdownProposalResult = 'applied' | 'edited' | 'cancelled'
