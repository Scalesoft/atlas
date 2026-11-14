// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { createApp, h, type App } from 'vue'
import { createIconElement, ICON_STROKE_WIDTH, iconMarkup, iconSvgMarkup, MarkdownIcon } from './index'
import * as icons from '../ui/icons'

let app: App | undefined
afterEach(() => { app?.unmount(); app = undefined; document.body.replaceChildren() })

/** Registry markup as the DOM serialises it, so a comparison is about geometry, not `<path/>` vs `<path></path>`. */
function body(name: string) {
  const probe = document.createElementNS('http://www.w3.org/2000/svg', 'svg')
  probe.innerHTML = iconMarkup[name]!
  return probe.innerHTML
}

function mount(component: Parameters<typeof h>[0], props?: Record<string, unknown>) {
  const host = document.createElement('div'); document.body.append(host)
  app = createApp({ render: () => h(component, props) }); app.mount(host)
  return host
}

describe('@atlas/markdown/icons', () => {
  it('builds a DOM icon that names itself and carries the shared contract', () => {
    const element = createIconElement(document, 'trash-2')
    expect(element.getAttribute('data-icon')).toBe('trash-2')
    expect(element.getAttribute('stroke-width')).toBe(String(ICON_STROKE_WIDTH))
    expect(element.getAttribute('viewBox')).toBe('0 0 24 24')
    expect(element.getAttribute('width')).toBe('16')
    expect(element.innerHTML).toBe(body('trash-2'))
  })

  it('ships every icon the Atlas settings and Security centrum use', () => {
    const settings = ['shield', 'shield-check', 'shield-alert', 'layout-dashboard', 'users', 'user-check', 'lock', 'lock-open', 'key-round',
      'fingerprint', 'network', 'scroll-text', 'building-2', 'globe', 'server', 'circle-check', 'circle-x', 'ban', 'hourglass', 'triangle-alert',
      'arrow-right-left', 'arrow-right', 'filter', 'activity', 'sparkles', 'calendar', 'plug', 'map-pin'] as const
    for (const name of settings) {
      expect(iconMarkup[name], name).toMatch(/^<(path|rect|circle|line|polygon|polyline|ellipse) /)
      expect(createIconElement(document, name).innerHTML, name).toBe(body(name))
    }
    // One concept, one drawing: no new entry repeats another one's geometry.
    const drawings = Object.values(iconMarkup)
    expect(new Set(drawings).size).toBe(drawings.length)
  })

  it('draws circle-help for an unknown name instead of another concept', () => {
    const element = createIconElement(document, 'nope' as never)
    expect(element.innerHTML).toBe(body('circle-help'))
  })

  it('renders the same geometry through the Vue component', () => {
    const host = mount(MarkdownIcon, { name: 'link', size: 18 })
    const svg = host.querySelector('svg[data-icon="link"]')!
    expect(svg).not.toBeNull()
    expect(svg.getAttribute('width')).toBe('18')
    expect(svg.innerHTML).toBe(body('link'))
    expect(iconSvgMarkup('link', 18)).toContain(iconMarkup['link']!)
  })

  // 3.6 perf: 482 icons on a page each parsed their markup (463 ms at 4x CPU); now one parse per drawing, cloned after.
  it('parses a drawing once and clones it for every further icon, the Vue component and the DOM factory alike', () => {
    const setter = Object.getOwnPropertyDescriptor(Element.prototype, 'innerHTML')!.set!
    let parses = 0
    Object.defineProperty(Element.prototype, 'innerHTML', { ...Object.getOwnPropertyDescriptor(Element.prototype, 'innerHTML'),
      set(value: string) { if ((this as Element).namespaceURI === 'http://www.w3.org/2000/svg') parses++; setter.call(this, value) } })
    try {
      const host = mount({ render: () => Array.from({ length: 20 }, () => h(MarkdownIcon, { name: 'database' })) })
      for (let i = 0; i < 20; i++) createIconElement(document, 'database')
      expect(parses).toBeLessThanOrEqual(1)
      const drawn = [...host.querySelectorAll('svg[data-icon="database"]')]
      expect(drawn).toHaveLength(20)
      expect(drawn.every(svg => svg.innerHTML === body('database'))).toBe(true)
    } finally {
      Object.defineProperty(Element.prototype, 'innerHTML', { ...Object.getOwnPropertyDescriptor(Element.prototype, 'innerHTML'), set: setter })
    }
  })

  it('keeps the legacy ui/icons component names as thin wrappers over the registry', () => {
    for (const [component, name] of [[icons.LinkIcon, 'link'], [icons.TrashIcon, 'trash-2'], [icons.BoldIcon, 'bold']] as const) {
      const host = mount(component)
      expect(host.querySelector('svg')!.getAttribute('data-icon'), name).toBe(name)
      app?.unmount(); app = undefined
    }
  })
})
