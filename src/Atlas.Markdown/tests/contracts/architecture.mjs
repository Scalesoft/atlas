import assert from 'node:assert/strict'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, isAbsolute, relative, resolve, sep } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const source = resolve(packageRoot, 'src')
const ts = createRequire(resolve(packageRoot, 'package.json'))('typescript')
const label = file => relative(source, file).split(sep).join('/')
const inside = (parent, file) => { const path = relative(parent, file); return path === '' || (!path.startsWith('..') && !isAbsolute(path)) }
const files = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
  const file = resolve(directory, entry.name)
  return entry.isDirectory() ? files(file) : /\.(ts|vue)$/.test(file) && !file.endsWith('.test.ts') ? [file] : []
})
function imports(file) {
  const result = []
  const parsed = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true)
  const visit = node => {
    const literal = (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) ? node.moduleSpecifier
      : ts.isCallExpression(node) && (node.expression.kind === ts.SyntaxKind.ImportKeyword || node.expression.getText(parsed) === 'require') ? node.arguments[0] : null
    if (literal && ts.isStringLiteral(literal)) result.push(literal.text)
    ts.forEachChild(node, visit)
  }
  visit(parsed)
  return result
}
// Vite rewrites a few relative asset specifiers (`./scalar-standalone?url`, `./monaco-*.worker?worker`)
// through resolve.alias in the package's own vite.config.ts. Read those exact aliases rather than copying
// them, so the checker resolves what the library build resolves and nothing more. An alias that lands in
// node_modules is a dependency import in disguise and is judged like a bare specifier; one that lands in
// src is followed like any relative source import, so the boundaries below still apply to it.
const viteConfig = (await import(pathToFileURL(resolve(packageRoot, 'vite.config.ts')).href)).default
const viteQuery = /\?(?:inline|url|raw|worker|sharedworker|no-inline)(?:&(?:inline|url|raw|worker|sharedworker|no-inline))*$/
function viteAlias(specifier) {
  const alias = viteConfig.resolve.alias.find(({ find }) => typeof find === 'string' ? specifier === find : find.test(specifier))
  return alias ? resolve(specifier.replace(alias.find, alias.replacement).replace(viteQuery, '')) : null
}
function packageOf(file) {
  const [scope, name] = file.split(/[\\/]node_modules[\\/]/).at(-1).split(/[\\/]/)
  return scope.startsWith('@') ? `${scope}/${name}` : scope
}
// Returns { file } for a module under src, or { dependency } for an aliased dependency file.
function localImport(file, specifier) {
  const aliased = viteAlias(specifier)
  if (aliased && !inside(source, aliased)) {
    assert.ok(existsSync(aliased) && /[\\/]node_modules[\\/]/.test(aliased), `Unresolved Vite alias ${label(file)} -> ${specifier}`)
    return { dependency: packageOf(aliased) }
  }
  const target = aliased ?? resolve(dirname(file), specifier.replace(viteQuery, ''))
  // A plain .js file is only ever an emitted asset (`?url`), such as the OpenAPI frame's own script.
  const found = [target, `${target}.ts`, `${target}.vue`, resolve(target, 'index.ts')].find(candidate => existsSync(candidate)
    && (/\.(ts|vue|css|json)$/.test(candidate) || /\?url\b/.test(specifier) && candidate.endsWith('.js')))
  assert.ok(found, `Unresolved source import ${label(file)} -> ${specifier}`)
  return { file: found }
}
const coreForbiddenPackage = /^(vue(?:\/|$)|@tiptap\/vue-3|@tiptap\/extension-collaboration|@microsoft\/signalr|yjs(?:\/|$)|y-protocols(?:\/|$))/

// ui/iconMarkup.ts is the one icon registry: pure geometry data plus a DOM factory, with no Vue and
// no transport. Core-side toolbars must draw from it, or a concept grows a second drawing again.
// ui/strings.ts (and its plain-data sections in ui/strings/) is the one user-visible text dictionary.
const coreVisibleUi = new Set(['ui/iconMarkup.ts', 'ui/strings.ts'])
const coreVisibleUiFile = file => coreVisibleUi.has(file) || /^ui\/strings\/[\w-]+\.ts$/.test(file)

