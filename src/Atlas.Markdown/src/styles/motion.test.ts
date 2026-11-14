import { describe, expect, it } from 'vitest'
import { markdownConcealDefaults, markdownMotionDefaults } from '../core/motion'

// The motion contract of the owner's prototype (Atlas 2.0 motion unification, package E1), asserted on the
// stylesheet text: jsdom resolves neither media queries nor `:where()` custom properties. `.css?raw` is processed
// for tests (vite.config.ts `test.css`), so changing a stylesheet reruns this file in `check:changed`.
const sheets = Object.fromEntries(Object.entries(import.meta.glob<string>(['../**/*.css', '../**/*.vue'], { eager: true, query: '?raw', import: 'default' }))
  // Vite names files of this directory `./x.css`, the others `../dir/x.css`.
  .map(([file, text]) => [file.replace(/^\.\//, 'styles/').replace(/^\.\.\//, ''), text]))
const motionCss = sheets['styles/motion.css']!, editorCss = sheets['styles/editor.css']!, assistCss = sheets['assist/assist.css']!

/** The declarations of the first rule whose selector is exactly `selector` inside `scope` (whole sheet or an @-block). */
function declarations(scope: string, selector: string): Record<string, string> {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\s+/g, '\\s*')
  const body = new RegExp(`(?:^|[}\\s])${escaped}\\s*\\{([^{}]*)\\}`).exec(scope)?.[1]
  expect(body, `rule ${selector}`).toBeDefined()
  return Object.fromEntries(body!.replace(/\/\*[\s\S]*?\*\//g, '').split(';').map(part => part.trim()).filter(Boolean).map(part => {
    const colon = part.indexOf(':')
    return [part.slice(0, colon).trim(), part.slice(colon + 1).trim()]
  }))
}
/** The text of every `@media <query> { … }` block (one level of nesting). */
function media(sheet: string, query: RegExp): string {
  const blocks: string[] = []
  for (const match of sheet.matchAll(/@media\s*([^{]+)\{/g)) {
    if (!query.test(match[1]!)) continue
    let depth = 1, index = match.index! + match[0].length
    const start = index
    while (depth && index < sheet.length) { const char = sheet[index++]; if (char === '{') depth++; else if (char === '}') depth-- }
    blocks.push(sheet.slice(start, index - 1))
  }
  return blocks.join('\n')
}
const reducedMedia = media(motionCss, /prefers-reduced-motion:\s*reduce/)
const milliseconds = (value: string) => /^(\d*\.?\d+)(ms|s)$/.test(value) ? Number.parseFloat(value) * (value.endsWith('ms') ? 1 : 1000) : Number.NaN

describe('motion tokens', () => {
  const tokens = declarations(motionCss, ':where(:root)')

  it('carry the prototype values under :where(:root), so the editor moves the same without a host', () => {
    expect(tokens).toMatchObject({
      '--md-motion-fast': '120ms', '--md-motion-base': '200ms', '--md-motion-slow': '280ms',
      '--md-motion-menu': '140ms', '--md-motion-target': '160ms', '--md-motion-pop': '320ms',
      '--md-motion-glow': '1600ms', '--md-motion-loop': '1600ms', '--md-motion-spin': '800ms', '--md-motion-pulse': '1000ms',
      '--md-motion-ease': 'cubic-bezier(.22, 1, .36, 1)', '--md-motion-ease-io': 'cubic-bezier(.65, 0, .35, 1)',
      '--md-motion-rise': '4px', '--md-motion-pop-from': '.8', '--md-motion-time-scale': '1',
    })
    // The invisible ink and the vault keep their own long signature timing (O6): 5 durations, a stagger and 3 curves.
    expect(tokens).toMatchObject({
      '--md-conceal-fast': '200ms', '--md-conceal-enter': '520ms', '--md-conceal-unfold': '900ms', '--md-conceal-roll': '2800ms',
      '--md-conceal-aurora': '42s', '--md-conceal-stagger': '70ms',
      '--md-conceal-ease': 'cubic-bezier(.22, 1, .36, 1)', '--md-conceal-ease-rail': 'cubic-bezier(.2, 1.2, .4, 1)', '--md-conceal-ease-roll': 'cubic-bezier(.45, .05, .3, 1)',
    })
    // `--md-motion-surface` is the prototype's name for the target duration; it resolves wherever a host remaps.
    expect(declarations(motionCss, ':where(:root, .md-editor)')['--md-motion-surface']).toBe('var(--md-motion-target)')
  })

  it('match the JavaScript fallbacks of core/motion.ts', () => {
    for (const [name, value] of Object.entries(markdownMotionDefaults)) expect(milliseconds(tokens[`--md-motion-${name}`]!), name).toBe(value)
    for (const [name, value] of Object.entries(markdownConcealDefaults)) expect(milliseconds(tokens[`--md-conceal-${name}`]!), name).toBe(value)
  })

  it('are declared nowhere else: hosts and the assist module alias them', () => {
    const declared = Object.entries(sheets).filter(([file]) => !file.endsWith('/motion.css'))
      .flatMap(([file, text]) => [...text.matchAll(/(--md-(?:motion|conceal)-[\w-]+)\s*:/g)].map(match => `${file}: ${match[1]}`))
    expect(declared).toEqual([])
    const assist = declarations(assistCss, ':where(:root, .md-editor)')
    expect(assist).toMatchObject({
      '--md-assist-fast': 'var(--md-motion-fast)', '--md-assist-base': 'var(--md-motion-base)', '--md-assist-slow': 'var(--md-motion-slow)',
      '--md-assist-menu': 'var(--md-motion-target)', '--md-assist-glow': 'var(--md-motion-glow)',
      '--md-assist-ease': 'var(--md-motion-ease)', '--md-assist-ease-io': 'var(--md-motion-ease-io)',
      '--md-assist-time-scale': 'var(--md-motion-time-scale)',
    })
  })
})

describe('reduced motion', () => {
  const reducedTokens = {
    '--md-motion-base': '160ms', '--md-motion-slow': '160ms', '--md-motion-pop': '160ms',
    '--md-motion-loop': '0s', '--md-motion-spin': '0s', '--md-motion-pulse': '0s',
    '--md-motion-ease': 'linear', '--md-motion-ease-io': 'linear', '--md-motion-rise': '0px', '--md-motion-pop-from': '1',
    // The invisible ink and the vault: entries at most 160 ms, linear, the aurora and the stagger stand.
    '--md-conceal-fast': '160ms', '--md-conceal-enter': '160ms', '--md-conceal-unfold': '160ms', '--md-conceal-roll': '160ms',
    '--md-conceal-aurora': '0s', '--md-conceal-stagger': '0s',
    '--md-conceal-ease': 'linear', '--md-conceal-ease-rail': 'linear', '--md-conceal-ease-roll': 'linear',
  }

  it('rewrites the tokens: at most 160 ms, linear, no distance, loops stand', () => {
    expect(declarations(reducedMedia, ':where(:root)')).toEqual(reducedTokens)
    // The same path for a host or a preview that forces it (`data-md-motion="reduced"`, the playground toggle).
    expect(declarations(motionCss, ':where([data-md-motion=reduced])')).toEqual(reducedTokens)
    // Every duration that stays unchanged is already within 160 ms, except the afterglow (background only).
    const defaults = declarations(motionCss, ':where(:root)')
    for (const [name, value] of Object.entries(defaults)) {
      if (!/^--md-(motion-(fast|base|slow|menu|target|pop|loop|spin|pulse)|conceal-(fast|enter|unfold|roll))$/.test(name)) continue
      expect(milliseconds((reducedTokens as Record<string, string>)[name] ?? value), name).toBeLessThanOrEqual(160)
    }
  })

  it('limits transitions to linear opacity and colour, so drag positions never show an in-between frame', () => {
    // The editor and the outline layout around an editor or renderer.
    const root = ':is(.md-editor, .md-outline-layout, .md-detail-anchor, .md-section-review)'
    const guards = [reducedMedia, motionCss.replace(reducedMedia, '')].map(scope => {
      const rule = /([^{}]*\.md-editor[^{}]*)\{\s*transition-property:\s*([^;}]+?)\s*!important;\s*transition-timing-function:\s*linear\s*!important;?\s*\}/.exec(scope)
      expect(rule, 'transition-property guard').not.toBeNull()
      return { selector: rule![1]!, properties: rule![2]!.split(',').map(part => part.trim()) }
    })
    for (const { selector, properties } of guards) {
      // No `visibility`: a generic visibility transition hid whole editors under a hidden hydrating one; only `.dh-wrap` (below) animates it.
      expect(properties).toEqual(['opacity', 'color', 'background-color', 'border-color', 'box-shadow', 'fill', 'stroke'])
      // Every descendant and pseudo-element, but not inside the invisible ink: it keeps its own slow fades and colour
      // roll-overs (docs/concealed-content.md).
      for (const part of [`${root} :not(.md-concealed *),`, `${root} :not(.md-concealed *)::before`, `${root} :not(.md-concealed *)::after`]) expect(selector).toContain(part)
    }
    expect(guards[1]!.selector).toContain(`[data-md-motion=reduced] ${root}`)
  })

  it('keeps visibility in the reduced list only for the drag handle wrapper, which declares its own', () => {
    const rules = [...motionCss.matchAll(/([^{}]*)\{[^{}]*transition-property:\s*([^;}]+?)\s*!important/g)]
    const withVisibility = rules.filter(rule => /visibility/.test(rule[2]!)).map(rule => rule[1]!.trim())
    expect(withVisibility.length).toBe(2)
    for (const selector of withVisibility) expect(selector).toContain('.dh-wrap')
  })

  it('replaces the global 0.01 ms kill switch', () => {
    expect(editorCss).not.toMatch(/reduced-motion\.css/)
    expect(Object.keys(sheets).filter(file => file.endsWith('/reduced-motion.css'))).toEqual([])
    const offenders = Object.entries(sheets).flatMap(([file, text]) => [
      ...[...text.matchAll(/0\.01ms/g)].map(() => `${file}: 0.01ms`),
      ...[...media(text, /prefers-reduced-motion:\s*reduce/).matchAll(/(?:animation|transition)(?:-[\w-]+)?\s*:[^;{}]*!important/g)]
        .filter(([declaration]) => !/^transition-(property|timing-function):/.test(declaration) || file !== 'styles/motion.css')
        .map(([declaration]) => `${file}: ${declaration}`),
    ])
    expect(offenders).toEqual([])
  })

  it('stops every working loop through its duration token', () => {
    const loops = Object.entries(sheets).filter(([file]) => !file.startsWith('concealment/')).flatMap(([file, text]) => {
      const gated = media(text, /prefers-reduced-motion:\s*no-preference/)
      return [...text.replace(gated, '').matchAll(/animation:[^;{}]*\binfinite\b[^;{}]*/g)].map(([declaration]) => ({ file, declaration }))
    })
    expect(loops.length).toBeGreaterThanOrEqual(3)
    for (const { file, declaration } of loops) expect(declaration, file).toMatch(/var\(--md-motion-(loop|spin|pulse)\)/)
  })
})
