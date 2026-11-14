import { afterEach, describe, expect, it, vi } from 'vitest'

vi.mock('./monaco-editor.worker?worker', () => ({ default: class { readonly kind = 'editor'; constructor(readonly options?: { name?: string }) {} } }))
const { acquireMonacoEnvironment, registerWorker } = await import('./environment')

type Environment = { getWorker(moduleId: string, label: string): { kind: string } }
const current = () => (globalThis as unknown as { MonacoEnvironment?: Environment }).MonacoEnvironment
const fake = (kind: string) => () => ({ kind }) as unknown as Worker

afterEach(() => { delete (globalThis as { MonacoEnvironment?: unknown }).MonacoEnvironment })

describe('shared MonacoEnvironment', () => {
  it('is installed by the first editor, restored by the last, and answers registered labels', () => {
    const host = { getWorker: () => ({ kind: 'host' }), marker: 1 }
    ;(globalThis as { MonacoEnvironment?: unknown }).MonacoEnvironment = host
    const releaseFirst = acquireMonacoEnvironment()
    const releaseSecond = acquireMonacoEnvironment()
    const yaml = registerWorker('yaml', fake('yaml'))
    expect(current()).not.toBe(host)
    expect((current() as unknown as { marker: number }).marker).toBe(1)
    expect(current()!.getWorker('m', 'yaml').kind).toBe('yaml')
    // Unregistered labels go to the host's own environment first.
    expect(current()!.getWorker('m', 'editorWorkerService').kind).toBe('host')
    releaseFirst(); releaseFirst()
    expect(current()).not.toBe(host)
    yaml.dispose()
    releaseSecond()
    expect(current()).toBe(host)
  })

  it('lets the host answer shared labels first and falls back to the base editor worker', () => {
    const release = acquireMonacoEnvironment()
    const json = registerWorker('json', fake('ours'), { hostFirst: true })
    expect(current()!.getWorker('m', 'json').kind).toBe('ours')
    expect(current()!.getWorker('m', 'editorWorkerService').kind).toBe('editor')
    json.dispose(); release()
    expect(current()).toBeUndefined()

    const host = { getWorker: (_: string, label: string) => ({ kind: `host-${label}` }) }
    ;(globalThis as { MonacoEnvironment?: unknown }).MonacoEnvironment = host
    const again = acquireMonacoEnvironment()
    const shared = registerWorker('json', fake('ours'), { hostFirst: true })
    const newest = registerWorker('yaml', fake('first'))
    const newer = registerWorker('yaml', fake('second'))
    expect(current()!.getWorker('m', 'json').kind).toBe('host-json')
    expect(current()!.getWorker('m', 'yaml').kind).toBe('second')
    newer.dispose()
    expect(current()!.getWorker('m', 'yaml').kind).toBe('first')
    shared.dispose(); newest.dispose(); again()
    expect(current()).toBe(host)
  })

  it('keeps an environment the host replaced while an editor was mounted', () => {
    const release = acquireMonacoEnvironment()
    const replaced = { getWorker: () => ({ kind: 'replaced' }) }
    ;(globalThis as { MonacoEnvironment?: unknown }).MonacoEnvironment = replaced
    release()
    expect(current()).toBe(replaced)
  })
})