// Follow the entire source graph so an indirect UI/transport dependency cannot bypass the core boundary.
const visited = new Set()
function inspectCore(file) {
  if (visited.has(file)) return
  visited.add(file)
  assert.ok(!file.endsWith('.css'), `Core must not import CSS: ${label(file)}`)
  // `source/` (the Monaco source editor and the headless source session) sits beside plugins and Vue.
  assert.ok(!file.endsWith('.vue') && (!/^(vue|ui|legacy|collab|collaboration|plugins|source)\//.test(label(file)) || coreVisibleUiFile(label(file))), `Core reaches an outer layer: ${label(file)}`)
  for (const specifier of imports(file)) {
    const target = specifier.startsWith('.') ? localImport(file, specifier) : { dependency: specifier }
    if (target.file) inspectCore(target.file)
    else assert.ok(!coreForbiddenPackage.test(target.dependency), `Core imports ${specifier} via ${label(file)}`)
  }
}
inspectCore(resolve(source, 'core/index.ts'))

// Reference plugins obey the same public boundary as separately distributed integrations.
// Besides core, a plugin may use `@atlas/markdown/concealment`: it is its own published package entry
// (package.json exports, vite lib entry) documented as the reusable concealed surface for hosts and the
// secrets plugin (docs/concealed-content.md). Only that entry file is public; its internals are not.
// Likewise `@atlas/markdown/source`, the shared lazy source editor (docs/page-source-editor.md): a
// plugin reaches Monaco only through its `mountSourceEditor(…, { setup })`, never `source/monaco/*`.
const pluginVisibleEntries = new Map([
  ['core/index.ts', '@atlas/markdown/core'],
  ['concealment/index.ts', '@atlas/markdown/concealment'],
  ['source/index.ts', '@atlas/markdown/source'],
])
const pluginVisibleFiles = new Set([...pluginVisibleEntries.keys()].map(entry => resolve(source, entry)))
const pluginVisibleSpecifiers = new Set(pluginVisibleEntries.values())
for (const entry of readdirSync(resolve(source, 'plugins'), { withFileTypes: true }).filter(entry => entry.isDirectory())) {
  const root = resolve(source, 'plugins', entry.name)
  for (const file of files(root)) for (const specifier of imports(file)) {
    if (specifier.startsWith('.')) {
      const { file: target, dependency } = localImport(file, specifier)
      assert.ok(dependency || inside(root, target) || pluginVisibleFiles.has(target), `Plugin bypasses public core: ${label(file)} -> ${specifier}`)
    } else if (specifier.startsWith('@atlas/markdown')) {
      assert.ok(pluginVisibleSpecifiers.has(specifier), `Plugin bypasses public core: ${label(file)} -> ${specifier}`)
    }
  }
}

// Instant page load: Monaco (source editor) and Scalar (API reference) are multi-megabyte and must cost
// nothing until opened. Following only static, value-level imports from every published Vite entry, none
// of them may be reached; they belong behind `import()`. tests/package/bundle-budget.test.mjs checks the
// built output the same way.
const lazyOnlyPackage = /^(monaco-editor|monaco-yaml|monaco-[\w-]+|@scalar\/)(?:\/|$)/
function staticImports(file) {
  const text = readFileSync(file, 'utf8')
  const scripts = file.endsWith('.vue') ? [...text.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)].map(match => match[1]) : [text]
  return scripts.flatMap(script => ts.createSourceFile(file, script, ts.ScriptTarget.Latest, true).statements.flatMap(statement => {
    if (ts.isImportDeclaration(statement) && !statement.importClause?.isTypeOnly) return [statement.moduleSpecifier.text]
    if (ts.isExportDeclaration(statement) && statement.moduleSpecifier && !statement.isTypeOnly) return [statement.moduleSpecifier.text]
    return []
  }))
}
const eagerVisited = new Map()
function inspectEager(file, chain) {
  if (eagerVisited.has(file) || file.endsWith('.css')) return
  eagerVisited.set(file, chain)
  for (const specifier of staticImports(file)) {
    const target = specifier.startsWith('.') ? localImport(file, specifier) : { dependency: specifier }
    if (target.file) inspectEager(target.file, [...chain, label(target.file)])
    else assert.ok(!lazyOnlyPackage.test(target.dependency), `${target.dependency} is statically reachable from a public entry (must stay behind import()): ${[...chain, specifier].join(' -> ')}`)
  }
}
for (const [name, entry] of Object.entries(viteConfig.build.lib.entry)) inspectEager(entry, [`${name} (${label(entry)})`])

// The optional assist module (`@atlas/markdown/assist`, docs/assist.md) is separable by the owner's decision:
// the core, `./vue`, the root entry and every plugin never import it, not even dynamically or as a type; it reaches
// the editor only through the public core entry, stays framework/transport neutral, and no other stylesheet styles it.
const assistRoot = resolve(source, 'assist')
const assistEntry = resolve(assistRoot, 'index.ts')
// Every specifier of a file, including type-only and dynamic imports; `.vue` files contribute their script blocks.
function anyImports(file) {
  if (!file.endsWith('.vue')) return imports(file)
  const text = readFileSync(file, 'utf8')
  return [...text.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)].flatMap(match => {
    const result = []
    const visit = node => {
      const literal = (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) ? node.moduleSpecifier
        : ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword ? node.arguments[0] : null
      if (literal && ts.isStringLiteral(literal)) result.push(literal.text)
      ts.forEachChild(node, visit)
    }
    visit(ts.createSourceFile(`${file}.ts`, match[1], ts.ScriptTarget.Latest, true))
    return result
  })
}
const sourceFiles = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
  const file = resolve(directory, entry.name)
  return entry.isDirectory() ? sourceFiles(file) : /\.(ts|vue|css)$/.test(file) ? [file] : []
})
const assistSpecifier = /^@atlas\/markdown\/assist(?:\/|$)/
for (const file of files(source).filter(file => !inside(assistRoot, file))) {
  for (const specifier of anyImports(file)) {
    assert.ok(!assistSpecifier.test(specifier), `Only hosts may import the assist module: ${label(file)} -> ${specifier}`)
    if (!specifier.startsWith('.')) continue
    const { file: target } = localImport(file, specifier)
    assert.ok(!target || !inside(assistRoot, target), `Only hosts may import the assist module: ${label(file)} -> ${specifier}`)
  }
}
for (const file of files(assistRoot)) {
  for (const specifier of anyImports(file)) {
    if (specifier.startsWith('.')) {
      const { file: target, dependency } = localImport(file, specifier)
      assert.ok(dependency || inside(assistRoot, target) || target === resolve(source, 'core/index.ts'), `The assist module bypasses the public core: ${label(file)} -> ${specifier}`)
    } else if (specifier.startsWith('@atlas/markdown')) {
      assert.equal(specifier, '@atlas/markdown/core', `The assist module bypasses the public core: ${label(file)} -> ${specifier}`)
    } else {
      assert.ok(!coreForbiddenPackage.test(specifier), `The assist module must stay framework and transport neutral: ${label(file)} -> ${specifier}`)
    }
  }
}
// Whole graphs, dynamic and type-only imports included: no other published entry can ever reach the module.
const assistGraph = new Set()
for (const [name, entry] of Object.entries(viteConfig.build.lib.entry)) {
  if (resolve(entry) === assistEntry) continue
  const seen = new Set()
  const walk = (file, chain) => {
    if (seen.has(file) || file.endsWith('.css')) return
    seen.add(file)
    assert.ok(!inside(assistRoot, file), `The public entry ${name} reaches the assist module: ${chain.join(' -> ')}`)
    for (const specifier of anyImports(file)) {
      if (!specifier.startsWith('.')) continue
      const { file: target } = localImport(file, specifier)
      if (target) walk(target, [...chain, label(target)])
    }
  }
  walk(resolve(entry), [`${name} (${label(resolve(entry))})`])
  seen.forEach(file => assistGraph.add(file))
}
assert.equal(resolve(viteConfig.build.lib.entry.assist ?? ''), assistEntry, 'The assist module must keep its own Vite lib entry')
// No stylesheet outside the module styles it, so the global style.css cannot carry assist rules.
for (const file of sourceFiles(source).filter(file => !inside(assistRoot, file) && /\.(css|vue)$/.test(file))) {
  assert.doesNotMatch(readFileSync(file, 'utf8'), /\.md-assist/, `Assist styles belong to assist/assist.css only: ${label(file)}`)
}

