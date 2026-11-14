// Bundle budgets for the built package. Instant page load is a product requirement: Monaco (source
// editor) and Scalar (API reference) must cost nothing until a host opens them.
//
//   pnpm --filter @atlas/markdown build && node --test tests/package/bundle-budget.test.mjs
//
// Budgets guard against regressions; they are not targets. A cap is the size measured when it was set plus a fixed
// margin of 5 % (`measured`), so ordinary feature work fits without touching this file. Growing past a cap is a
// deliberate decision: find what grew (`node tests/package/bundle-report.mjs`) and cut it or move it behind `import()`;
// otherwise measure again, write the new size here and say why in the commit. Git keeps the history of every raise.
import assert from 'node:assert/strict'
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { test } from 'node:test'
import { analyzeBundle, distDirectory } from './bundle-report.mjs'

const KiB = 1024
/** The cap of a size measured in bytes (raw, gzip): that size plus the 5 % margin. */
const measured = (raw, gzip) => ({ raw: raw * 1.05, gzip: gzip * 1.05 })
// Measured on 2026-10-28 (0.18.0, the load-speed pass).
export const budgets = {
  // `.` + `./vue` + every `./plugins/*` entry: what a rich-profile host with all plugins loads up front. Raised 2026-10-28 (load
  // speed round 2) by the emoji name table (+83 KB): it replaces the 612 KB upstream catalog the host loaded with every page.
  // Measured again 2026-11-02 (Atlas 3.4): develop had used the margin (244.3 of 244.5 KB gzip); 3.4's fixes add 0.7 KB, the popover's
  // leave animation and focus return (contextToolbar), the table inserter's edge, the bubble stepping aside and the overview alias.
  // Measured again 2026-11-04 (Atlas 3.6): 755.3 (v3.5.2) -> 814.8 KiB raw (+59.5 KiB) from deliberate
  // editor features, all in the always-loaded core: the shared paste chunk +35 KiB (timed lists, chip text and preview, selecting a block
  // before Backspace/Delete/Tab, proximity and stubbed popovers, read actions, context toolbar, strings), the core chunk +11 KiB (kit and commands:
  // turn-into, timed list), the toc/outline and heading anchors +10 KiB, block runtimes (charts, diagrams, contacts) +5 KiB. No single
  // eager import was found that should be dynamic: Monaco, Scalar, Mermaid, KaTeX, ECharts and YAML stay behind import().
  richEager: measured(834372, 265075),
  // The package's global stylesheet (`./style.css`). Measured again 2026-11-03 (Atlas 3.6): 3.6-G's editor styles had used the margin
  // (+33 B gzip over), hidden while the `./server` entry reshaped the chunks and so the order of the concatenated CSS.
  globalCss: measured(170031, 29123),
  // Loaders a host imports up front, `./source` (types, skeleton and a cached `import()` of Monaco) and `./match` (the slash
  // menu's matching for a host's own pickers): fixed caps, since their absolute size matters and 5 % of 2 KiB is one line.
  sourceEntry: { raw: 4 * KiB, gzip: 2 * KiB },
  matchEntry: { raw: 4 * KiB, gzip: 2 * KiB },
  // The one shared Monaco chunk (editor, its CSS, YAML/Markdown tokenizers, protected blocks): all a
  // Markdown page source downloads.
  sourceEditor: measured(3128757, 811735),
  // Every lazy Monaco chunk together (shared editor + OpenAPI setup with monaco-yaml + JSON mode).
  monaco: measured(3195494, 832052),
  // The optional `@atlas/markdown/assist` module's own code (it shares the rest with `./core`) and its stylesheet `./assist/style.css`.
  // Raised 2026-11-04 (Atlas 3.6): 62.2 -> 66.1 KiB raw (+3.9) for the list and marker work of 3.6 (an AI rewrite keeps requirement headings
  // and dependency lines whole, the assist marker beside the grip).
  assistEntry: measured(67727, 20066),
  assistCss: measured(11695, 3472),
}
const editorWorkers = ['editor']
const openApiWorkers = ['json', 'yaml']

