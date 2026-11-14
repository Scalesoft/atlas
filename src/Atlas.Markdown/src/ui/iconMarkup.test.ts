import { describe, expect, it } from 'vitest'
import { iconMarkup, iconSvgMarkup, ICON_STROKE_WIDTH } from './iconMarkup'

describe('iconMarkup registry', () => {
  it('has every concept the editor and the wiki use', () => {
    for (const name of ['link','trash-2','plus','check','x','chevron-down','ellipsis','search','settings-2','maximize-2','download','copy','paperclip','ticket','file-text','star'])
      expect(iconMarkup[name], name).toBeTruthy()
  })
  it('stores paths only, never <svg>, never stroke attributes, never text', () => {
    for (const [name, markup] of Object.entries(iconMarkup)) {
      expect(markup, name).not.toMatch(/<svg|stroke-width|<text/)
      expect(markup, name).toMatch(/^(<(path|line|circle|polyline|polygon|rect|ellipse)\b[^>]*\/>)+$/)
    }
  })
  it('renders one svg with the shared geometry contract', () => {
    const svg = iconSvgMarkup('link', 18)
    expect(svg).toContain('viewBox="0 0 24 24"')
    expect(svg).toContain(`stroke-width="${ICON_STROKE_WIDTH}"`)
    expect(svg).toContain('width="18" height="18"')
    expect(svg).toContain('aria-hidden="true"')
  })
  it('has no two names with identical geometry (dead duplicates)', () => {
    const seen = new Map<string, string>()
    for (const [name, markup] of Object.entries(iconMarkup)) {
      expect(seen.get(markup), `${name} duplicates ${seen.get(markup)}`).toBeUndefined()
      seen.set(markup, name)
    }
  })
})
