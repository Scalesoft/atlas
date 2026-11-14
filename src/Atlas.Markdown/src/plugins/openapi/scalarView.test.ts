// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mountScalarFrame } from './scalarView'

// The bundle lives outside the package root, where the test server may not read; only its URL matters here.
vi.mock('./scalar-standalone?url', () => ({ default: '/assets/scalar-standalone.js' }))

// The frame's protocol (M3): an opaque-origin sandbox, configured and answered by postMessage only. The rendering itself runs in a
// real browser (Scalar does not run in jsdom).
describe('mountScalarFrame', () => {
  afterEach(() => { document.body.replaceChildren() })

  const mount = (allowRequests?: boolean) => {
    const dialog = document.createElement('dialog')
    const host = document.createElement('div')
    dialog.append(host); document.body.append(dialog)
    const callbacks = { onReady: vi.fn(), onError: vi.fn() }
    const dispose = mountScalarFrame(host, { version: 1, filename: 'api.yaml', source: 'openapi: 3.0.0' }, { allowRequests }, callbacks)
    const frame = host.querySelector('iframe')!
    const send = (data: unknown, source: unknown = frame.contentWindow) => window.dispatchEvent(new MessageEvent('message', { data, source: source as Window }))
    return { dialog, frame, callbacks, dispose, send }
  }

  it('runs the reference in an opaque origin with its scripts as files, its own first', () => {
    const { frame } = mount(false)
    expect(frame.getAttribute('sandbox')).toBe('allow-scripts')
    expect(frame.srcdoc).not.toMatch(/<script>/)
    const sources = [...frame.srcdoc.matchAll(/<script src="([^"]+)"><\/script>/g)].map(match => match[1])
    expect(sources).toHaveLength(2)
    expect(sources[0]).toMatch(/scalarFrame/)
  })

  it('sends the configuration on request and follows only its own frame', () => {
    const { frame, callbacks, dialog, dispose, send } = mount(false)
    const post = vi.spyOn(frame.contentWindow!, 'postMessage')
    send({ type: 'openapi-frame-ready' }, window)
    expect(callbacks.onReady).not.toHaveBeenCalled()

    send({ type: 'openapi-frame-loaded' })
    expect(post).toHaveBeenCalledWith(expect.objectContaining({ type: 'openapi-frame-config', allowRequests: false,
      configuration: expect.objectContaining({ content: 'openapi: 3.0.0', hideTestRequestButton: true, persistAuth: false }) }), '*')
    send({ type: 'openapi-frame-ready' })
    expect(callbacks.onReady).toHaveBeenCalledOnce()
    const cancel = vi.fn(); dialog.addEventListener('cancel', cancel)
    send({ type: 'openapi-frame-escape' })
    expect(cancel).toHaveBeenCalledOnce()
    send({ type: 'openapi-frame-error', message: 'boom' })
    expect(callbacks.onError).toHaveBeenCalledWith(expect.stringContaining('boom'))

    dispose()
    send({ type: 'openapi-frame-missing' })
    expect(callbacks.onError).toHaveBeenCalledOnce()
    expect(frame.isConnected).toBe(false)
  })
})
