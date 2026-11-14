// The generic Monaco source editor: the one lazy chunk behind `mountSourceEditor` in `src/source/index.ts`
// (with slim.ts, styles.ts, environment.ts and protectedBlocks.ts; vite.config.ts keeps them together).
// View-specific language services arrive through `options.setup`, loaded by the view in its own chunk.
import { monaco } from './slim'
import { installMonacoStyles } from './styles'
import { acquireMonacoEnvironment, registerWorker } from './environment'
import { attachProtectedBlocks, type ProtectedBlocksHandle } from './protectedBlocks'
import type { SourceDiagnostic, SourceDisposable, SourceEditor, SourceEditorOptions, SourceLanguage, SourceProtectedBlock } from '../index'
import type { ProtectedFenceSyntax } from '../protectedFence'

const extensions: Record<SourceLanguage, string> = { yaml: 'yaml', json: 'json', markdown: 'md' }
const severities = { error: monaco.MarkerSeverity.Error, warning: monaco.MarkerSeverity.Warning, info: monaco.MarkerSeverity.Info }
let nextModelId = 0
type SourceViewOptions = monaco.editor.IEditorOptions & monaco.editor.IGlobalEditorOptions

// Markdown needs no language worker: without word-based and quick suggestions nothing asks for
// `editorWorkerService`, so opening a page source starts no worker at all.
const markdownOptions: SourceViewOptions = {
  wordBasedSuggestions: 'off', quickSuggestions: false, suggestOnTriggerCharacters: false,
}
// Monaco's defaults, restated so a language switch away from Markdown restores them.
const codeOptions: SourceViewOptions = {
  wordBasedSuggestions: 'matchingDocuments', quickSuggestions: { other: 'on', comments: 'off', strings: 'off' },
  suggestOnTriggerCharacters: true,
}

function editorOptions(language: SourceLanguage): SourceViewOptions {
  return language === 'markdown' ? markdownOptions : codeOptions
}

/**
 * Mounts Monaco into `host`. Loaded only through `mountSourceEditor` (the skeleton lives there), which
 * also hands over the protected-fence syntax it shares with the headless session.
 */
