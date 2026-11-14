// Runs inside the reference frame (scalarView.ts) before Scalar's bundle, as a classic script: the frame has an opaque origin, so
// it reaches neither the host's DOM, cookies nor storage, and the host reaches it only by postMessage. Plain JavaScript, emitted
// as its own file: a script in the srcdoc itself would need an inline-script exception in the host's CSP.
(() => {
  const post = message => parent.postMessage(message, '*')
  // Scalar keeps UI preferences in localStorage, which throws in an opaque origin; a memory store keeps them for this view only.
  try { void window.localStorage } catch {
    const items = new Map()
    const memory = {
      get length() { return items.size },
      key: index => [...items.keys()][index] ?? null,
      getItem: key => items.has(String(key)) ? items.get(String(key)) : null,
      setItem: (key, value) => { items.set(String(key), String(value)) },
      removeItem: key => { items.delete(String(key)) },
      clear: () => { items.clear() },
    }
    for (const name of ['localStorage', 'sessionStorage']) Object.defineProperty(window, name, { configurable: true, get: () => memory })
  }
  // Keys typed in the frame never reach the host's dialog: Esc asks the host to close it, as it does everywhere else.
  addEventListener('keydown', event => {
    if (event.key === 'Escape' && !event.defaultPrevented && !event.isComposing) post({ type: 'openapi-frame-escape' })
  })
  addEventListener('message', function start(event) {
    const data = event.source === parent ? event.data : undefined
    if (!data || data.type !== 'openapi-frame-config') return
    removeEventListener('message', start)
    const createApiReference = window.Scalar && window.Scalar.createApiReference
    const mount = document.getElementById('reference')
    if (!createApiReference || !mount) { post({ type: 'openapi-frame-missing' }); return }
    const config = data.configuration
    if (!data.allowRequests) config.customFetch = () => Promise.reject(new Error('API requests are disabled for this view.'))
    try { createApiReference(mount, config) } catch (error) { post({ type: 'openapi-frame-error', message: String(error) }); return }
    post({ type: 'openapi-frame-ready' })
  })
  // Scalar's bundle runs after this script; the configuration is asked for once it has.
  addEventListener('DOMContentLoaded', () => post({ type: 'openapi-frame-loaded' }))
})()
