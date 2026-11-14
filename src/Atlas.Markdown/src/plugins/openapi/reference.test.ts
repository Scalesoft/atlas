// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ScalarFrameCallbacks } from './scalarView'

const frames: { callbacks: ScalarFrameCallbacks; disposed: boolean }[] = []
vi.mock('./scalarView', () => ({
  mountScalarFrame: (_host: HTMLElement, _data: unknown, _options: unknown, callbacks: ScalarFrameCallbacks) => {
    const frame = { callbacks, disposed: false }
    frames.push(frame)
    return () => { frame.disposed = true }
  },
}))

const { mountOpenApiReference, OPENAPI_REFERENCE_TIMEOUT_MS } = await import('./reference')
const data = { version: 1 as const, filename: 'probe.yaml', source: 'openapi: 3.1.0' }
const settle = async () => { await vi.dynamicImportSettled(); await Promise.resolve() }

describe('OpenAPI reference view state', () => {
  let host: HTMLElement
  beforeEach(() => { frames.length = 0; host = document.createElement('div'); document.body.append(host) })
  afterEach(() => { vi.useRealTimers(); host.remove() })

  it('shows loading, then removes the status once Scalar is ready', async () => {
    const ready = vi.fn()
    host.addEventListener('openapi-render-ready', ready)
    const dispose = mountOpenApiReference(host, data)
    expect(host.querySelector('[role=status]')?.textContent).toContain('Načítá se dokumentace API')
    await settle()
    frames[0].callbacks.onReady()
    expect(host.querySelector('.md-openapi-status')).toBeNull()
    expect(ready).toHaveBeenCalledOnce()
    dispose()
    expect(frames[0].disposed).toBe(true)
  })

  it('replaces the endless loading state with an alert and a working retry', async () => {
    const errors: string[] = []
    host.addEventListener('openapi-render-error', event => errors.push((event as CustomEvent<string>).detail))
    mountOpenApiReference(host, data)
    await settle()
    frames[0].callbacks.onError('Knihovnu Scalar se nepodařilo načíst.')
    const alert = host.querySelector('[role=alert]')
    expect(alert?.textContent).toContain('Knihovnu Scalar se nepodařilo načíst.')
    expect(host.querySelector('[role=status]')).toBeNull()
    expect(frames[0].disposed).toBe(true)
    expect(errors).toEqual(['Knihovnu Scalar se nepodařilo načíst.'])
    alert!.querySelector('button')!.click()
    expect(host.querySelector('[role=status]')).not.toBeNull()
    await settle()
    expect(frames).toHaveLength(2)
    frames[0].callbacks.onReady() // a stale attempt cannot clear the new state
    expect(host.querySelector('[role=status]')).not.toBeNull()
    frames[1].callbacks.onReady()
    expect(host.querySelector('.md-openapi-status')).toBeNull()
  })

  it('times out visibly but still renders a late load', async () => {
    vi.useFakeTimers()
    mountOpenApiReference(host, data)
    await settle()
    vi.advanceTimersByTime(OPENAPI_REFERENCE_TIMEOUT_MS)
    expect(host.querySelector('[role=alert]')?.textContent).toContain('nenačetla včas')
    expect(frames[0].disposed).toBe(false)
    frames[0].callbacks.onReady()
    expect(host.querySelector('.md-openapi-status')).toBeNull()
  })

  it('stops reporting after disposal', async () => {
    const dispose = mountOpenApiReference(host, data)
    await settle()
    dispose()
    frames[0].callbacks.onError('late')
    expect(host.children).toHaveLength(0)
  })
})
