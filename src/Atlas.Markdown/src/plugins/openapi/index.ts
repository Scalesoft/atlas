import { defineMarkdownBlockModule, type MarkdownPlugin, type MarkdownModuleNodeContext, type MarkdownPluginCommand } from '../../core'
import { openapiStrings } from './strings'
import { defaultOpenApi, lightOpenApiSummary, normalizeOpenApiShape, type OpenApiData } from './shape'

export interface OpenApiPluginOptions {
  /**
   * Optional host workspace. Return true when the host takes ownership of the detail. The context is
   * bound to the block node: its `signal` aborts only when the block is removed or replaced or the
   * editor is destroyed, never on a readonly toggle. Watch `context.onEditableChange` to end source
   * editing (YAML) while keeping the read-only API view, and re-check `editor.isEditable` and `getPos()`
   * before writing through `context.update`. `context.anchor` identifies the block; never render into it.
   */
  openDetail?: (context: MarkdownModuleNodeContext<OpenApiData>, view: 'api' | 'yaml') => boolean
  /** Scalar-compatible request proxy, supplied by the host; never stored in document JSON. */
  proxyUrl?: string
  /** Disable Try It on public or untrusted views. Defaults to true in standalone editors. */
  allowRequests?: boolean
  /** Not used by the reference view: it runs in an opaque-origin frame that a host function cannot reach, and Try It sends
   * Scalar's own requests without the host's credentials. Kept for compatibility; never stored in document JSON. */
  customFetch?: typeof fetch
  /** Display name captured on insertion or file import; never used as request identity. */
  uploaderName?: string
}

// `insertCommand` shares one command identity between the default module and every configured plugin,
// so the exported `insertOpenApi` dispatches against any `createOpenApiPlugin(options)` instance.
const definition = (options: OpenApiPluginOptions = {}, insertCommand?: MarkdownPluginCommand<OpenApiData>) => defineMarkdownBlockModule<OpenApiData>({
  id: 'openapi', name: 'openapi', stubHeight: data => 312 + (data.uploadedBy ? 30 : 0), dataValidator: 'openapi', get label() { return openapiStrings.label }, get description() { return openapiStrings.slashDescription }, icon: 'code', keywords: ['api', 'openapi', 'swagger', 'rest'], insertCommand, defaults: {
    ...defaultOpenApi,
    ...(options.uploaderName?.trim() ? { uploadedBy: options.uploaderName.trim() } : {}),
  },
  // Schema only (J8.5): the YAML parser is not part of the plugin's first load.
  normalize: normalizeOpenApiShape,
  text: data => { const summary = lightOpenApiSummary(data); return openapiStrings.lightSummary(summary.title, summary.version, data.filename) },
  html: data => { const summary = lightOpenApiSummary(data); return ['div', {}, ['strong', {}, summary.title], ['p', {}, summary.version ? `v${summary.version} · ${data.filename}` : data.filename]] },
  load: async () => (await import('./runtime')).createOpenApiRuntime(options),
})

const module = definition()
export const insertOpenApi = module.insert
export const openApiNode = module.node
export const createOpenApiPlugin = (options: OpenApiPluginOptions = {}): MarkdownPlugin => definition(options, module.insert).createPlugin()
export { defaultOpenApi, MAX_OPENAPI_SOURCE_BYTES } from './shape'
export type { OpenApiData, OpenApiMode, OpenApiSummary } from './shape'
export { normalizeOpenApiShape, lightOpenApiSummary } from './shape'
/** The full check and the summary parse the YAML: the parser loads with this call, for a host that validates or summarizes a spec. */
export const loadOpenApiContent = () => import('./data')

// Building blocks for a host-owned `openDetail` workspace. Both stay lazy: Monaco and its workers
// load on the first source-editor mount (or prefetch), the Scalar frame code and bundle on the first reference mount.
export { mountOpenApiReference, OPENAPI_REFERENCE_TIMEOUT_MS } from './reference'
export type { OpenApiReferenceOptions } from './reference'

/** A mounted Monaco source editor. `getValue()` returns the exact original source until the user edits. */
export interface OpenApiSourceEditor {
  getValue(): string
  /** Replaces the content without reporting it through `onChange`; the filename selects YAML or JSON. */
  setValue(source: string, filename?: string): void
  setFilename(filename: string): void
  focus(): void
  layout(): void
  destroy(): void
}

export interface OpenApiSourceEditorOptions {
  source: string
  /** `*.json` edits as JSON, anything else as YAML. */
  filename: string
  onChange?: (source: string) => void
}

// `mountOpenApiSourceEditor` shows a static source skeleton at once and swaps in Monaco when its chunk
// arrives. Hosts warm that chunk with `prefetchOpenApiSourceEditor()` on hover/focus of their YAML tab.
export { mountOpenApiSourceEditor, prefetchOpenApiSourceEditor } from './sourceEditorLoader'
