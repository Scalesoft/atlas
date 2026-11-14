// `@atlas/markdown/source`: the shared source editor surface (docs/page-source-editor.md).
//
// This entry is eager for every host that imports it, so it stays tiny: own types, a skeleton and cached
// dynamic imports. Monaco, its CSS and workers load only through `import('./monaco/editor')`, the headless
// session only through `import('./session')`. No public type here mentions Monaco.
// Plugins and `./vue` may import this entry; core may not (tests/contracts/architecture.mjs).
import { showSourceSkeleton } from './skeleton'
import type {
  MarkdownSourceProtectedRange, MarkdownSourceSession, MarkdownSourceSessionOptions,
} from './session'

export type SourceLanguage = 'yaml' | 'json' | 'markdown'

/** A problem shown in the editor (squiggle, F8 navigation). Lines and columns are 1-based. */
export interface SourceDiagnostic {
  readonly line: number
  readonly column?: number
  readonly endLine?: number
  /** Exclusive; defaults to the end of `endLine`. */
  readonly endColumn?: number
  readonly message: string
  readonly severity: 'error' | 'warning' | 'info'
  readonly source?: string
}

/**
 * A block that may only be deleted or moved as a whole: an `md-protected` fence at column 0
 * (```` ```md-protected <id> ````, label line, ```` ``` ````). Only `id` is required; line hints (1-based,
 * fences included) and the label come straight from `MarkdownSourceSession.protectedBlocks`.
 */
export type SourceProtectedBlock = Pick<MarkdownSourceProtectedRange, 'id'> & Partial<Omit<MarkdownSourceProtectedRange, 'id'>>

export interface SourceDisposable { dispose(): void }

export interface SourceWorkerOptions {
  /** Let a `MonacoEnvironment` the host installed earlier answer this label first (shared labels such as `json`). */
  hostFirst?: boolean
}

export interface SourceEditorSetupEnvironment {
  /** Starts `create()` whenever Monaco asks for a worker with this label; released with the editor. */
  registerWorker(label: string, create: () => Worker, options?: SourceWorkerOptions): SourceDisposable
}

/**
 * Configures language services before the model is created. `monaco` is the editor's Monaco namespace
 * (the namespace of Monaco's `editor.api` module), typed `unknown` so the public types stay
 * Monaco-free. Load heavy setup code lazily inside the hook. The returned disposable runs on `destroy()`.
 */
export type SourceEditorSetup = (monaco: unknown, environment: SourceEditorSetupEnvironment) => SourceDisposable | void | Promise<SourceDisposable | void>

export interface SourceEditorOptions {
  language: SourceLanguage
  value: string
  ariaLabel: string
  readonly?: boolean
  /** Shown when someone tries to type into a readonly editor. */
  readOnlyMessage?: string
  /** Groups models by purpose (`file:///__atlas_<scope>__/…`) so schemas can match them. Default `source`. */
  modelScope?: string
  /** Markdown only: blocks that can be deleted or moved only as a whole. */
  protectedBlocks?: readonly SourceProtectedBlock[]
  onChange?: (value: string) => void
  /** A user edit that would change part of a protected block was refused. */
  onProtectedEditRejected?: (block: SourceProtectedBlock) => void
  /** Esc pressed while the editor has nothing of its own to dismiss (find box, suggestions, a selection…): the host may leave the view. */
  onEscape?: () => void
  setup?: SourceEditorSetup
  /** User-visible text; unset keys keep the Czech defaults. `labels: markdownStrings.source` follows the host's dictionary. */
  labels?: SourceEditorLabels
}

/**
 * Text the source editor shows. This entry stays independent of the editor dictionary (it must stay
 * tiny, and Monaco's chunk must not share modules with it), so it keeps its own defaults; the same
 * keys exist in `markdownStrings.source`.
 */
