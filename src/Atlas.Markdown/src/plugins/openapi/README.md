# OpenAPI module

The gallery also demonstrates a single in-place workspace with dynamic Document/API/YAML tabs above the document. Hosts can opt into it with the runtime-only `openDetail(context, view)` callback, returning `true` to take ownership; the default standalone dialogs remain available. The context is a `MarkdownModuleNodeContext` bound to the block node: `context.signal` aborts only when the block is removed or replaced or the editor is destroyed (not on a readonly toggle), `context.onEditableChange(listener)` reports permission changes so a host can close YAML while keeping the API view, and `context.anchor` is the block's element (identify or position against it, never render into it). Closing folds API/YAML into Document, then the last Document tab contracts and disappears while the document content stays visible. See [workspace lifecycle and prototype boundaries](../../../../../docs/markdown/openapi-detail-workspace.md).

Source editing uses `monaco-editor` 0.52.2 and `monaco-yaml` 5.5.1. Keep this pair pinned: the YAML integration requires the older module-based Monaco worker API. Coexistence with a host's other Monaco YAML configuration must be verified before integration. Both, and `@scalar/api-reference` 1.69.2, are bundled into the package build (lazy chunks and hashed files under `dist/assets/`); they are development dependencies of this repository, not consumer installs.

`@atlas/markdown/plugins/openapi` adds an optional rich-document block for self-contained OpenAPI 3.0 and 3.1 definitions. Register `createOpenApiPlugin()` in both editors and renderers that display the block. The core editor entry stays independent of Scalar. The plugin imports the `yaml` peer (`^2.9.1`) at runtime: it is optional in the manifest only because consumers that never import `plugins/openapi` do not need it; hosts using this plugin must install it.

```ts
import { createOpenApiPlugin, openApiNode, insertOpenApi } from '@atlas/markdown/plugins/openapi'

const plugin = createOpenApiPlugin({ allowRequests: true, uploaderName: 'Jana Nováková' })
const block = openApiNode({ version: 1, filename: 'nis.yaml', source: openApiYaml })
// controller.dispatch(insertOpenApi({ version: 1, filename: 'nis.yaml', source: openApiYaml }))
```

The source is stored exactly as supplied, including whitespace and comments. The title, version, operation count, schema count and author are derived at render time. The author is `info.contact.name` when present, otherwise the captured `uploadedBy`. The host's `uploaderName` is captured on the plugin's default insertion action and on file import; callers of `openApiNode` or `insertOpenApi` with explicit data can supply `uploadedBy` themselves. Source edits preserve the stored uploader. No host identity or authentication value is stored beyond this display name.

Source must fit in 512 KiB, have a valid OpenAPI 3.0/3.1 root, `info.title`, string `info.version`, `paths` and response objects for operations. YAML duplicate keys, excessive aliases, cyclic aliases and external structural `$ref` / `$dynamicRef` are rejected. Literal `$ref` values in examples and extensions are allowed. This is a bounded structural check, not full OpenAPI schema validation. Local demo definitions should keep version strings quoted in YAML.