const report = analyzeBundle(undefined, { brotli: false })
const kib = bytes => `${(bytes / KiB).toFixed(1)} KiB`
const workerName = name => name.match(/^assets\/(.+?)\.worker-/)?.[1] ?? name
const entry = name => { const found = report.entries.find(item => item.name === name); assert.ok(found, `Missing public entry ${name}`); return found }
function withinBudget(label, actual, budget) {
  for (const metric of Object.keys(budget)) {
    assert.ok(actual[metric] <= budget[metric], `${label} ${metric} ${kib(actual[metric])} exceeds its budget of ${kib(budget[metric])}`)
  }
}

test('no Monaco or Scalar code is statically reachable from any public entry', () => {
  for (const entry of report.entries) {
    for (const name of entry.eager) {
      const chunk = report.chunk(name)
      assert.ok(!chunk.monaco, `${entry.name} statically loads Monaco code in ${name}`)
      assert.ok(!chunk.scalar, `${entry.name} statically loads Scalar code in ${name}`)
    }
    for (const asset of entry.referencedAssets) {
      assert.ok(!/\.worker-|scalar-standalone/.test(asset), `${entry.name} references ${asset} from an eager chunk`)
    }
  }
  assert.ok(report.monaco.editor, 'The lazy source editor chunk (Monaco + editor worker) must exist')
  assert.ok(!report.eagerUnion.includes(report.monaco.editor.name), 'The source editor chunk must be lazy')
  assert.equal(report.scalar.length, 1, 'Scalar ships as exactly one separate hashed asset')
})

test('worker options keep a literal type, which a host Vite needs to rebundle the workers', () => {
  // `{type:`module`}` (the minifier's quotes) failed Atlas's `vite build` and broke Monaco in its dev server (UX ED-1).
  for (const file of readdirSync(distDirectory).filter(name => name.endsWith('.js'))) {
    assert.ok(!readFileSync(join(distDirectory, file), 'utf8').includes('type:`module`'), `${file} has a template-literal worker type`)
  }
})

test('the ./source entry is a tiny loader in front of the shared Monaco chunk', () => {
  const source = entry('./source')
  withinBudget('Eager ./source JS', source.total, budgets.sourceEntry)
  assert.ok(source.lazyRoots.includes(report.monaco.editor.name), './source must load the shared source editor chunk through import()')
})

// Atlas's shell ranks its command palette with it before any editor loads (src/Atlas.Web/src/keyboard/commandMatch.ts).
test('the ./match entry loads only the matching, no editor', () => {
  withinBudget('Eager ./match JS', entry('./match').total, budgets.matchEntry)
})

test('the headless source session loads no Monaco code', () => {
  const session = report.lazy.find(item => /^session-/.test(item.name))
  assert.ok(session, 'The lazy source session chunk must exist')
  const reached = new Set()
  const visit = name => { if (reached.has(name)) return; reached.add(name); report.chunk(name).imports.forEach(visit) }
  visit(session.name)
  for (const name of reached) assert.ok(!report.chunk(name).monaco, `The source session statically loads Monaco code in ${name}`)
})

test('the secrets and concealed plugins load the invisible ink only on demand', () => {
  // 0.5.1: `./concealment` (motion, particle field, release) loads with the first discovered reference, not the page.
  const ink = entry('./concealment')
  const inkChunk = ink.eager.find(name => name === 'concealment.js')
  assert.ok(inkChunk, 'The ./concealment entry chunk must exist')
  for (const name of ['./plugins/secrets', './plugins/concealed']) {
    const plugin = entry(name)
    assert.ok(!plugin.eager.includes(inkChunk), `${name} statically loads the invisible ink`)
    assert.ok(plugin.lazyRoots.includes(inkChunk), `${name} must load the invisible ink through import()`)
  }
})

