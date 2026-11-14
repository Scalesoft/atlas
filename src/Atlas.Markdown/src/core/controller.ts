import type { Editor, AnyExtension, JSONContent } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import { MarkdownDocumentCodec } from '../engine/documentCodec'
import { loadMarkdownDialect } from '../engine/markdownDialectLoader'
import { destroyEditor, initializationFailure } from '../engine/editorLifecycle'
import { createTiptapEditor } from '../engine/editorFactory'
import { OutlineHighlight } from '../engine/outlineHighlight'
import { DeferredScrollToSelection } from '../engine/deferredScroll'
import { registerEditorProjection } from '../engine/projections'
import { MarkdownPluginRegistry } from './plugins/registry'
import { StatePublisher } from './statePublisher'
import { registerMarkdownContentCapability } from './contentCapability'
import { clonePlainData, deepFreezePlainData } from './plainData'
import { createRuntimeDocumentDataValidator } from '../engine/runtimeDocumentData'
import { validateBuiltinDocument } from '../engine/builtinDocumentValidation'
import { storedDocumentJson } from '../engine/storedDocument'
import type { MarkdownResourcePolicy } from '../features/images/resourcePolicy'
import type { ImageUploadService } from '../features/images/uploadService'
import { dispatchCommand, queryCommand } from './commands'
import { builtinSchemaRevision } from './documentConstraints'
import { claimCollaborationAdapter, extensionsForController, registerControllerEngine, type CollaborationSession, type CollaborationUpdate } from './internal'
import type {
  CommandState, LocalControllerOptions, CollaborativeControllerOptions, MarkdownControllerOptions, MarkdownEditorController, LocalMarkdownEditorController, MarkdownCommand,
  MarkdownDispatchOptions, MarkdownDocumentSchema, MarkdownEditorState, MarkdownProfile, MarkdownProjection, MarkdownValue, MarkdownValueInput, MarkdownContentStamp, MarkdownValueSnapshot, MarkdownContentChange,
} from './types'
import { RICH_FORMAT } from './types'

