import assert from 'node:assert/strict'
import { test } from 'node:test'
import { readFileSync, readdirSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// Computed styles are unreliable in the editor's jsdom/happy-dom unit-test environment (custom
// properties, `:where()` and `color-mix()` are not fully resolved there), so the one-focus-ring
// and host-font-inheritance guarantees from the 2026-09-07 UI unification plan are asserted here
// against the stylesheet text instead. This is intentionally narrower than a visual regression
// test — it does not replace the census/gallery screenshot review, only stops a second focus
// ring or a stray monospace declaration from creeping back into styles/*.css.

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const stylesDir = resolve(packageRoot, 'src/styles')
const files = readdirSync(stylesDir).filter(name => name.endsWith('.css'))
const sheets = files.map(name => ({ name, text: readFileSync(resolve(stylesDir, name), 'utf8') }))

/** Splits a stylesheet into `{ selector, body }` rules, ignoring @-rules and nested `@media` bodies
 *  (a lightweight scan is enough here — we only need selector text and declaration text, not a
 *  full CSS AST). */
function rules(text) {
  const found = []
  const re = /([^{}]+)\{([^{}]*)\}/g
  let match
  while ((match = re.exec(text))) {
    const selector = match[1].trim()
    if (selector.startsWith('@') || selector.startsWith('from') || selector.startsWith('to') || /^\d+%$/.test(selector)) continue
    found.push({ selector, body: match[2] })
  }
  return found
}

test('exactly one :focus-visible rule sets outline, and it uses var(--focus-ring)', () => {
  const outlineRules = []
  for (const { name, text } of sheets) {
    for (const { selector, body } of rules(text)) {
      if (!selector.includes(':focus-visible')) continue
      if (!/(?<!-)\boutline\s*:/.test(body)) continue // ignore `outline-offset`-only or unrelated declarations
      outlineRules.push({ file: name, selector, body })
    }
  }
  assert.equal(outlineRules.length, 1,
    `Expected exactly one :focus-visible rule that sets outline, found ${outlineRules.length}:\n${outlineRules.map(r => `${r.file}: ${r.selector} { ${r.body.trim()} }`).join('\n')}`)
  assert.match(outlineRules[0].body, /outline\s*:\s*var\(--focus-ring\)/, 'The one focus-visible outline rule must use var(--focus-ring).')
})

test('no outline-offset other than var(--focus-offset)', () => {
  const offenders = []
  for (const { name, text } of sheets) {
    for (const { selector, body } of rules(text)) {
      const match = body.match(/outline-offset\s*:\s*([^;]+);?/)
      if (!match) continue
      // `var(--focus-offset)`, with or without a literal fallback for standalone (non-.md-editor) use.
      if (/^var\(--focus-offset(\s*,\s*[^)]+)?\)$/.test(match[1].trim())) continue
      offenders.push(`${name}: ${selector} { outline-offset: ${match[1].trim()} }`)
    }
  }
  assert.deepEqual(offenders, [])
})

test('no font-family with SFMono or a bare monospace fallback outside code/pre selectors', () => {
  const offenders = []
  for (const { name, text } of sheets) {
    for (const { selector, body } of rules(text)) {
      const declares = /font(-family)?\s*:[^;]*(SFMono|monospace)/i.test(body)
      if (!declares) continue
      const allowedSelector = /\b(code|pre)\b/.test(selector) || /md-source(-panel)?\s+textarea/.test(selector)
        || /collaboration-carets__label/.test(selector) // a decorative per-user cursor tag, not a control
        || /md-youtrack-issue-key|md-suggestion-list \.k\b/.test(selector) // an issue key and a shortcut read as code, like the action menu's keys
      if (!allowedSelector) offenders.push(`${name}: ${selector} { ${body.trim()} }`)
    }
  }
  assert.deepEqual(offenders, [])
})

test('no font shorthand uses a CSS-wide keyword as the family (the whole declaration would be dropped)', () => {
  // `font: 500 13px/1 inherit` is invalid: `inherit` cannot be the family in the shorthand, so
  // browsers discard the entire declaration and the control silently inherits body typography.
  const uiDir = resolve(packageRoot, 'src')
  const walk = dir => readdirSync(dir, { withFileTypes: true }).flatMap(entry => entry.isDirectory() ? walk(resolve(dir, entry.name)) : /\.(css|vue)$/.test(entry.name) ? [resolve(dir, entry.name)] : [])
  const offenders = []
  for (const file of walk(uiDir)) {
    const text = readFileSync(file, 'utf8')
    const re = /\bfont\s*:\s*([^;{}]+);/g
    let match
    while ((match = re.exec(text))) {
      const value = match[1].trim()
      if (/^(inherit|initial|unset|revert)$/.test(value)) continue // bare keyword is valid
      if (/\b(inherit|initial|unset|revert)\b/.test(value)) offenders.push(`${file.replace(packageRoot, '')}: font: ${value}`)
    }
  }
  assert.deepEqual(offenders, [])
})

test('MarkdownRenderer keeps the editor body in both variants, so content rules scoped to .editor-body apply', () => {
  const renderer = readFileSync(resolve(packageRoot, 'src/vue/MarkdownRenderer.vue'), 'utf8')
  const template = renderer.slice(renderer.indexOf('<template>'), renderer.indexOf('</template>'))
  assert.equal(template.match(/class="editor-body"/g)?.length, 2, 'plain and document variants each wrap .ProseMirror in .editor-body')
  // A child combinator on the renderer's ProseMirror would skip that body again.
  const offenders = sheets.flatMap(({ name, text }) => rules(text).filter(({ selector }) => /\.md-renderer\s*>\s*\.ProseMirror/.test(selector)).map(({ selector }) => `${name}: ${selector}`))
  assert.deepEqual(offenders, [])
})

test('every heading level H1-H6 has its own, strictly decreasing size in each editor and history variant', () => {
  const content = rules(sheets.find(({ name }) => name === 'content.css').text)
  const history = rules(sheets.find(({ name }) => name === 'history.css').text)
  const scopes = [[content, '.md-editor .editor-body'], [content, '.md-editor.paper .editor-body'], [content, '.md-editor.compact .editor-body'],
    [history, ':is(.md-document-history,.md-section-review) .md-renderer']]
  for (const [sheet, scope] of scopes) {
    const sizes = [1, 2, 3, 4, 5, 6].map(level => {
      // The last matching rule wins, as in the cascade (H4-H6 share a grouped rule and override its size).
      const size = sheet.filter(({ selector }) => selector === `${scope} h${level}` || (selector.startsWith(`${scope} :is(`) && selector.includes(`h${level}`)))
        .map(({ body }) => /font-size:\s*([\d.]+)px/.exec(body)?.[1]).filter(Boolean).at(-1)
      assert.ok(size, `${scope} h${level} has a font size`)
      return Number(size)
    })
    sizes.slice(1).forEach((size, index) => assert.ok(size < sizes[index], `${scope}: h${index + 2} (${size}px) is smaller than h${index + 1} (${sizes[index]}px)`))
  }
})
