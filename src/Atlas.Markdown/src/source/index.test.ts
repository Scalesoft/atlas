// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownSourceSession, mountSourceEditor, prefetchSourceEditor } from './index'

const monaco = vi.hoisted(() => ({ loads: 0, sessions: 0, ready: Promise.resolve(), mounted: [] as unknown[] }))
vi.mock('./monaco/editor', () => {
  monaco.loads++
  return {
    mountSourceEditor: async (host: HTMLElement, options: unknown) => {
      await monaco.ready
      monaco.mounted.push(options)
      const surface = host.ownerDocument.createElement('div'); surface.className = 'monaco-editor'; host.append(surface)
      return { destroy: () => surface.remove() }
    },
  }
})
vi.mock('./session', () => {
  monaco.sessions++
  return { openMarkdownSourceSession: async (options: { value: unknown }) => ({ text: 'text', value: options.value }) }
})

afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); document.body.replaceChildren() })

describe('@atlas/markdown/source', () => {
  it('does not load Monaco for a prefetch under Save-Data, then loads it once on intent', async () => {
    vi.stubGlobal('navigator', { ...navigator, connection: { saveData: true } })
    expect(prefetchSourceEditor()).toBe(false)
    await new Promise(resolve => setTimeout(resolve))
    expect(monaco.loads).toBe(0)
    vi.unstubAllGlobals()
    expect(prefetchSourceEditor()).toBe(true); prefetchSourceEditor()
    await vi.waitFor(() => expect(monaco.loads).toBe(1))
  })

  it('shows the real text at once, the loading status only after ~300 ms, and swaps in the editor', async () => {
    let release!: () => void
    monaco.ready = new Promise<void>(resolve => { release = resolve })
    const host = document.createElement('div'); document.body.append(host)
    const mounted = mountSourceEditor(host, { language: 'markdown', value: '# Stránka\n\n```md-protected b3\nChráněný blok: Graf\n```', ariaLabel: 'Zdroj stránky', protectedBlocks: [{ id: 'b3' }] })
    const skeleton = host.querySelector('.md-source-skeleton')!
    expect(skeleton.getAttribute('role')).toBe('status')
    expect(skeleton.getAttribute('aria-busy')).toBe('true')
    expect(skeleton.querySelector('pre')!.textContent).toContain('Chráněný blok: Graf')
    expect(skeleton.querySelector('span')!.textContent).toBe('')
    await vi.waitFor(() => expect(skeleton.querySelector('span')!.textContent).toBe('Načítá se editor zdroje…'), { timeout: 1000 })
    expect(host.style.position).toBe('relative')
    release()
    const editor = await mounted
    expect(monaco.mounted.at(-1)).toMatchObject({ language: 'markdown', protectedBlocks: [{ id: 'b3' }] })
    expect(host.querySelector('.md-source-skeleton')).toBeNull()
    expect(host.querySelector('.monaco-editor')).not.toBeNull()
    expect(host.style.position).toBe('')
    editor.destroy()
  })

  it('keeps the skeleton silent when the host already reports loading (one status only)', async () => {
    let release!: () => void
    monaco.ready = new Promise<void>(resolve => { release = resolve })
    const busy = document.createElement('div'); busy.setAttribute('aria-busy', 'true')
    const host = document.createElement('div'); busy.append(host); document.body.append(busy)
    const mounted = mountSourceEditor(host, { language: 'markdown', value: '# Stránka', ariaLabel: 'Zdroj stránky' })
    const skeleton = host.querySelector('.md-source-skeleton')!
    expect(skeleton.getAttribute('role')).toBeNull()
    await new Promise(resolve => setTimeout(resolve, 350))
    expect(skeleton.querySelector('span')!.textContent).toBe('')
    expect(skeleton.querySelector('pre')!.textContent).toBe('# Stránka')
    release()
    ;(await mounted).destroy()
  })

  it('loads the headless session lazily and asynchronously', async () => {
    expect(monaco.sessions).toBe(0)
    const value = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [] } } as const
    const session = createMarkdownSourceSession({ value })
    expect(session).toBeInstanceOf(Promise)
    await expect(session).resolves.toMatchObject({ text: 'text', value })
    expect(monaco.sessions).toBe(1)
  })
})
