import { computed, onScopeDispose, ref, shallowRef, toValue, watch, type ComputedRef, type MaybeRefOrGetter, type Ref } from 'vue'
import type { MarkdownPlugin, MarkdownRichValue } from '../core'
// Types only: the draft's behaviour (./sourceDraft), the `./source` entry, the session and Monaco load
// when a draft opens, so this composable costs the eager `./vue` bundle almost nothing.
import type { MarkdownSourceDiagnostic, MarkdownSourceProtectedRange } from '../source'
import type { SourceDraftController, SourceDraftState } from './sourceDraft'

/**
 * Page source draft (docs/page-source-editor.md): protected Markdown of the live page, a local draft and
 * a host-owned save. The draft never writes through the live collaborative controller; `save` is the
 * host endpoint that replaces the page like a restore from history.
 */
export type MarkdownSourceSaveFailure = 'conflict' | 'live-edits' | 'error'
export type MarkdownSourceSaveResult =
  | void
  | { readonly ok: true }
  | { readonly ok: false; readonly reason: MarkdownSourceSaveFailure; readonly message?: string }

export interface MarkdownSourceSaveContext {
  /** Aborted when the draft closes (tab closed, permission lost) before the request settles. */
  readonly signal: AbortSignal
}

export interface MarkdownSourceDraftOptions {
  /**
   * The live saved page: the draft's base. Flush your own pending live edits before calling `open()`.
   * A change while the draft is clean regenerates the text; a change while dirty marks it `stale`.
   */
  value: MaybeRefOrGetter<MarkdownRichValue>
  /** The same content plugins the document is edited with. */
  plugins?: MaybeRefOrGetter<readonly MarkdownPlugin[] | undefined>
  /** No Apply while true. Hosts close the source view when the permission is lost. */
  readonly?: MaybeRefOrGetter<boolean | undefined>
  /**
   * Saves the applied page on the server (new working revision + audit event). Resolve with
   * `{ ok: false, reason: 'conflict' }` on a version conflict (HTTP 409) and `'live-edits'` when others
   * have unsaved live edits; a thrown error with `status: 409` also counts as a conflict. After a
   * conflict, bring `value` up to date so Reload can regenerate from the current page.
   */
  save(value: MarkdownRichValue, text: string, context: MarkdownSourceSaveContext): Promise<MarkdownSourceSaveResult>
}

export type MarkdownSourceDraftStatus = 'closed' | 'loading' | 'ready' | 'error'
/** What happened last; `MarkdownSourcePanel` words it. `unavailable`: the current page could not be converted again. */
export type MarkdownSourceNoticeKind = MarkdownSourceSaveFailure | 'changed' | 'invalid' | 'saved' | 'unchanged' | 'unavailable'
export interface MarkdownSourceNotice {
  readonly kind: MarkdownSourceNoticeKind
  /** The host's own message of a failed save, when it gave one. */
  readonly detail?: string
}

export interface MarkdownSourceDraft {
  /** The draft text; bind the editor to it. */
  readonly text: Ref<string>
  /** The text generated from the base value; `dirty` compares against it. */
  readonly baseText: Readonly<Ref<string>>
  readonly protectedBlocks: Readonly<Ref<readonly MarkdownSourceProtectedRange[]>>
  /** Diagnostics of the last Apply (errors block it, warnings do not). */
  readonly diagnostics: Readonly<Ref<readonly MarkdownSourceDiagnostic[]>>
  readonly status: Readonly<Ref<MarkdownSourceDraftStatus>>
  /** The latest outcome to show above the editor. */
  readonly notice: Readonly<Ref<MarkdownSourceNotice | undefined>>
  readonly dirty: ComputedRef<boolean>
  /** The base changed (or the server said so) since the draft started; Reload regenerates it. */
  readonly stale: Readonly<Ref<boolean>>
  /** A save is in flight. */
  readonly busy: Readonly<Ref<boolean>>
  readonly readonly: ComputedRef<boolean>
  readonly canApply: ComputedRef<boolean>
  /** Increments whenever `text` is replaced from outside the editor (open, discard, reload, save). */
  readonly revision: Readonly<Ref<number>>
  /** Creates the session from the current value; resolves false when it failed or was superseded. */
  open(): Promise<boolean>
  /** Validates the draft, then calls the host `save`; resolves true once saved (or nothing changed). */
  apply(): Promise<boolean>
  /** Drops the draft; a base that changed meanwhile is regenerated. */
  discard(): void
  /** Regenerates the text from the current value, dropping the draft (confirm first when dirty). */
  reload(): Promise<boolean>
  /** Ends the session; `open()` starts again. Called automatically when the owning scope ends. */
  close(): void
}