export interface SourceEditorLabels {
  /** Skeleton status once loading takes longer than ~300 ms. */
  loadingLabel?: string
  /** Alert for a refused partial edit of a protected block, given the select-block shortcut. */
  protectedEditRejectedMessage?: (shortcut: string) => string
  /** Context-menu action that selects the whole protected block. */
  selectProtectedBlockLabel?: string
  /** Hover Markdown of a protected block, given its escaped short label and shortcut. */
  protectedBlockHover?: (label: string, shortcut: string) => string
}

export interface SourceSetValueOptions {
  language?: SourceLanguage
  /** Replaces the protected set; omitted keeps the current one. `[]` turns protection off. */
  protectedBlocks?: readonly SourceProtectedBlock[]
}

/** A mounted source editor. `getValue()` returns the exact original text until the user edits. */
export interface SourceEditor {
  getValue(): string
  /** Replaces the content without reporting it through `onChange` (and resets undo). */
  setValue(value: string, options?: SourceSetValueOptions): void
  setLanguage(language: SourceLanguage): void
  /** Replaces the diagnostics of `owner` (default `atlas-source`). */
  setDiagnostics(diagnostics: readonly SourceDiagnostic[], owner?: string): void
  setReadonly(readonly: boolean, message?: string): void
  /** Scrolls to a 1-based line, places the cursor there and focuses unless `focus: false`. */
  revealLine(line: number, options?: { focus?: boolean }): void
  focus(): void
  layout(): void
  destroy(): void
}

// The only way into the Monaco chunk. The protected-fence syntax it shares with the headless session
// arrives alongside (a tiny chunk of its own; see protectedFence.ts). A failed download is retried by the next call.
type EditorModules = [typeof import('./monaco/editor'), typeof import('./protectedFence')]
let pendingEditor: Promise<EditorModules> | undefined
function loadEditor(): Promise<EditorModules> {
  return pendingEditor ??= Promise.all([import('./monaco/editor'), import('./protectedFence')])
    .catch(error => { pendingEditor = undefined; throw error })
}

/** Whether the user asked browsers to save data; prefetching is skipped then. */
function saveData(): boolean {
  return (globalThis.navigator as (Navigator & { connection?: { saveData?: boolean } }) | undefined)?.connection?.saveData === true
}

/**
 * Starts downloading the source editor (Monaco, its stylesheet and base setup) without mounting it.
 * Call it on hover or focus of whatever opens a source view (a tab's `tab-intent`, a menu item); never on
 * page load or idle. Returns false (and does nothing) under Save-Data.
 */
export function prefetchSourceEditor(): boolean {
  if (saveData()) return false
  loadEditor().catch(() => { /* A real mount reports the failure and retries. */ })
  return true
}

/**
 * Mounts a Monaco source editor into `host` (which should have its final size). A static rendering of
 * `value` shows at once and Monaco replaces it when its chunk arrives. Call `destroy()` when done.
 */
export async function mountSourceEditor(host: HTMLElement, options: SourceEditorOptions): Promise<SourceEditor> {
  const removeSkeleton = showSourceSkeleton(host, options.value, options.labels?.loadingLabel)
  try {
    const [{ mountSourceEditor: mount }, { protectedFence }] = await loadEditor()
    // The skeleton stays while a view's own setup chunk may still be arriving; it is absolutely
    // positioned, so Monaco measures the host as if it were not there.
    return await mount(host, options, protectedFence)
  } finally { removeSkeleton() }
}

// Headless Markdown source session (phase 2), loaded on first use.
export type {
  MarkdownSourceApplyResult, MarkdownSourceBlock, MarkdownSourceDiagnostic, MarkdownSourceProtectedRange,
  MarkdownSourceProtectionReason, MarkdownSourceSession, MarkdownSourceSessionOptions,
} from './session'

/** Converts a rich value into protected Markdown source; call `destroy()` when the source view closes. */
export async function createMarkdownSourceSession(options: MarkdownSourceSessionOptions): Promise<MarkdownSourceSession> {
  return (await import('./session')).openMarkdownSourceSession(options)
}