test('eager JavaScript of the root, Vue and rich-profile plugin entries stays within budget', () => {
  const rich = new Set(report.entries.filter(entry => entry.name === '.' || entry.name === './vue' || entry.name.startsWith('./plugins/')).flatMap(entry => entry.eager))
  withinBudget('Eager rich-profile JS', report.total(rich), budgets.richEager)
})

test('the emoji plugin draws from its name table and loads the upstream catalog only for a search', () => {
  const text = readFileSync(join(distDirectory, 'plugins/emoji.js'), 'utf8')
  assert.doesNotMatch(text, /from\s*["']@tiptap\/extension-emoji["']/, 'The 600 KB upstream catalog must not load with a document')
  assert.match(text, /import\(["']@tiptap\/extension-emoji["']\)/, 'The picker loads the upstream catalog through import()')
})

test('the Markdown dialect loads on demand, never with a rich-profile page', () => {
  const dialect = report.lazy.find(item => /^markdownDialect-/.test(item.name))
  assert.ok(dialect, 'The lazy Markdown dialect chunk must exist')
  assert.ok(!report.eagerUnion.includes(dialect.name), 'The Markdown dialect must load through import(), not with the page')
  for (const name of ['./plugins/charts', './plugins/contacts']) {
    assert.ok(entry(name).lazyRoots.some(chunk => /^markdown-/.test(chunk)), `${name} must load its dialect form through import()`)
  }
})

test('the global stylesheet carries no Monaco CSS and stays within budget', () => {
  const globalCss = report.css.find(item => item.name === 'atlas-markdown.css')
  assert.ok(globalCss, 'dist/atlas-markdown.css is missing')
  const text = readFileSync(join(distDirectory, 'atlas-markdown.css'), 'utf8')
  assert.doesNotMatch(text, /--monaco-monospace-font|\.monaco-editor\s*\{|codicon/, 'Monaco CSS must load with the lazy source editor, not with the page')
  for (const item of report.css) assert.ok(!/sourceEditor|monaco/i.test(item.name), `Monaco CSS must not be a package stylesheet: ${item.name}`)
  withinBudget('dist/atlas-markdown.css', globalCss, budgets.globalCss)
})

test('YAML (OpenAPI) and Markdown share one Monaco chunk; view setup stays in its own lazy chunk', () => {
  const { editor, setups } = report.monaco
  // Exactly one lazy chunk carries Monaco's core; everything else Monaco-related imports it.
  const cores = report.lazy.filter(item => /MonacoEnvironment/.test(readFileSync(join(distDirectory, item.name), 'utf8')))
  assert.deepEqual(cores.map(item => item.name), [editor.name], 'Monaco must ship as one shared source editor chunk')
  for (const item of report.monaco.chunks.filter(item => item !== editor)) {
    assert.ok(item.imports.includes(editor.name), `${item.name} must reuse the shared source editor chunk`)
  }
  // The OpenAPI plugin reaches the same editor chunk through `./source` and adds only its setup chunk.
  const openapi = entry('./plugins/openapi')
  assert.ok(openapi.lazyRoots.includes(editor.name), 'The OpenAPI source view must load the shared source editor chunk')
  const [setup, ...others] = setups
  assert.ok(setup && !others.length, 'Exactly one view setup chunk (OpenAPI) is expected')
  assert.ok(openapi.lazyRoots.includes(setup.name), 'The OpenAPI setup chunk is loaded by the OpenAPI plugin')
  assert.ok(!entry('./source').lazyRoots.includes(setup.name), 'The Markdown source must not download the OpenAPI setup')
  assert.deepEqual(setup.assets.filter(name => /\.worker-/.test(name)).map(workerName).sort(), openApiWorkers, 'JSON and YAML workers belong to the OpenAPI setup chunk')
  assert.deepEqual(setup.dynamicImports.filter(name => !/^jsonMode-/.test(name)), [], 'Only JSON mode may load after the OpenAPI setup chunk')
})

test('the lazy Monaco chunks stay slim and within budget', () => {
  const { editor, chunks } = report.monaco
  const text = readFileSync(join(distDirectory, editor.name), 'utf8')
  // Tokenizers are registered inside the editor chunk: opening it never waits for a second chunk.
  assert.deepEqual(editor.dynamicImports, [], 'Nothing may load after the shared editor chunk')
  assert.deepEqual(editor.imports.filter(name => report.chunk(name).monaco), [], 'The shared editor chunk must be self-contained')
  assert.match(text, /\.mdtxt/, 'The Markdown language must be registered')
  assert.match(text, /md-source-protected/, 'Protected blocks ship inside the shared editor chunk')
  assert.doesNotMatch(text, /jsonDefaults/, 'JSON mode belongs to the OpenAPI setup chunk, not the shared editor')
  assert.match(text, /registerMonacoStyle|data-atlas-monaco|atlasMonaco/, 'The editor chunk must inject its own CSS')
  withinBudget('Shared source editor chunk', editor, budgets.sourceEditor)
  withinBudget('Lazy Monaco', report.total(chunks.map(chunk => chunk.name)), budgets.monaco)
})

test('only the editor, JSON and YAML workers are emitted', () => {
  const workers = report.assets.filter(item => /worker/i.test(item.name)).map(item => workerName(item.name))
  assert.deepEqual([...workers].sort(), [...editorWorkers, ...openApiWorkers].sort(), 'Language workers other than editor/json/yaml (ts, css, html, …) must not be emitted')
  assert.deepEqual(report.monaco.editor.assets.filter(name => /\.worker-/.test(name)).map(workerName), editorWorkers, 'The shared editor chunk starts only the base editor worker')
  const started = report.monaco.chunks.flatMap(item => item.assets).filter(name => /\.worker-/.test(name))
  assert.deepEqual(report.monaco.workers.map(item => item.name).sort(), [...new Set(started)].sort(),
    'Every emitted worker must be started by a lazy Monaco chunk')
})

// The owner's rule for the assist module (docs/assist.md): a separate optional entry that nothing else loads.
test('no public entry other than ./assist reaches assist code, statically or through import()', () => {
  const assist = entry('./assist')
  assert.ok(report.chunk(assist.file).assist, 'The ./assist entry chunk must carry the module')
  for (const item of report.entries) {
    if (item.name === './assist') continue
    for (const name of report.reachable([item.file])) assert.ok(!report.chunk(name).assist, `${item.name} can load assist code in ${name}`)
  }
  for (const item of report.css) {
    if (item.name === 'assist/style.css') continue
    assert.doesNotMatch(readFileSync(join(distDirectory, item.name), 'utf8'), /\.md-assist/, `${item.name} must not carry assist styles`)
  }
})

test('the assist entry adds only its own chunk on top of ./core and stays within budget', () => {
  const assist = entry('./assist')
  const core = new Set(entry('./core').eager)
  const own = assist.eager.filter(name => !core.has(name))
  assert.deepEqual(own, [assist.file], 'Besides its own chunk, ./assist may load only what ./core already loads')
  assert.ok(own.every(name => report.chunk(name).assist))
  assert.deepEqual(assist.lazyRoots.filter(name => !entry('./core').lazyRoots.includes(name)), [], 'The assist module adds no lazy dependencies beyond core')
  withinBudget('Eager ./assist JS (own chunk)', report.total(own), budgets.assistEntry)
  const css = report.css.find(item => item.name === 'assist/style.css')
  assert.ok(css, 'dist/assist/style.css is missing')
  assert.match(readFileSync(join(distDirectory, 'assist/style.css'), 'utf8'), /--md-assist-time-scale/)
  withinBudget('dist/assist/style.css', css, budgets.assistCss)
})