In an editable document the card edits itself: the filename (click or Enter to rename, Esc cancels), the presentation mode (a segmented choice, applied at once) and the specification (**Nahrát soubor** or dropping a .yaml, .yml or .json onto the card) change in place, each as one undo step, after the same validation as the dialog (an error shows under the card and leaves the block unchanged). **Upravit specifikaci**, Enter on the selected block and the toolbar open the source in a maximizable Monaco dialog (or the host's `openDetail` YAML view) and keep its confirm step. YAML/JSON editing has syntax highlighting, line numbers, search, folding and local schema assistance; the bundled schema covers common OpenAPI structure, not full conformance validation. Workers load only when editing. Presentation modes use toggle buttons: `mode: 'download'` shows only the exact-source download, `'reference'` opens read-only Scalar documentation, and `'interactive'` additionally enables Try It when the host permits requests. Old blocks without `mode` behave as interactive. File import fills a local draft; Apply validates and records one undo operation, while Cancel leaves the document unchanged. Download returns the exact stored source and filename. The full Scalar reference loads only after a reader opens it and runs inside a same-origin iframe so its navigation cannot alter the host page's URL. Separate blocks own separate Scalar instances. Closing, updating or removing a block disposes its instance.

`allowRequests` defaults to true for standalone editors. A public viewer should pass `{ allowRequests: false }`; this hides Try It and rejects requests at the transport boundary. `proxyUrl` can point to a host-owned Scalar-compatible proxy when CORS prevents direct calls. The plugin does not provide a public proxy. The reference renders in a sandboxed frame with an opaque origin (no host cookies, storage or DOM), so a host `customFetch` cannot reach it and is not used; Try It requests leave without the host's credentials. No proxy setting, callback, authentication credential, request or response is saved in the document block. Scalar auth persistence and cloud agent UI are disabled. Server addresses in the OpenAPI source still govern Try It targets, so hosts should restrict requests according to their own security policy.

The gallery's `/__openapi_demo` endpoint is a local Vite fixture with synthetic data. It is available in the gallery dev and preview servers, including the usual Aspire gallery run, and is not a .NET deployment endpoint.

## Host workspace building blocks

A host that takes over `openDetail` builds its API/YAML views from the same public entry; both stay lazy:

```ts
import { mountOpenApiReference, mountOpenApiSourceEditor, type OpenApiSourceEditor } from '@atlas/markdown/plugins/openapi'

const disposeReference = mountOpenApiReference(referenceHost, data, { allowRequests: false })
const editor: OpenApiSourceEditor = await mountOpenApiSourceEditor(sourceHost, { source: data.source, filename: data.filename, onChange })
```

`mountOpenApiReference` returns a disposer. It renders its own loading state and, when the Scalar bundle cannot be loaded or started (network error, blocked by CSP, or no response within `OPENAPI_REFERENCE_TIMEOUT_MS` = 30 s), a visible alert with a retry button. It also dispatches `openapi-render-ready` and `openapi-render-error` (`detail`: message) on the host element. A late load after the timeout still renders. `mountOpenApiSourceEditor` renders a static, read-only skeleton of the source into the host at once (`role="status"`, `aria-busy`) and swaps in Monaco when its chunk and workers arrive; call `destroy()` when the view closes. Call `prefetchOpenApiSourceEditor()` on hover or focus of whatever opens the source view (a YAML tab, an "Edit" button) to warm that chunk; like `prefetchSourceEditor()` it returns `false` and does nothing under Save-Data. Never call it on page load. `MarkdownDetailWorkspace` emits `tab-intent` for this; the plugin's own block toolbar prefetches on hover/focus while the editor is editable.

### Load cost

Nothing Monaco- or Scalar-related is reachable through static imports from any public entry; `tests/contracts/architecture.mjs` (source) and `tests/package/bundle-budget.test.mjs` (built `dist`) enforce it, together with size budgets. `node tests/package/bundle-report.mjs` prints the current raw/gzip/brotli sizes.

- The source editor is the shared `@atlas/markdown/source` editor (`src/source/monaco/`): one Monaco chunk that the Markdown page source uses too. It is `editor.api` plus one explicit contribution list in `src/source/monaco/slim.ts` (editing, find, folding, bracket matching, comments, hover, suggest, marker navigation, messages, context menu, format) with the YAML and Markdown Monarch tokenizers registered in the chunk.
- The OpenAPI part is a separate lazy setup chunk (`monacoSetup.ts`), downloaded in parallel with the editor chunk: the OpenAPI schema, `monaco-yaml` with its worker and Monaco's JSON mode with its worker (`jsonMode` is a small extra chunk on the first JSON model). It reaches Monaco only through the `setup(monaco, env)` hook of `mountSourceEditor`. Other Monaco languages and the TypeScript/CSS/HTML workers are not shipped.
- Monaco's CSS (including the codicon font) travels inside the lazy chunk and is injected into the host document (and shadow root) on the first mount. It is not part of `@atlas/markdown/style.css`. `MonacoEnvironment` is owned by `src/source/monaco/environment.ts`, which chains any environment the host installed before.

## Content Security Policy and deployment

The reference runs in a sandboxed `srcdoc` iframe (`allow-scripts allow-same-origin allow-forms allow-downloads`). A `srcdoc` document inherits the embedding page's CSP, so the host policy must allow:

- `script-src` for the origin serving the package assets (normally `'self'`). The iframe loads `assets/scalar-standalone-<hash>.js` (about 3.8 MB, 1 MB gzip) as an external module script; no inline script, `data:`, `blob:` or `'unsafe-eval'` is needed. The URL is resolved from the chunk's `import.meta.url`, so sub-path deployments such as `/atlas/` work. When assets are served from another origin (CDN), that origin must send CORS headers, because module scripts are fetched in CORS mode.
- `worker-src` (or `script-src` fallback) for the same origin: Monaco loads `assets/editor.worker-*.js`, `json.worker-*.js` and `yaml.worker-*.js` as module workers.
- `style-src 'unsafe-inline'`: Scalar and Monaco inject `<style>` elements and inline styles, as does the editor itself. Monaco's stylesheet and its codicon font arrive this way (a `data:` font URL, so `font-src data:` too).
- `connect-src` for Try It targets (the servers in each definition, or the host's `proxyUrl`). With `allowRequests: false` no request is sent.

Serve `assets/*-<hash>.js` with long-lived immutable caching; the names change with their content. The Scalar file is downloaded only when a reader opens a reference; the workers only when the source editor opens.

In a consumer `vite dev` server, the package's prebuilt workers and the Scalar bundle are served through Vite's transform pipeline, which may prepend ES imports to them. They are therefore module workers and a module script, which accept those imports.