// One concept, one drawing: an `<svg>` written anywhere else is a second copy waiting to drift.
// selectionUI draws a geometric selection frame and MarkdownDocumentHistory plots a chart — neither is an icon.
const drawsSvg = new Set(['ui/iconMarkup.ts', 'icons/index.ts', 'features/drag/selectionUI.ts', 'vue/MarkdownDocumentHistory.vue'])

// D5: a dictionary word repeated as a hand-typed literal is exactly how the second wording
// (Nahrávám vs. Načítání, Smazat vs. Odstranit, Fullscreen vs. Celá obrazovka) crept back in.
// ui/strings.ts is the one place allowed to spell these out.
const bannedLiterals = ['Smazat', 'Fullscreen', 'Nahrávám']

for (const file of files(source)) {
  if (label(file) === 'engine/editorFactory.ts') continue
  const content = readFileSync(file, 'utf8')
  assert.ok(!/\bnew\s+Editor\s*\(|\buseEditor\s*\(/.test(content), `An editor was constructed outside engine/editorFactory.ts: ${label(file)}`)
  assert.ok(drawsSvg.has(label(file)) || !/<svg[\s>]/.test(content), `An icon is drawn outside the registry: ${label(file)}`)
  if (label(file) === 'ui/strings.ts') continue
  for (const literal of bannedLiterals) assert.ok(!content.includes(`'${literal}'`) && !content.includes(`"${literal}"`), `Banned literal '${literal}' outside ui/strings.ts: ${label(file)}`)
}

// Motion (Atlas 2.0, owner's prototype): styles/motion.css is the one source of the `--md-motion-*` defaults, and
// reduced motion rewrites tokens instead of switching motion off. No 0.01 ms kill switch and no `animation|transition
// … !important` under reduced motion, except the transition-property/timing guard in styles/motion.css
// (packages/markdown/src/styles/motion.test.ts checks the values and the guard itself).
const motionSource = resolve(source, 'styles/motion.css')
for (const file of sourceFiles(source).filter(file => /\.(css|vue)$/.test(file))) {
  const text = readFileSync(file, 'utf8')
  if (file !== motionSource) assert.doesNotMatch(text, /--md-(?:motion|conceal)-[\w-]+\s*:/, `Motion tokens (--md-motion-*, --md-conceal-*) are declared only in styles/motion.css: ${label(file)}`)
  assert.doesNotMatch(text, /0\.01ms/, `Reduced motion rewrites the motion tokens instead of a 0.01 ms switch-off: ${label(file)}`)
  for (const match of text.matchAll(/@media[^{]*prefers-reduced-motion:\s*reduce[^{]*\{/g)) {
    let depth = 1, index = match.index + match[0].length
    const start = index
    while (depth && index < text.length) { const char = text[index++]; if (char === '{') depth++; else if (char === '}') depth-- }
    for (const [declaration] of text.slice(start, index - 1).matchAll(/(?:animation|transition)(?:-[\w-]+)?\s*:[^;{}]*!important/g)) {
      assert.ok(file === motionSource && /^transition-(property|timing-function):/.test(declaration), `Reduced motion must not switch motion off with !important: ${label(file)}: ${declaration}`)
    }
  }
}
// No literal durations or curves in styles (E2): every transition, animation and delay reads a token of styles/motion.css.
// The allowlist is the frozen baseline of motion that is not style: it may only shrink (an entry that no longer
// occurs fails, so a converted literal must leave the list) and every entry carries its reason.
const motionLiteralAllowlist = new Map([
  ['assist/assist.css|380ms', 'keep: assist-own duration (--md-assist-morph), the morph of the module has no editor token'],
  ['assist/assist.css|360ms', 'keep: assist-own duration (--md-assist-solidify)'],
  ['concealment/style.css|180ms', 'keep: functional — the hold-progress thread follows the gesture, not a style'],
  ['ui/CollaborationNotice.vue|3.6s', 'keep: functional status indicator (O7), slow breathing, stands still under reduced motion'],
])
const motionLiteralUsed = new Set()
for (const file of sourceFiles(source).filter(file => /\.(css|vue)$/.test(file) && file !== motionSource)) {
  const text = readFileSync(file, 'utf8').replace(/\/\*[\s\S]*?\*\//g, '')
  for (const [, property, value] of text.matchAll(/(?:^|[;{\s])((?:transition|animation)(?:-[\w-]+)?|--md-assist-[\w-]+)\s*:([^;{}]*)/g)) {
    assert.doesNotMatch(value, /cubic-bezier\(/, `Motion curves come from styles/motion.css tokens: ${label(file)}: ${property}: ${value.trim()}`)
    for (const [, literal] of value.matchAll(/(?<![\w.-])((?:\d*\.)?\d+(?:ms|s))\b/g)) {
      if (/^0(?:\.0+)?m?s$/.test(literal)) continue
      const key = `${label(file)}|${literal}`
      assert.ok(motionLiteralAllowlist.has(key), `Literal duration ${literal} in ${label(file)} (${property}): use a --md-motion-* or --md-conceal-* token (styles/motion.css), or allowlist it with a reason`)
      motionLiteralUsed.add(key)
    }
  }
}
for (const key of motionLiteralAllowlist.keys()) assert.ok(motionLiteralUsed.has(key), `Stale motion-literal allowlist entry (the baseline only shrinks, remove it): ${key}`)
console.log(`PASS: ${visited.size} core modules are framework/transport neutral; plugins use public core/concealment/source entries; ${eagerVisited.size} eagerly loaded modules never reach Monaco or Scalar; none of the ${assistGraph.size} modules behind the other entries reaches the assist module, which uses only the public core; editor construction uses one factory; icons come from the registry; no stray D5 literals; motion tokens have one source and reduced motion switches nothing off; ${motionLiteralAllowlist.size} motion literals remain, all allowlisted with a reason`)