export async function mountSourceEditor(host: HTMLElement, options: SourceEditorOptions, fence: ProtectedFenceSyntax): Promise<SourceEditor> {
  installMonacoStyles(host)
  const releaseEnvironment = acquireMonacoEnvironment()
  const owned: SourceDisposable[] = []
  let model: monaco.editor.ITextModel | undefined
  let editor: monaco.editor.IStandaloneCodeEditor | undefined
  let protection: ProtectedBlocksHandle | undefined
  let destroyed = false
  const cleanup = () => {
    protection?.dispose()
    for (const item of owned.splice(0).reverse()) { try { item.dispose() } catch { /* keep releasing the rest */ } }
    editor?.dispose(); model?.dispose(); releaseEnvironment()
  }
  try {
    const setup = await options.setup?.(monaco, {
      registerWorker(label, create, workerOptions) { const registration = registerWorker(label, create, workerOptions); owned.push(registration); return registration },
    })
    if (setup) owned.push(setup)
    let language = options.language
    const scope = (options.modelScope ?? 'source').replace(/[^\w-]/g, '_')
    const uri = monaco.Uri.parse(`file:///__atlas_${scope}__/${Date.now()}-${++nextModelId}/source.${extensions[language]}`)
    model = monaco.editor.createModel(options.value, language, uri)
    const readonlyMessage = (message?: string) => message ? { value: message } : undefined
    editor = monaco.editor.create(host, {
      model,
      ariaLabel: options.ariaLabel, accessibilitySupport: 'on',
      readOnly: options.readonly === true, readOnlyMessage: readonlyMessage(options.readOnlyMessage),
      automaticLayout: true, minimap: { enabled: false }, scrollBeyondLastLine: false,
      theme: host.ownerDocument.defaultView?.getComputedStyle(host).colorScheme.includes('dark') ? 'vs-dark' : 'vs',
      wordWrap: 'on', tabSize: 2, insertSpaces: true, glyphMargin: false,
      ...editorOptions(language),
    })
    const mountedModel = model, mountedEditor = editor
    // Esc with nothing of the editor's own to dismiss (the find box, suggestions, a selection, more cursors, a message,
    // marker navigation, a snippet, a running format) goes to the host. Monaco prevents the default of every Esc itself,
    // so a host cannot tell the two apart from outside.
    if (options.onEscape) mountedEditor.addCommand(monaco.KeyCode.Escape, () => options.onEscape!(),
      '!findWidgetVisible && !suggestWidgetVisible && !editorHasSelection && !editorHasMultipleSelections && !messageVisible && !markersNavigationVisible && !inSnippetMode && !cancellableOperation')
    let original = options.value
    let changed = false
    let programmatic = false
    const enableProtection = (blocks: readonly SourceProtectedBlock[]) => {
      protection ??= attachProtectedBlocks(monaco, mountedEditor, mountedModel, host, { fence, onRejected: block => options.onProtectedEditRejected?.(block), labels: options.labels })
      protection.set(blocks)
    }
    if (options.protectedBlocks) enableProtection(options.protectedBlocks)
    owned.push(mountedModel.onDidChangeContent(() => {
      if (programmatic) return
      changed = true
      options.onChange?.(mountedModel.getValue())
    }))
    const setLanguage = (next: SourceLanguage) => {
      if (next === language) return
      language = next
      monaco.editor.setModelLanguage(mountedModel, next)
      mountedEditor.updateOptions(editorOptions(next))
    }
    return {
      // The exact original text (line endings included) until the user edits.
      getValue: () => changed ? mountedModel.getValue() : original,
      setValue(value, valueOptions = {}) {
        if (destroyed) return
        programmatic = true
        try {
          mountedModel.setValue(value)
          original = value; changed = false
          if (valueOptions.language) setLanguage(valueOptions.language)
        } finally { programmatic = false }
        // Without new blocks the known ones stay; the content change already re-scanned them.
        if (valueOptions.protectedBlocks) enableProtection(valueOptions.protectedBlocks)
      },
      setLanguage(next) { if (!destroyed) setLanguage(next) },
      setDiagnostics(diagnostics: readonly SourceDiagnostic[], owner = 'atlas-source') {
        if (destroyed) return
        const lines = mountedModel.getLineCount()
        const clamp = (line: number) => Math.min(Math.max(Math.trunc(line) || 1, 1), lines)
        monaco.editor.setModelMarkers(mountedModel, owner, diagnostics.map(item => {
          const startLineNumber = clamp(item.line)
          const endLineNumber = clamp(item.endLine ?? item.line)
          return {
            severity: severities[item.severity] ?? monaco.MarkerSeverity.Error,
            message: item.message,
            source: item.source,
            startLineNumber, startColumn: item.column ?? 1,
            endLineNumber, endColumn: item.endColumn ?? mountedModel.getLineMaxColumn(endLineNumber),
          }
        }))
      },
      setReadonly(readonly, message) {
        if (!destroyed) mountedEditor.updateOptions({ readOnly: readonly, readOnlyMessage: readonlyMessage(message) })
      },
      revealLine(line, revealOptions = {}) {
        if (destroyed) return
        const lineNumber = Math.min(Math.max(Math.trunc(line) || 1, 1), mountedModel.getLineCount())
        mountedEditor.revealLineInCenterIfOutsideViewport(lineNumber)
        mountedEditor.setPosition({ lineNumber, column: 1 })
        if (revealOptions.focus !== false) mountedEditor.focus()
      },
      focus() { if (!destroyed) mountedEditor.focus() },
      layout() { if (!destroyed) mountedEditor.layout() },
      destroy() {
        if (destroyed) return
        destroyed = true
        cleanup()
      },
    }
  } catch (error) {
    destroyed = true
    cleanup()
    throw error
  }
}
