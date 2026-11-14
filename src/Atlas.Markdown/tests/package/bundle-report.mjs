// Size report for the built package (`dist`). Walks each public entry's *static*
// imports to find the JavaScript a host pays for on page load, then the lazy graph behind `import()`.
//
//   node tests/package/bundle-report.mjs            human-readable table
//   node tests/package/bundle-report.mjs --json     machine-readable report
//
// `analyzeBundle()` is shared with bundle-budget.test.mjs, which enforces the budgets in CI.
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, join, posix, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { brotliCompressSync, constants, gzipSync } from 'node:zlib'

const here = dirname(fileURLToPath(import.meta.url))
export const packageDirectory = resolve(here, '../..')
export const distDirectory = join(packageDirectory, 'dist')

// Library chunks keep whitespace; the compacted Monaco chunk prints backtick literals.
const staticImport = /(?:^|[;}\n])\s*(?:import|export)\s*(?:[\w$*{}\s,]+?\s*from\s*)?["'`](\.{1,2}\/[^"'`$]+)["'`]/g
const dynamicImport = /\bimport\(\s*["'`](\.{1,2}\/[^"'`$]+)["'`]\s*\)/g
const assetUrl = /new URL\(\s*["'`]([^"'`$]+)["'`]\s*,\s*import\.meta\.url\s*\)/g
// Fingerprints that survive minification: Monaco installs `MonacoEnvironment` workers and ships its
// `monaco-editor` DOM class; the Scalar standalone bundle is only ever referenced as a hashed asset.
const monacoCode = /MonacoEnvironment|["'`.\s]monaco-(?:editor|list|hover|scrollable-element|action-bar)\b|\/\/(?:#region)? [^\n]*node_modules\/monaco-(?:editor|yaml)\//
const scalarCode = /@scalar\/api-reference|\/\/(?:#region)? [^\n]*node_modules\/@scalar\//
// The optional assist module (src/assist): library chunks keep a `// <source>` marker per module (`//#region <source>`
// before Rolldown 1.2.16).
const assistCode = /\/\/(?:#region)? src\/assist\//

export function sizes(buffer, { brotli = true } = {}) {
  return {
    raw: buffer.length,
    gzip: gzipSync(buffer, { level: 9 }).length,
    brotli: brotli ? brotliCompressSync(buffer, { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } }).length : NaN,
  }
}
const sum = items => items.reduce((total, item) => ({
  raw: total.raw + item.raw, gzip: total.gzip + item.gzip, brotli: total.brotli + item.brotli,
}), { raw: 0, gzip: 0, brotli: 0 })

/** Public package entries (`exports` with an `import` condition) mapped to their dist files. */
export function publicEntries(manifest = JSON.parse(readFileSync(join(packageDirectory, 'package.json'), 'utf8'))) {
  return Object.entries(manifest.exports)
    .filter(([, target]) => typeof target === 'object' && target.import)
    .map(([name, target]) => ({ name, file: posix.normalize(target.import.replace(/^\.\/dist\//, '')) }))
}

/** `brotli: false` skips the slow quality-11 pass (budgets check raw and gzip). */
export function analyzeBundle(dist = distDirectory, { brotli = true } = {}) {
  if (!existsSync(dist)) throw new Error(`No build at ${dist}; run \`pnpm --filter @atlas/markdown build\` first`)
  const files = new Map()
  const walk = directory => readdirSync(directory, { withFileTypes: true }).forEach(entry => {
    const path = join(directory, entry.name)
    if (entry.isDirectory()) walk(path)
    else if (/\.(js|css)$/.test(entry.name)) files.set(relative(dist, path).split(sep).join('/'), path)
  })
  walk(dist)
  const cache = new Map()
  function chunk(name) {
    if (cache.has(name)) return cache.get(name)
    const path = files.get(name)
    if (!path) throw new Error(`Dist file ${name} is referenced but missing`)
    const buffer = readFileSync(path)
    const text = buffer.toString('utf8')
    const base = posix.dirname(name)
    const target = specifier => posix.normalize(posix.join(base, specifier))
    const info = {
      name, ...sizes(buffer, { brotli }),
      monaco: monacoCode.test(text), scalar: scalarCode.test(text), assist: assistCode.test(text),
      imports: name.endsWith('.js') ? [...text.matchAll(staticImport)].map(match => target(match[1])) : [],
      dynamicImports: name.endsWith('.js') ? [...text.matchAll(dynamicImport)].map(match => target(match[1])) : [],
      // Worker and asset URLs resolve against the chunk (vite `base: './'`).
      assets: name.endsWith('.js') ? [...text.matchAll(assetUrl)].map(match => target(match[1])) : [],
    }
    cache.set(name, info)
    return info
  }
  function closure(roots) {
    const seen = new Set()
    const visit = name => { if (seen.has(name)) return; seen.add(name); chunk(name).imports.forEach(visit) }
    roots.forEach(visit)
    return seen
  }
  /** Everything a root can ever load: static imports and `import()` alike. */
  function reachable(roots) {
    const seen = new Set()
    const visit = name => { if (seen.has(name)) return; seen.add(name); chunk(name).imports.forEach(visit); chunk(name).dynamicImports.forEach(visit) }
    roots.forEach(visit)
    return seen
  }
  const entries = publicEntries().map(entry => {
    const eager = closure([entry.file])
    const chunks = [...eager].map(chunk)
    return {
      ...entry, eager: [...eager], total: sum(chunks),
      referencedAssets: [...new Set(chunks.flatMap(item => item.assets))],
      lazyRoots: [...new Set(chunks.flatMap(item => item.dynamicImports))].filter(name => !eager.has(name)),
    }
  })
  const eagerUnion = new Set(entries.flatMap(entry => entry.eager))
  // Everything reachable only across `import()` from some public entry.
  const lazy = new Set()
  const queue = entries.flatMap(entry => entry.lazyRoots)
  while (queue.length) {
    const next = queue.shift()
    for (const name of closure([next])) {
      if (eagerUnion.has(name) || lazy.has(name)) continue
      lazy.add(name)
      queue.push(...chunk(name).dynamicImports)
    }
  }
  const assets = [...files.keys()].filter(name => name.startsWith('assets/')).map(name => ({ name, ...sizes(readFileSync(files.get(name)), { brotli }) }))
  const css = [...files.keys()].filter(name => name.endsWith('.css')).map(name => ({ name, ...sizes(readFileSync(files.get(name)), { brotli }) }))
  // The source editor chunk is the one Monaco chunk that owns `MonacoEnvironment` and starts the base
  // editor worker; it is shared by every source view. View setup chunks (OpenAPI: schema, monaco-yaml,
  // JSON mode) import it statically and start their own language workers. Everything the editor and the
  // setups load, statically or on demand (e.g. `jsonMode`), belongs to Monaco.
  const lazyChunks = [...lazy].map(chunk)
  const editor = lazyChunks.find(item => item.monaco && item.assets.some(asset => /editor\.worker-/.test(asset)))
  const setups = editor ? lazyChunks.filter(item => item !== editor && item.imports.includes(editor.name) && item.assets.some(asset => /\.worker-/.test(asset))) : []
  const monacoNames = new Set()
  const pendingMonaco = editor ? [editor.name, ...setups.map(item => item.name)] : []
  while (pendingMonaco.length) {
    const next = pendingMonaco.shift()
    if (monacoNames.has(next) || eagerUnion.has(next)) continue
    monacoNames.add(next)
    pendingMonaco.push(...chunk(next).imports, ...chunk(next).dynamicImports)
  }
  return {
    entries, eagerUnion: [...eagerUnion], lazy: [...lazy].map(chunk), assets, css,
    chunk, reachable, total: names => sum([...names].map(chunk)),
    monaco: {
      chunks: [...monacoNames].map(chunk),
      editor,
      setups,
      workers: assets.filter(item => /\.worker-[\w-]+\.js$/.test(item.name)),
    },
    scalar: assets.filter(item => /scalar-standalone/.test(item.name)),
  }
}

const kb = bytes => `${(bytes / 1024).toFixed(1)} KiB`
function row(label, item) { return `${label.padEnd(44)} ${kb(item.raw).padStart(12)} ${kb(item.gzip).padStart(11)} ${kb(item.brotli).padStart(11)}` }

export function formatReport(report) {
  const lines = [`${'eager JS (static imports only)'.padEnd(44)} ${'raw'.padStart(12)} ${'gzip'.padStart(11)} ${'brotli'.padStart(11)}`]
  for (const entry of report.entries) lines.push(row(`${entry.name} (${entry.eager.length} chunks)`, entry.total))
  lines.push(row('union of all public entries', report.total(report.eagerUnion)))
  lines.push('', 'lazy Monaco')
  for (const item of report.monaco.chunks) lines.push(row(item.name, item))
  lines.push(row('all Monaco chunks', report.total(report.monaco.chunks.map(item => item.name))))
  lines.push('', 'workers and hashed assets')
  for (const item of report.assets) lines.push(row(item.name, item))
  lines.push('', 'stylesheets')
  for (const item of report.css.sort((a, b) => b.raw - a.raw)) lines.push(row(item.name, item))
  return lines.join('\n')
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const report = analyzeBundle()
  if (process.argv.includes('--json')) {
    const { chunk, total, reachable, ...plain } = report
    console.log(JSON.stringify(plain, null, 2))
  } else console.log(formatReport(report))
}