let nextSession = 0
const sessionPrefix = globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`

export interface LegacyEditorControllerOptions {
  readonly profile: MarkdownProfile
  readonly initialMarkdown: string
  readonly collaborative?: boolean
  readonly extraExtensions?: readonly AnyExtension[]
  readonly codeBlock?: AnyExtension
  readonly editable?: boolean
  readonly placeholder?: string
  readonly imageUploadUrl?: string
  readonly imageUpload?: ImageUploadService
  readonly resourcePolicy?: MarkdownResourcePolicy
}

class Controller<P extends MarkdownProfile> implements MarkdownEditorController<P> {
  readonly profile: P
  readonly schema: MarkdownDocumentSchema
  private readonly plugins: MarkdownPluginRegistry
  private readonly codec: MarkdownDocumentCodec<P>
  private editor: Editor | null = null
  private host: HTMLElement | null = null
  private destroyed = false
  private value: MarkdownValue<P>
  private pendingValue = false
  private projection: MarkdownProjection | null = null
  private readonly publisher: StatePublisher<MarkdownEditorState>
  private sessionFactory: ReturnType<typeof claimCollaborationAdapter> | null = null
  private session: CollaborationSession | null = null
  private hydrated = false
  /** The view turned editable for the server's document landing; until it is synced or fails, it stays so. */
  private hydrating = false
  /**
   * attachView is building the view: a session hydrated at once (a page bundle's state) must not report `ready` yet, or the sync
   * plugin's first render of the shared document into the new view counted as a local edit and dirtied every opened page.
   */
  private attaching = false
  private sessionWritable = false
  private requestedEditable: boolean
  private pendingLegacyMarkdown: string | undefined
  private readonly contentSessionId = `${sessionPrefix}:${++nextSession}`
  private readonly validateRuntimeData = createRuntimeDocumentDataValidator()
  private localFailure: MarkdownEditorState['error'] = null

  constructor(options: MarkdownControllerOptions<P>, private readonly legacy?: LegacyEditorControllerOptions) {
    if (!['markdown-safe', 'rich'].includes(options.profile) || !['local', 'collaborative'].includes(options.source?.kind)) {
      throw new TypeError('Invalid editor configuration or content source.')
    }
    // An editor parses and writes Markdown (paste, source view, projections): its dialect loads alongside, never blocking.
    loadMarkdownDialect().catch(() => {})
    const collaborative = options.source.kind === 'collaborative'
    const externallyCollaborative = legacy?.collaborative ?? false
    if ((collaborative && 'value' in options.source) || (!collaborative && 'adapter' in options.source)) {
      throw new TypeError('Local value and collaboration adapter are conflicting content sources.')
    }
    this.profile = options.profile
    this.plugins = new MarkdownPluginRegistry(options.plugins ?? [], this.profile)
    try {
      this.codec = new MarkdownDocumentCodec(this.profile, this.plugins, {
        profile: this.profile, placeholder: options.placeholder, collaborative: collaborative || externallyCollaborative,
        imageUpload: options.imageUpload, imageUploadUrl: legacy?.imageUploadUrl, resourcePolicy: options.resourcePolicy ?? legacy?.resourcePolicy,
        codeBlock: options.codeBlock ?? legacy?.codeBlock, onMarkdownImport: options.onMarkdownImport,
        resolveWikiLink: options.resolveWikiLink, resolveEmbed: options.resolveEmbed, lexicon: options.lexicon,
        extraExtensions: [...(legacy?.extraExtensions ?? [])],
      })
      this.value = this.codec.normalize(options.source.kind === 'local' ? options.source.value
        : options.profile === 'markdown-safe' ? '' : { format: RICH_FORMAT, version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } })
      if (options.source.kind === 'collaborative') this.sessionFactory = claimCollaborationAdapter(options.source.adapter)
    } catch (error) {
      const cleanupErrors: unknown[] = []
      try { this.plugins.destroy() } catch (cleanupError) { cleanupErrors.push(cleanupError) }
      throw initializationFailure(error, cleanupErrors)
    }
    this.schema = Object.freeze({ version: builtinSchemaRevision(this.profile), profile: this.profile, plugins: this.plugins.schemaPlugins })
    this.requestedEditable = options.editable ?? true
    this.pendingLegacyMarkdown = legacy && !externallyCollaborative ? legacy.initialMarkdown : undefined
    this.publisher = new StatePublisher<MarkdownEditorState>(Object.freeze({
      contentRevision: 0, changeOrigin: 'local', lifecycle: 'idle', binding: collaborative || externallyCollaborative ? 'collaborative' : 'local', editable: !collaborative && this.requestedEditable,
      focused: false, connection: collaborative ? 'offline' : 'local', documentSave: collaborative ? 'pending' : 'local',
      projectionSave: collaborative ? 'pending' : 'local', pendingChanges: 0, error: null, peers: Object.freeze([]),
      link: null,
    }), options.onSubscriberError)
    registerControllerEngine(this, () => this.editor)
    registerMarkdownContentCapability(this, Object.freeze({
      getStamp: () => this.contentStamp(),
      getSnapshot: () => this.valueSnapshot(),
      subscribe: (listener: (change: MarkdownContentChange) => void) => {
        let revision = this.state.contentRevision
        return this.subscribe(state => {
          if (state.contentRevision === revision) return
          revision = state.contentRevision
          if (state.lifecycle !== 'failed' && state.lifecycle !== 'destroyed') {
            listener(Object.freeze({ sessionId: this.contentSessionId, revision, origin: state.changeOrigin }))
          }
        })
      },
    }))
  }

  private get snapshot(): MarkdownEditorState { return this.publisher.value }
  get state(): MarkdownEditorState { return this.publisher.value }
  get actions() { return this.plugins.actions }
  get panels() { return this.plugins.panels }

  private setViewEditable(editable: boolean): void {
    if (!this.editor) return
    // A flip restyles the whole document (contenteditable, 321 ms on a big page at 4x CPU); each collaboration update asked for what the view had.
    const editorProps = this.editor.options.editorProps
    const attributes = editorProps.attributes as Record<string, string> | undefined
    // What remains (J12.10, 2026-11-04, 148 blocks / ~1.9k elements): ~35–45 ms of a ~40–50 ms switch is Chrome itself, inside
    // setAttribute('contenteditable'): it restyles and lays out every element, as the inherited -webkit-user-modify and line-break
    // flip. `content-visibility: auto` on the page's blocks (content.css, 3.6-G) leaves it the blocks near the screen.
    if (this.editor.options.editable === editable && attributes?.['aria-readonly'] === String(!editable)) return
    // A prop flip, one view update (contract J1.2): setOptions updates the view twice (setProps, then updateState), and every update
    // runs each plugin view again (y-sync compares the whole document with Yjs). The Editable extension reads options.editable.
    const next = { ...attributes, 'aria-readonly': String(!editable) }
    this.editor.options = { ...this.editor.options, editable, editorProps: { ...editorProps, attributes: next } }
    if (!this.editor.isDestroyed) this.editor.view.setProps({ attributes: next })
  }

  private updateCollaboration = ({ hydrated, writable, hydrating, ...patch }: CollaborationUpdate): void => {
    if (this.destroyed) return
    // Turning editable redraws every node view and restyles the whole document: done on the still empty view, right before the
    // server's document lands in the same task, it costs nothing (no input can come between). State says editable only once synced.
    if (hydrating) {
      if (this.requestedEditable && !this.hydrated && !this.localFailure && this.snapshot.error?.recoverable !== false) {
        this.hydrating = true
        this.setViewEditable(true)
      }
      return
    }
    this.hydrated ||= hydrated === true
    if (writable !== undefined) this.sessionWritable = writable
    if (this.localFailure && patch.error?.recoverable !== false) {
      patch.error = this.localFailure
      patch.projectionSave = 'degraded'
      if (this.localFailure.kind === 'content') patch.documentSave = 'degraded'
    }
    const failed = patch.error?.recoverable === false || this.snapshot.error?.recoverable === false
    const editable = this.requestedEditable && this.hydrated && this.sessionWritable && !failed
    // Synced comes a moment before connected (writable): the view stays editable across that gap rather than flip twice.
    if (editable || failed || !this.requestedEditable) this.hydrating = false
    this.setViewEditable(editable || this.hydrating)
    this.publish({ ...patch, editable, lifecycle: failed ? 'failed' : this.host ? this.hydrated && !this.attaching ? 'ready' : 'starting' : 'idle' })
  }

  private publish(patch: Partial<MarkdownEditorState>, force = false): void {
    if (this.destroyed) return
    const next = { ...this.snapshot, ...patch }
    if (!force && Object.keys(next).every(key => next[key as keyof MarkdownEditorState] === this.snapshot[key as keyof MarkdownEditorState])) return
    this.publisher.replace(Object.freeze(next), true)
  }

  private assertAttached(element: HTMLElement): void {
    if (this.destroyed || this.host !== element) throw new Error('Controller was destroyed or the view attachment was cancelled.')
  }

  attachView(element: HTMLElement, options: { readonly ariaLabel: string; readonly beforeMount?: () => void }): () => void {
    if (this.destroyed) throw new Error('Controller is destroyed.')
    if (this.snapshot.error?.recoverable === false) throw new Error('The editor session failed; create a new controller.')
    if (this.host) throw new Error('Only one editor view may be attached.')
    if (!options.ariaLabel?.trim()) throw new TypeError('A non-empty aria label is required.')
    // Reserve the lease before notifying consumers. A reentrant destroy invalidates it,
    // and the assertion prevents construction from continuing with an orphaned editor.
    this.host = element
    this.attaching = true
    this.publish({ lifecycle: 'starting' })
    this.assertAttached(element)
    let changedWhileDetached = false
    let candidateSession: CollaborationSession | null = null
    try {
      if (!this.editor) {
        if (this.sessionFactory) {
          candidateSession = this.sessionFactory(this.updateCollaboration, this.schema)
          this.assertAttached(element)
          this.session = candidateSession
          candidateSession = null
        }
        const editor = createTiptapEditor({
          element: null,
          content: this.legacy?.collaborative ? undefined : this.pendingLegacyMarkdown
            ?? (typeof this.value === 'string' ? this.value : this.value.document) as string | JSONContent,
          extensions: [...this.codec.extensions, OutlineHighlight, DeferredScrollToSelection, ...extensionsForController(this), ...(this.session?.extensions ?? [])],
          editable: this.snapshot.editable,
          editorProps: { attributes: { role: 'textbox', 'aria-multiline': 'true', 'aria-label': options.ariaLabel, 'aria-readonly': String(!this.snapshot.editable) },
            // The view is built before Tiptap adds its Editable plugin: without this a read-only view is drawn editable and then flips
            // contenteditable on the whole document (a subtree style invalidation, ~15 ms per 1000 blocks).
            editable: () => this.editor?.options.editable ?? this.snapshot.editable },
          onTransaction: ({ transaction, appendedTransactions }) => {
            if (!this.editor || this.snapshot.lifecycle !== 'ready') return
            // Appended plugin transactions are part of the same accepted batch.
            // Use its captured result, not a live state that a subscriber may replace.
            const finalTransaction = appendedTransactions.at(-1) ?? transaction
            const changed = !transaction.before.eq(finalTransaction.doc)
            try { this.validateCurrentDocument(finalTransaction.doc, this.checkedBlocks) } catch { return }
            this.publish({
              contentRevision: this.snapshot.contentRevision + (changed ? 1 : 0),
              changeOrigin: changed ? this.session?.isRemoteTransaction() ? 'remote' : 'local' : this.snapshot.changeOrigin,
            }, transaction.selectionSet || transaction.storedMarksSet || this.plugins.hasCommands
              || appendedTransactions.some(appended => appended.selectionSet || appended.storedMarksSet))
          },
          onFocus: () => this.publish({ focused: true }),
          onBlur: () => this.publish({ focused: false }),
        })
        this.editor = editor
        this.assertAttached(element)
        this.codec.validateParsedDocument(editor.state.doc)
        this.pendingLegacyMarkdown = undefined
        registerEditorProjection(editor, () => this.getProjection())
        this.plugins.create(editor, this)
        this.assertAttached(element)
        options.beforeMount?.()
        this.assertAttached(element)
        editor.mount(element)
        this.assertAttached(element)
        this.plugins.attach(editor, this)
        this.assertAttached(element)
        this.session?.attachEditor(editor)
        this.assertAttached(element)
      } else {
        const previousDocument = this.editor.state.doc
        this.editor.setOptions({ editorProps: { ...this.editor.options.editorProps, attributes: { role: 'textbox', 'aria-multiline': 'true', 'aria-label': options.ariaLabel, 'aria-readonly': String(!this.snapshot.editable) } } })
        options.beforeMount?.()
        this.assertAttached(element)
        this.editor.mount(element)
        this.assertAttached(element)
        this.plugins.attach(this.editor, this)
        this.assertAttached(element)
        changedWhileDetached = !!this.session && !previousDocument.eq(this.editor.state.doc)
        this.session?.attachEditor(this.editor)
        this.assertAttached(element)
        if (this.pendingValue) {
          this.pendingValue = false
          this.replaceValue(this.value)
        }
        if (this.pendingLegacyMarkdown !== undefined) this.replaceLegacyMarkdown(this.pendingLegacyMarkdown)
      }
      this.pendingValue = false
      this.attaching = false
      this.publish({ lifecycle: this.session && !this.hydrated ? 'starting' : 'ready',
        ...(changedWhileDetached ? { contentRevision: this.snapshot.contentRevision + 1, changeOrigin: 'remote' as const } : {}),
      })
    } catch (error) {
      const cleanupErrors: unknown[] = []
      this.attaching = false
      this.host = null
      for (const cleanup of [
        () => this.plugins.destroy(),
        () => candidateSession?.destroy(),
        () => this.session?.destroy(),
      ]) {
        try { cleanup() } catch (cleanupError) { cleanupErrors.push(cleanupError) }
      }
      cleanupErrors.push(...destroyEditor(this.editor))
      this.editor = null
      this.session = null
      if (!this.destroyed) {
        this.publish({ lifecycle: 'failed', editable: false, error: Object.freeze({
          kind: 'configuration', message: error instanceof Error ? error.message : String(error), recoverable: false,
        }) })
      }
      throw initializationFailure(error, cleanupErrors)
    }
    let attached = true
    return () => {
      if (!attached || this.host !== element || this.destroyed) return
      attached = false
      const errors: unknown[] = []
      try {
        if (this.localFailure?.kind !== 'content') { this.value = this.getValue(); this.projection = this.getProjection() }
      }
      catch (error) { errors.push(error) }
      // Invalidate the lease before running third-party cleanup, which may reenter destroy().
      this.host = null
      for (const action of [() => this.plugins.detach(), () => this.session?.detachEditor(), () => this.editor?.unmount()]) {
        try { action() } catch (error) { errors.push(error) }
      }
      this.publish({ focused: false, lifecycle: this.snapshot.error?.recoverable === false ? 'failed' : 'idle' })
      if (errors.length) throw new AggregateError(errors, 'Editor view detach failed.')
    }
  }

  focus(): boolean {
    // Asked to be writable but not yet (a collaborative editor before hydration): the caret would land in a dead document.
    if (this.requestedEditable && !this.editor?.isEditable) return false
    return !this.destroyed && !!this.host && !!this.editor?.commands.focus(undefined, { scrollIntoView: false })
  }
  blur(): void {
    if (!this.destroyed && this.host) this.editor?.commands.blur()
  }
  setEditable(editable: boolean): void {
    if (this.destroyed) return
    this.requestedEditable = editable
    if (this.localFailure?.kind === 'content') return
    if (this.sessionFactory) { this.updateCollaboration({}); return }
    this.setViewEditable(editable)
    this.publish({ editable })
  }
  query(command: MarkdownCommand, options?: MarkdownDispatchOptions): CommandState {
    if (this.destroyed || !this.host || !this.editor || this.localFailure?.kind === 'content') return { enabled: false, active: false }
    return queryCommand(this.editor, this.profile, command, this.plugins, options)
  }
  dispatch(command: MarkdownCommand, options?: MarkdownDispatchOptions): boolean {
    return !this.destroyed && this.localFailure?.kind !== 'content' && !!this.host && !!this.editor && dispatchCommand(this.editor, this.profile, command, this.plugins, options)
  }
  getProjection(): MarkdownProjection {
    this.assertReadable()
    if (this.sessionFactory && !this.host && !this.destroyed) throw new Error('Attach a collaborative view before requesting its current projection.')
    if (!this.editor) {
      if (this.projection) return structuredClone(this.projection)
      throw new Error('Attach a view before requesting a projection.')
    }
    const doc = this.pendingLegacyMarkdown !== undefined ? this.codec.parseMarkdown(this.editor, this.pendingLegacyMarkdown)
      : this.pendingValue ? this.parseValue(this.value) : this.editor.state.doc
    try {
      const result = this.codec.project(this.editor, doc)
      if (this.localFailure?.kind === 'projection') {
        const ownsError = this.snapshot.error === this.localFailure
        this.localFailure = null
        if (ownsError) this.publish({ error: null, projectionSave: this.sessionFactory ? 'pending' : 'local' })
      }
      return result
    } catch (error) { this.reportReadFailure(error, 'projection'); throw error }
  }

  private contentStamp(): MarkdownContentStamp {
    return Object.freeze({ sessionId: this.contentSessionId, revision: this.state.contentRevision })
  }

  private reportReadFailure(cause: unknown, kind: 'content' | 'projection'): void {
    const error = Object.freeze({ kind, message: cause instanceof Error ? cause.message : String(cause), recoverable: kind === 'projection' })
    if (this.localFailure?.kind === 'content') return
    this.localFailure = error
    if (kind === 'content') this.setViewEditable(false)
    this.publish({ error: this.snapshot.error?.recoverable === false ? this.snapshot.error : error, projectionSave: 'degraded', ...(kind === 'content' ? {
      lifecycle: 'failed' as const, editable: false, documentSave: 'degraded' as const,
    } : {}) })
  }

  /**
   * With `checked` (a transaction): only the top-level blocks not in `checked` (the ones the transaction made) get the structural,
   * data and domain checks. Every check is local to a block's subtree and nodes are immutable, so a block once accepted stays valid;
   * walking the whole page cost ~25 ms a keystroke on the biggest real page at 4x CPU (~5000 table cells, perf 4.0). An attribute a
   * plugin changes in place in an accepted block (a ProseMirror contract breach) is caught when the value is read: `valueSnapshot`
   * checks everything.
   */
  private validateCurrentDocument(document: Node, checked?: WeakSet<Node>): void {
    try {
      if (!checked) { this.validateRuntimeData(document); document.check(); validateBuiltinDocument(document); return }
      const blocks: Node[] = []
      document.forEach(block => { if (!checked.has(block)) blocks.push(block) })
      document.type.createChecked(document.attrs, document.content, document.marks)
      const made = document.type.create(document.attrs, blocks, document.marks)
      this.validateRuntimeData(made)
      blocks.forEach(block => block.check())
      validateBuiltinDocument(made)
      blocks.forEach(block => checked.add(block))
    } catch (error) { this.reportReadFailure(error, 'content'); throw error }
  }
  private readonly checkedBlocks = new WeakSet<Node>()

  private assertReadable(): void {
    if (this.localFailure?.kind === 'content') throw new Error(this.localFailure.message)
  }

  private valueSnapshot(): MarkdownValueSnapshot<P> {
    this.assertReadable()
    if (this.sessionFactory && !this.host && !this.destroyed) throw new Error('Attach a collaborative view before requesting its current snapshot.')
    const stamp = this.contentStamp()
    if (!this.editor) return deepFreezePlainData({ ...stamp, value: clonePlainData(this.value) })
    const document = this.pendingLegacyMarkdown !== undefined ? this.codec.parseMarkdown(this.editor, this.pendingLegacyMarkdown)
      : this.pendingValue ? this.parseValue(this.value) : this.editor.state.doc
    this.validateCurrentDocument(document)
    try {
      const value = this.profile === 'rich' ? { format: RICH_FORMAT, version: 1, document: clonePlainData(storedDocumentJson(document)),
        ...(this.schema.plugins.length ? { plugins: this.schema.plugins } : {}) } as MarkdownValue<P>
        : this.codec.getValue(this.editor, document)
      return deepFreezePlainData({ ...stamp, value: clonePlainData(value) })
    } catch (error) { this.reportReadFailure(error, this.profile === 'rich' ? 'content' : 'projection'); throw error }
  }
  getMarkdownLossy(): boolean {
    this.assertReadable()
    if (this.sessionFactory && !this.host && !this.destroyed) throw new Error('Attach a collaborative view before requesting its current fidelity.')
    if (!this.editor) {
      if (this.projection) return this.projection.markdownLossy
      throw new Error('Attach a view before requesting fidelity.')
    }
    const doc = this.pendingLegacyMarkdown !== undefined ? this.codec.parseMarkdown(this.editor, this.pendingLegacyMarkdown)
      : this.pendingValue ? this.parseValue(this.value) : this.editor.state.doc
    try { return this.codec.getMarkdownLossy(doc) }
    catch (error) { this.reportReadFailure(error, 'projection'); throw error }
  }
  getValue(): MarkdownValue<P> {
    this.assertReadable()
    if (this.sessionFactory && !this.host && !this.destroyed) throw new Error('Attach a collaborative view before requesting its current value.')
    if (!this.editor || this.pendingValue) return structuredClone(this.value)
    try {
      if (this.pendingLegacyMarkdown !== undefined) return this.codec.getValue(this.editor, this.codec.parseMarkdown(this.editor, this.pendingLegacyMarkdown))
      return this.codec.getValue(this.editor, this.editor.state.doc)
    } catch (error) { this.reportReadFailure(error, this.profile === 'rich' ? 'content' : 'projection'); throw error }
  }
  protected setLocalValue(input: MarkdownValueInput<P>): void {
    if (this.destroyed) throw new Error('Controller is destroyed.')
    this.assertReadable()
    const value = this.codec.normalize(input)
    if (typeof value === 'string' ? value === this.getValue() : JSON.stringify(value) === JSON.stringify(this.getValue())) return
    // Validate the candidate before overwriting the detached recovery buffer.
    // Markdown needs the initialized parser; before first attachment it stays pending.
    const document = this.editor ? this.parseValue(value) : undefined
    this.value = structuredClone(value)
    if (!this.editor || !this.host) {
      this.pendingValue = true
      this.publish({ contentRevision: this.snapshot.contentRevision + 1 })
      return
    }
    this.replaceValue(value, document)
  }
  private replaceValue(value: MarkdownValue<P>, parsedDocument?: Node): void {
    const editor = this.editor!
    const document = parsedDocument ?? this.parseValue(value)
    if (document.eq(editor.state.doc)) return
    editor.commands.setContent(document, { emitUpdate: false, errorOnInvalidContent: true })
  }
  private parseValue(value: MarkdownValue<P>): Node {
    const editor = this.editor!
    return this.codec.parse(editor, value)
  }
  protected replaceLegacyMarkdown(markdown: string): void {
    if (!this.legacy || this.legacy.collaborative) throw new Error('Cannot replace externally collaborative Markdown.')
    if (this.destroyed) throw new Error('Controller is destroyed.')
    if (typeof markdown !== 'string') throw new TypeError('Legacy Markdown value must be a string.')
    if (!this.editor) { this.pendingLegacyMarkdown = markdown; return }
    const document = this.codec.parseMarkdown(this.editor, markdown)
    if (!this.host) { this.pendingLegacyMarkdown = markdown; return }
    this.pendingLegacyMarkdown = undefined
    if (!document.eq(this.editor.state.doc)) this.editor.commands.setContent(document, { emitUpdate: false, errorOnInvalidContent: true })
  }
  subscribe(listener: (state: MarkdownEditorState) => void): () => void {
    return this.publisher.subscribe(listener)
  }
  destroy(): void {
    if (this.destroyed) return
    // Reserve destruction before serializers or plugin fidelity checks can reenter us.
    this.destroyed = true
    const errors: unknown[] = []
    try {
      if (this.editor && this.localFailure?.kind !== 'content' && (this.host || !this.sessionFactory)) {
        this.value = this.getValue()
        this.projection = this.getProjection()
      }
    } catch (error) { errors.push(error) }
    const editor = this.editor
    const session = this.session
    this.host = null
    this.publisher.close(Object.freeze({ ...this.snapshot, lifecycle: 'destroyed', focused: false, editable: false }))
    for (const dispose of [() => this.plugins.destroy(), () => session?.destroy()]) {
      try { dispose() } catch (error) { errors.push(error) }
    }
    errors.push(...destroyEditor(editor))
    this.session = null
    this.editor = null
    if (errors.length) throw new AggregateError(errors, 'Editor cleanup failed.')
  }
}

class LocalController<P extends MarkdownProfile> extends Controller<P> implements LocalMarkdownEditorController<P> {
  setValue(value: MarkdownValueInput<P>): void { this.setLocalValue(value) }
}
class LegacyController extends Controller<MarkdownProfile> {
  replaceMarkdown(markdown: string): void { this.replaceLegacyMarkdown(markdown) }
}
/** Internal adapter only; not exported from any package entry point. */
export function createLegacyEditorController(options: LegacyEditorControllerOptions): { controller: MarkdownEditorController; replaceMarkdown: (markdown: string) => void } {
  const controller = new LegacyController({
    profile: options.profile, editable: options.editable, placeholder: options.placeholder, imageUpload: options.imageUpload, resourcePolicy: options.resourcePolicy,
    source: { kind: 'local', value: options.profile === 'markdown-safe' ? '' : {
      format: RICH_FORMAT, version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] },
    } },
  }, options)
  return { controller, replaceMarkdown: markdown => controller.replaceMarkdown(markdown) }
}
export function createMarkdownEditorController<P extends MarkdownProfile>(
  options: LocalControllerOptions<P>,
): LocalMarkdownEditorController<P>
export function createMarkdownEditorController<P extends MarkdownProfile>(
  options: CollaborativeControllerOptions<P>,
): MarkdownEditorController<P>
export function createMarkdownEditorController<P extends MarkdownProfile>(
  options: MarkdownControllerOptions<P>,
): MarkdownEditorController<P>
export function createMarkdownEditorController<P extends MarkdownProfile>(options: MarkdownControllerOptions<P>): MarkdownEditorController<P> {
  return options.source?.kind === 'local' ? new LocalController(options) : new Controller(options)
}
