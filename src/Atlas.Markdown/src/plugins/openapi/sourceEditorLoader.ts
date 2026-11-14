import { mountSourceEditor, prefetchSourceEditor, type SourceLanguage } from '../../source'
import { markdownStrings } from '../../core'
import { openapiStrings } from './strings'
import type { OpenApiSourceEditor, OpenApiSourceEditorOptions } from './index'

// Part of the eager plugin entry: only the shared `./source` loader and a cached import of the OpenAPI
// setup chunk (schema, monaco-yaml, JSON mode). The Monaco chunk itself is the one `./source` shares with
// the Markdown page source.
/**
 * `modelScope` of every OpenAPI source editor: its models live under `file:///__atlas_openapi__/…`,
 * which the OpenAPI schema in monacoSetup.ts matches. Kept here, in the eager chunk the setup chunk
 * already depends on, so it costs no chunk of its own.
 */
export const OPENAPI_MODEL_SCOPE = 'openapi'

let pendingSetup: Promise<typeof import('./monacoSetup')> | undefined
function loadSetup(): Promise<typeof import('./monacoSetup')> {
  return pendingSetup ??= import('./monacoSetup').catch(error => { pendingSetup = undefined; throw error })
}

/**
 * Starts downloading the source editor (Monaco, its stylesheet and the OpenAPI language setup) without
 * mounting it. Call it on hover or focus of whatever opens a source view; never on page load. Returns
 * false (and does nothing) under Save-Data, like `prefetchSourceEditor`.
 */
export function prefetchOpenApiSourceEditor(): boolean {
  if (!prefetchSourceEditor()) return false
  loadSetup().catch(() => { /* A real mount reports the failure and retries. */ })
  return true
}

function languageFor(filename: string): SourceLanguage {
  return /\.json$/i.test(filename) ? 'json' : 'yaml'
}

/** Mounts the Monaco YAML/JSON source editor with OpenAPI schema assistance. Call `destroy()` when done. */
export async function mountOpenApiSourceEditor(host: HTMLElement, options: OpenApiSourceEditorOptions): Promise<OpenApiSourceEditor> {
  // Both chunks download in parallel; the editor waits for the setup before creating its model.
  const setup = loadSetup()
  setup.catch(() => { /* Reported by the mount below. */ })
  const editor = await mountSourceEditor(host, {
    language: languageFor(options.filename),
    value: options.source,
    ariaLabel: openapiStrings.sourceLabel,
    modelScope: OPENAPI_MODEL_SCOPE,
    labels: markdownStrings.source,
    onChange: options.onChange,
    setup: async (monaco, environment) => (await setup).setupOpenApiSource(monaco, environment),
  })
  return {
    getValue: () => editor.getValue(),
    setValue(source, filename) { editor.setValue(source, filename ? { language: languageFor(filename) } : undefined) },
    setFilename(filename) { editor.setLanguage(languageFor(filename)) },
    focus: () => editor.focus(),
    layout: () => editor.layout(),
    destroy: () => editor.destroy(),
  }
}
