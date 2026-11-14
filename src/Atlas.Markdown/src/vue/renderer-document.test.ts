// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import type { MarkdownRichValue } from '../core'
import { createStaticCodeHighlighting } from '../plugins/editor-tools'
import { createHeadingAnchorsPlugin } from '../plugins/heading-anchors'
import { staticOutlineSource } from '../ui/outline'
import MarkdownRenderer from './MarkdownRenderer.vue'

const apps: App[] = []
beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} }) })
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})

const rich = (content: unknown[]): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: { type: 'doc', content } } as MarkdownRichValue)
const heading = (level: number, text: string) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
const flush = async () => { for (let i = 0; i < 6; i++) { await nextTick(); await new Promise(resolve => setTimeout(resolve)) } }

function mount(props: Record<string, unknown>) {
  const host = document.createElement('div')
  document.body.append(host)
  const app = createApp({ render: () => h(MarkdownRenderer as never, props) })
  apps.push(app)
  app.mount(host)
  return host
}

describe('MarkdownRenderer document variant', () => {
  it('keeps the plain layout by default', async () => {
    const host = mount({ value: rich([paragraph('Text')]), profile: 'rich', ariaLabel: 'Náhled' })
    await flush()
    const root = host.firstElementChild as HTMLElement
    expect(root.className).toBe('md-editor readonly md-renderer')
    // The editor's body without paper or outline, so the content rules of the editor apply.
    expect(root.querySelector(':scope > .editor-body > .ProseMirror > p')?.textContent).toBe('Text')
    expect(host.querySelector('.paper-inner, .md-outline-trigger, .md-outline')).toBeNull()
  })

  it('mirrors the editor document layout and offers the outline over rendered headings', async () => {
    const host = mount({
      value: rich([heading(1, 'Úvod'), paragraph('A'), heading(2, 'Podrobnosti'), paragraph('B')]), profile: 'rich', ariaLabel: 'Stránka',
      variant: 'document', outline: { mode: 'floating', preview: false },
    })
    await flush()
    const root = host.firstElementChild as HTMLElement
    expect(root.classList).toContain('paper')
    expect(root.classList).toContain('has-outline')
    expect(host.querySelector('.md-outline-layout > .md-outline-document > .paper-inner > .editor-body > .ProseMirror h1')?.textContent).toBe('Úvod')
    // The outline is a lazy chunk, exactly as in the editor.
    await vi.waitFor(() => expect(host.querySelector('.md-outline-trigger')).not.toBeNull(), { timeout: 5000 })
    await vi.waitFor(() => expect(host.querySelector('.md-outline-rail')?.children).toHaveLength(2), { timeout: 5000 })
  })

  it('collects headings statically, skips modules and highlights with the editor attribute', () => {
    const root = document.createElement('div')
    root.innerHTML = '<h2 data-heading-id="a">Jedna</h2><p>x</p><div data-md-module><h3>Uvnitř modulu</h3></div><h3></h3>'
    const source = staticOutlineSource(root)
    const headings = source.collect()
    expect(headings.map(entry => [entry.level, entry.title, entry.headingId])).toEqual([[2, 'Jedna', 'a'], [3, 'Bez názvu', undefined]])
    const highlight = source.createHighlight()
    highlight.set(headings[0]!)
    expect(root.querySelector('h2')!.getAttribute('data-md-outline-target')).toBe('true')
    highlight.set(headings[1]!)
    expect(root.querySelector('h2')!.hasAttribute('data-md-outline-target')).toBe(false)
    highlight.destroy()
    expect(root.querySelector('[data-md-outline-target]')).toBeNull()
    const changed = vi.fn()
    const stop = source.subscribe(changed, () => {})
    source.changed()
    stop()
    source.changed()
    expect(changed).toHaveBeenCalledTimes(1)
  })

  it('refreshes the outline when the rendered document is replaced', async () => {
    const value = ref(rich([heading(2, 'Před')]))
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => h(MarkdownRenderer as never, { value: value.value, profile: 'rich', ariaLabel: 'Stránka', variant: 'document', outline: 'panel' }) })
    apps.push(app)
    app.mount(host)
    await flush()
    const entries = () => [...host.querySelectorAll('.md-outline-entry')].map(entry => entry.textContent?.trim())
    expect(entries()).toEqual(['Před'])
    value.value = rich([heading(2, 'Po'), heading(3, 'Další')])
    await flush()
    expect(entries()).toEqual(['Po', 'Další'])
  })

  it('wraps tables as the editor table view does in both variants, so the table styles apply', async () => {
    const cell = (type: string, text: string) => ({ type, content: [paragraph(text)] })
    const host = mount({
      value: rich([{ type: 'table', content: [{ type: 'tableRow', content: [cell('tableHeader', 'H')] }, { type: 'tableRow', content: [cell('tableCell', 'C')] }] }]),
      profile: 'rich', ariaLabel: 'Stránka', variant: 'document',
    })
    await flush()
    expect(host.querySelector('.editor-body .ProseMirror > .tableWrapper > table th')?.textContent).toBe('H')
    const plain = mount({ value: rich([{ type: 'table', content: [{ type: 'tableRow', content: [cell('tableCell', 'P')] }] }]), profile: 'rich', ariaLabel: 'Náhled' })
    await flush()
    expect(plain.querySelector('.editor-body .ProseMirror > .tableWrapper > table td')?.textContent).toBe('P')
  })

  it('highlights rendered code with the editor classes and keeps heading anchors', async () => {
    const host = mount({
      value: rich([heading(2, 'Kód'), { type: 'codeBlock', attrs: { language: 'csharp' }, content: [{ type: 'text', text: 'var x = await client.GetAsync("/api");' }] }]),
      profile: 'rich', ariaLabel: 'Stránka', variant: 'document',
      plugins: [createStaticCodeHighlighting({ languages: ['csharp'] }), createHeadingAnchorsPlugin({ getHeadingUrl: id => `#${id}` })],
    })
    await vi.waitFor(() => expect(host.querySelectorAll('pre code .hljs-keyword').length).toBeGreaterThan(0), { timeout: 5000 })
    expect(host.querySelector('pre code')!.textContent).toBe('var x = await client.GetAsync("/api");')
  })
})
