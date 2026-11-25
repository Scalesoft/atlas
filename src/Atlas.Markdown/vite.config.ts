import { basename, isAbsolute, resolve } from 'node:path'
import { existsSync, readFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { defineConfig, minify, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'

// Monaco's modules import their own `.css`. Library mode would extract those into a stylesheet that the
// lazy chunk never loads, so it used to be folded into the global style.css (~230 KB on every page).
// Turn each import into `registerMonacoStyle(css)` instead: the CSS then ships inside the lazy source
// editor chunk and is injected when an editor mounts (src/source/monaco/styles.ts).
// Library ES output keeps whitespace so hosts can tree-shake our entries; a Monaco chunk is a lazy,
// self-contained blob with nothing to shake, so it also ships without whitespace and comments (-17% raw).
function lazyMonaco(): Plugin {
  const prefix = '\0lazy-monaco-style:'
  const registry = resolve(import.meta.dirname, 'src/source/monaco/styles.ts')
  return {
    name: 'lazy-monaco',
    enforce: 'pre',
    async resolveId(source, importer) {
      if (!importer || !/\.css$/.test(source) || !/[\\/]monaco-editor[\\/]esm[\\/]/.test(importer)) return null
      const resolved = await this.resolve(source, importer, { skipSelf: true })
      // The `.js` suffix keeps Vite's CSS pipeline from claiming the virtual module by its extension.
      return resolved ? `${prefix}${resolved.id}.js` : null
    },
    load(id) {
      if (!id.startsWith(prefix)) return null
      const file = id.slice(prefix.length, -'.js'.length)
      return `import css from ${JSON.stringify(`${file}?inline`)}\nimport { registerMonacoStyle } from ${JSON.stringify(registry)}\nregisterMonacoStyle(css)\n`
    },
    // After the library minifier, which reprints whitespace: only the final code can be compacted.
    async generateBundle(_options, bundle) {
      for (const chunk of Object.values(bundle)) {
        if (chunk.type !== 'chunk' || !chunk.moduleIds.some(id => /[\\/]monaco-editor[\\/]esm[\\/]/.test(id))) continue
        const result = await minify(chunk.fileName, chunk.code, { compress: false, mangle: false, codegen: { removeWhitespace: true } })
        if (result.errors.length) this.error(`Minifying ${chunk.fileName}: ${result.errors.map(error => error.message).join('; ')}`)
        // The minifier prints `{type:`module`}`; a host's Vite rebundles the workers and accepts only a string literal there.
        chunk.code = result.code.replaceAll('type:`module`', 'type:"module"')
      }
    },
  }
}

// One Monaco chunk for every source view: the generic editor (src/source/monaco/*) and all of Monaco's
// own modules land in `sourceEditor-*.js`, so the YAML and Markdown views share it byte for byte and
// view setup chunks (the OpenAPI schema, monaco-yaml, JSON mode) only import it. Without the group the
// bundler would split Monaco's core into a shared chunk behind a second, sequential request. Monaco's
// JSON language stays out: it belongs to the OpenAPI setup chunk and its lazy `jsonMode`.
const sourceEditorModule = (id: string) => /[\\/]src[\\/]source[\\/]monaco[\\/]/.test(id)
  || (/[\\/]monaco-editor[\\/]esm[\\/]/.test(id) && !/[\\/]monaco-editor[\\/]esm[\\/]vs[\\/]language[\\/]json[\\/]/.test(id))

const entries: Record<string, string> = {
  'atlas-markdown': resolve(import.meta.dirname, 'src/index.ts'),
  core: resolve(import.meta.dirname, 'src/core/index.ts'),
  // The collaborator palette alone (a few hundred bytes), for a host's first screen that must not load the editor.
  'core/userColor': resolve(import.meta.dirname, 'src/core/userColor.ts'),
  vue: resolve(import.meta.dirname, 'src/vue/index.ts'),
  icons: resolve(import.meta.dirname, 'src/icons/index.ts'),
  match: resolve(import.meta.dirname, 'src/match/index.ts'),
  concealment: resolve(import.meta.dirname, 'src/concealment/index.ts'),
  // The optional assist module (docs/assist.md): its own entry and stylesheet, never imported by another entry.
  assist: resolve(import.meta.dirname, 'src/assist/index.ts'),
  source: resolve(import.meta.dirname, 'src/source/index.ts'),
  // describeMarkdownSchema for build tools and servers (docs/document-validation.md); editors never load it.
  schema: resolve(import.meta.dirname, 'src/schema/index.ts'),
  'plugins/youtrack': resolve(import.meta.dirname, 'src/plugins/youtrack/index.ts'),
  ...Object.fromEntries(['contacts', 'secrets', 'concealed', 'attachments', 'charts', 'openapi', 'diagrams', 'math', 'toc', 'emoji', 'pages', 'editor-tools', 'heading-anchors'].map(name => [
    `plugins/${name}`, resolve(import.meta.dirname, `src/plugins/${name}/index.ts`),
  ])),
  'collaboration/signalr': resolve(import.meta.dirname, 'src/collaboration/signalr.ts'),
  'collaboration/recovery-panel': resolve(import.meta.dirname, 'src/collaboration/recovery-panel.ts'),
  'collaboration/simulator': resolve(import.meta.dirname, 'src/collaboration/simulator.ts'),
  draft: resolve(import.meta.dirname, 'src/draft/index.ts'),
  server: resolve(import.meta.dirname, 'src/server/index.ts'),
}

export default defineConfig({
  // Emit worker URLs relative to the chunk (new URL(..., import.meta.url)), never '/assets/…',
  // so consumer bundlers copy them and hosts under a sub-path (e.g. /atlas/) load them.
  base: './',
  resolve: { alias: [
    // Scalar's self-contained browser IIFE (sets window.Scalar). Library mode would inline `?url`
    // assets as data: URLs; `no-inline` emits a separate hashed file loaded only by the reference frame.
    { find: /^\.\/scalar-standalone\?url$/, replacement: `${resolve(import.meta.dirname, 'node_modules/@scalar/api-reference/dist/browser/standalone.js')}?url&no-inline` },
    { find: /^\.\/monaco-editor\.worker\?worker$/, replacement: `${resolve(import.meta.dirname, 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js')}?worker` },
    { find: /^\.\/monaco-json\.worker\?worker$/, replacement: `${resolve(import.meta.dirname, 'node_modules/monaco-editor/esm/vs/language/json/json.worker.js')}?worker` },
    { find: /^\.\/monaco-yaml\.worker\?worker$/, replacement: `${resolve(import.meta.dirname, 'src/plugins/openapi/yaml.worker.ts')}?worker` },
    { find: /^monaco-editor\/esm\/vs\/(.+)$/, replacement: `${resolve(import.meta.dirname, 'node_modules/monaco-editor/esm/vs')}/$1` },
  ] },
  // Module workers: a consumer's dev server rewrites Monaco's dynamic import() by prepending an ES
  // import, which a classic worker cannot parse. Production output stays one self-contained file each.
  worker: { format: 'es' },
  plugins: [vue(), lazyMonaco(), {
    name: 'public-editor-styles',
    generateBundle(_options, bundle) {
      const read = (file: string) => readFileSync(resolve(import.meta.dirname, 'src/styles', file), 'utf8')
      const baseCss = Object.values(bundle).filter(item => item.type === 'asset' && item.fileName.endsWith('.css')
        && !item.fileName.includes('/'))
        .map(item => typeof item.source === 'string' ? item.source : new TextDecoder().decode(item.source)).join('\n')
      if (/--monaco-monospace-font|\.monaco-editor\s*\{/.test(baseCss)) this.error('Monaco CSS must stay in the lazy source editor chunk, not the global style.css')
      this.emitFile({ type: 'asset', fileName: 'atlas-markdown.css', source: baseCss })
      // Reuse the complete compiled stylesheet, including Vue scoped component rules.
      // Reassembling raw CSS here silently dropped compact, block-handle and slash-menu styles.
      this.emitFile({ type: 'asset', fileName: 'styles/base.css', source: "@import '../atlas-markdown.css';\n" })
      this.emitFile({ type: 'asset', fileName: 'styles/states.css', source: read('states.css') })
      this.emitFile({ type: 'asset', fileName: 'themes/ocean.css', source: read('ocean.css') })
      this.emitFile({ type: 'asset', fileName: 'themes/sand.css', source: read('sand.css') })
      this.emitFile({ type: 'asset', fileName: 'themes/forest.css', source: read('forest.css') })
      this.emitFile({ type: 'asset', fileName: 'themes/midnight.css', source: read('midnight.css') })
      this.emitFile({ type: 'asset', fileName: 'concealment/style.css', source: readFileSync(resolve(import.meta.dirname, 'src/concealment/style.css'), 'utf8') })
      // Like the invisible ink, the assist stylesheet is copied, never imported: a CSS import would fold it into style.css.
      this.emitFile({ type: 'asset', fileName: 'assist/style.css', source: readFileSync(resolve(import.meta.dirname, 'src/assist/assist.css'), 'utf8') })
      // Declarations of `core`, `vue`, `plugins/openapi`, … live in `<entry>/index.d.ts`, next to the
      // `<entry>.js` chunk. TypeScript resolves relative `'../core'` to `core.js` first and types it as
      // `any`, so give every such JS entry a sibling declaration that forwards to the directory.
      for (const [name, source] of Object.entries(entries)) {
        if (name === 'atlas-markdown' || basename(source) !== 'index.ts') continue
        this.emitFile({ type: 'asset', fileName: `${name}.d.ts`, source: `export * from './${basename(name)}/index';\n` })
      }
    },
  }, {
    // Vitest's fsModuleCache resets when node_modules/.pnpm/lock.yaml changes, but looks for it only above this package;
    // the workspace store is ../Atlas.Web. Without this key a changed patch (y-tiptap) kept cached imports of the old copy.
    name: 'workspace-lockfile-cache-key',
    configureVitest({ defineCacheKeyGenerator }) {
      const lock = resolve(import.meta.dirname, '../Atlas.Web/node_modules/.pnpm/lock.yaml')
      const key = existsSync(lock) ? createHash('sha256').update(readFileSync(lock)).digest('hex').slice(0, 16) : undefined
      defineCacheKeyGenerator(() => key)
    },
  } as Plugin],
  build: {
    cssCodeSplit: true,
    lib: {
      entry: entries,
      formats: ['es'],
      cssFileName: 'atlas-markdown',
    },
    rollupOptions: {
      output: {
        // Library mode names assets `[name].[ext]` (public stylesheets rely on it). The Scalar bundle is
        // content-hashed next to the worker assets so hosts can cache it immutably.
        assetFileNames: asset => asset.names.some(name => name === 'standalone.js') ? 'assets/scalar-[name]-[hash][extname]' : '[name].[ext]',
        // The protected-fence syntax (src/source/protectedFence.ts) stays out of this group: the editor
        // receives it from `./source` instead of importing it, so the session never reaches Monaco.
        codeSplitting: { groups: [{ name: 'sourceEditor', test: sourceEditorModule }] },
      },
      external: (id) => {
        const bundled = ['lowlight', 'devlop', 'dequal', 'monaco-editor', 'monaco-yaml', 'monaco-languageserver-types', 'monaco-marker-data-provider', 'monaco-types', 'monaco-worker-manager', 'path-browserify', 'prettier', 'proxy-disposable', 'vscode-languageserver-textdocument', 'vscode-languageserver-types', 'vscode-uri', 'jsonc-parser', 'marked', 'dompurify', '@vscode/l10n']
        return !bundled.some(name => id === name || id.startsWith(`${name}/`)) && !id.startsWith('.') && !isAbsolute(id) && !id.startsWith('\0')
      },
    },
  },
  // Stylesheet contract tests read sources as `?raw`; vitest otherwise replaces every CSS import with an empty module.
  // tests/ holds node:test contracts (node --test), not vitest files.
  test: { include: ['src/**/*.test.ts'], css: { include: [/\.css\?raw$/] }, setupFiles: ['./vitest.setup.ts'] },
})
