// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mountOpenApiSourceEditor, prefetchOpenApiSourceEditor } from './sourceEditorLoader'

const monaco = vi.hoisted(() => ({
  editorLoads: 0, setupLoads: 0, ready: Promise.resolve(),
  options: undefined as undefined | Record<string, unknown>,
  calls: [] as unknown[][],
}))
// The shared editor chunk behind `@atlas/markdown/source`.
vi.mock('../../source/monaco/editor', () => {
  monaco.editorLoads++
  return {
    mountSourceEditor: async (host: HTMLElement, options: Record<string, unknown>) => {
      await monaco.ready
      monaco.options = options
      await (options.setup as (monaco: unknown, environment: unknown) => Promise<unknown>)({ fake: 'monaco' }, { registerWorker: () => ({ dispose() {} }) })
      const surface = host.ownerDocument.createElement('div'); surface.className = 'monaco-editor'; host.append(surface)
      const record = (name: string) => (...args: unknown[]) => { monaco.calls.push([name, ...args]) }
      return { getValue: () => 'value', setValue: record('setValue'), setLanguage: record('setLanguage'), focus: record('focus'), layout: record('layout'), destroy: () => surface.remove() }
    },
  }
})
// The OpenAPI setup chunk (schema, monaco-yaml, JSON mode).
vi.mock('./monacoSetup', () => {
  monaco.setupLoads++
  return { setupOpenApiSource: (api: unknown) => { monaco.calls.push(['setup', api]); return { dispose() {} } } }
})

afterEach(() => { vi.unstubAllGlobals(); document.body.replaceChildren() })

// Imported statically so the module load is not charged to the first test's 5 s timeout on a loaded host; the loader
// still imports Monaco lazily, which the first test asserts through the mock factories' load counters.
describe('lazy OpenAPI source editor', () => {
  it('does not load Monaco for a prefetch under Save-Data, then loads the editor and setup once on intent', async () => {
    vi.stubGlobal('navigator', { ...navigator, connection: { saveData: true } })
    expect(prefetchOpenApiSourceEditor()).toBe(false)
    await new Promise(resolve => setTimeout(resolve))
    expect([monaco.editorLoads, monaco.setupLoads]).toEqual([0, 0])
    vi.unstubAllGlobals()
    expect(prefetchOpenApiSourceEditor()).toBe(true); prefetchOpenApiSourceEditor()
    await vi.waitFor(() => expect([monaco.editorLoads, monaco.setupLoads]).toEqual([1, 1]))
  })

  it('shows a static source skeleton at once and mounts the shared editor with the OpenAPI setup', async () => {
    let release!: () => void
    monaco.ready = new Promise<void>(resolve => { release = resolve })
    const host = document.createElement('div'); document.body.append(host)
    const mounted = mountOpenApiSourceEditor(host, { source: 'openapi: 3.1.0\ninfo:\n  title: Demo', filename: 'api.json' })
    const skeleton = host.querySelector('.md-source-skeleton')!
    expect(skeleton.getAttribute('role')).toBe('status')
    expect(skeleton.querySelector('pre')!.textContent).toContain('title: Demo')
    release()
    const editor = await mounted
    expect(monaco.options).toMatchObject({ language: 'json', ariaLabel: 'OpenAPI zdroj', modelScope: 'openapi', value: 'openapi: 3.1.0\ninfo:\n  title: Demo' })
    expect(monaco.calls).toContainEqual(['setup', { fake: 'monaco' }])
    expect(host.querySelector('.md-source-skeleton')).toBeNull()
    editor.setValue('a: 1', 'api.yaml')
    editor.setFilename('other.JSON')
    expect(monaco.calls).toContainEqual(['setValue', 'a: 1', { language: 'yaml' }])
    expect(monaco.calls).toContainEqual(['setLanguage', 'json'])
    expect(editor.getValue()).toBe('value')
    editor.destroy()
    expect(host.querySelector('.monaco-editor')).toBeNull()
  })
})