// `MarkdownSourcePanel` reports whether the reader works in the editor (see `SourceDraftState.viewActive`).
// Internal to the package: not exported from `./vue`.
const viewActivity = new WeakMap<MarkdownSourceDraft, Ref<boolean>>()
export function markdownSourceDraftViewActivity(draft: MarkdownSourceDraft): Ref<boolean> | undefined {
  return viewActivity.get(draft)
}

export function useMarkdownSourceDraft(options: MarkdownSourceDraftOptions): MarkdownSourceDraft {
  const text = ref('')
  const baseText = ref('')
  const protectedBlocks = shallowRef<readonly MarkdownSourceProtectedRange[]>([])
  const diagnostics = shallowRef<readonly MarkdownSourceDiagnostic[]>([])
  const status = ref<MarkdownSourceDraftStatus>('closed')
  const notice = shallowRef<MarkdownSourceNotice>()
  const stale = ref(false)
  const busy = ref(false)
  const revision = ref(0)
  const staleReason = ref<'changed' | 'conflict' | 'live-edits'>()
  const viewActive = ref(false)
  const readonly = computed(() => !!toValue(options.readonly))
  const dirty = computed(() => status.value === 'ready' && text.value !== baseText.value)
  const canApply = computed(() => status.value === 'ready' && !readonly.value && !busy.value && dirty.value
    && staleReason.value !== 'changed' && staleReason.value !== 'conflict')
  const state: SourceDraftState = { text, baseText, protectedBlocks, diagnostics, status, notice, stale, busy, revision, staleReason, viewActive, dirty, canApply }

  // The behaviour (and through it the session and Monaco) loads on the first open().
  let controller: SourceDraftController | undefined
  let loading: Promise<SourceDraftController> | undefined
  let epoch = 0
  const load = () => loading ??= import('./sourceDraft').then(
    module => (controller = module.createSourceDraftController(state, options)),
    error => { loading = undefined; throw error },
  )
  async function open(): Promise<boolean> {
    if (controller) return controller.open()
    const token = epoch
    status.value = 'loading'; notice.value = undefined
    try {
      const loaded = await load()
      return token === epoch && loaded.open()
    } catch {
      if (token === epoch) status.value = 'error'
      return false
    }
  }
  function close() {
    epoch++
    if (controller) controller.close()
    else status.value = 'closed'
  }

  watch(() => toValue(options.value), () => controller?.reconcile())
  // A base that moved while the reader was in the editor: follow it once they leave, or mark the
  // draft stale as soon as they start editing on the old base.
  watch(viewActive, active => { if (!active) controller?.reconcile() })
  watch(dirty, value => { if (value) controller?.reconcile() })
  onScopeDispose(close)

  const draft: MarkdownSourceDraft = {
    text, baseText, protectedBlocks, diagnostics, status, notice, dirty, stale, busy, readonly, canApply, revision,
    open, close,
    apply: () => controller?.apply() ?? Promise.resolve(false),
    discard: () => controller?.discard(),
    reload: () => controller?.reload() ?? Promise.resolve(false),
  }
  viewActivity.set(draft, viewActive)
  return draft
}
