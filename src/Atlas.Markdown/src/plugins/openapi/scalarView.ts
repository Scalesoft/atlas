// Scalar updates window.history while scrolling and navigating. Its self-contained browser bundle
// (an IIFE that sets window.Scalar) runs in a child realm so each reference owns its own URL and UI
// state. The build emits the bundle as a separate hashed asset whose URL is resolved from
// import.meta.url; the browser fetches it only when a reader opens a reference.
import scalarBundleUrl from './scalar-standalone?url'
import scalarFrameUrl from './scalarFrame.js?url&no-inline'
import { openapiStrings } from './runtimeStrings'
import type { OpenApiPluginOptions } from './index'
import type { OpenApiData } from './data'

export interface ScalarFrameCallbacks {
  onReady(): void
  onError(message: string): void
}

const attribute = (value: string) => value.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;')

/**
 * Loaded only after a reader opens a reference. Each call owns one isolated frame. The frame has an opaque origin (sandbox without
 * allow-same-origin, M3): the writer's spec renders without the host's origin, cookies or storage, so a flaw in Scalar's rendering
 * cannot act as the reader. The two scripts of the frame are files (the host's CSP has no inline-script exception) and classic (a
 * module would need CORS from the opaque origin); scalarFrame.js receives the configuration by postMessage and answers the same way.
 * A host `customFetch` cannot cross into the frame: with `allowRequests`, Try It sends Scalar's own requests from the opaque origin.
 */
export function mountScalarFrame(host: HTMLElement, data: OpenApiData, options: OpenApiPluginOptions, callbacks: ScalarFrameCallbacks): () => void {
  const doc = host.ownerDocument
  const view = doc.defaultView
  const frame = doc.createElement('iframe')
  frame.className = 'md-openapi-frame'
  frame.title = openapiStrings.frameTitle(data.filename)
  frame.setAttribute('sandbox', 'allow-scripts')
  // Scalar's copy buttons (code samples, "Copy as Markdown"): a write on the reader's click, never a read.
  frame.allow = 'clipboard-write'
  // `about:srcdoc` inherits the parent's base URL; absolute URLs keep the reference independent of that and of the host's sub-path.
  const script = (url: string) => `<script src="${attribute(new URL(url, doc.baseURI).href)}"></script>`
  frame.srcdoc = '<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>'
    + `<body style="margin:0"><div id="reference"></div>${script(scalarFrameUrl)}${script(scalarBundleUrl)}</body></html>`
  let stopped = false
  const fail = (message: string) => { if (!stopped) callbacks.onError(message) }
  const allowRequests = options.allowRequests !== false
  const configuration: Record<string, unknown> = {
    content: data.source,
    baseServerURL: new URL(doc.baseURI).origin,
    showSidebar: true,
    modelsSectionLabel: openapiStrings.scalarModelsLabel,
    hideModels: false,
    hideClientButton: true,
    hideTestRequestButton: !allowRequests,
    documentDownloadType: 'none',
    showDeveloperTools: 'never',
    withDefaultFonts: false,
    persistAuth: false,
    telemetry: false,
    agent: { disabled: true },
    ...(options.proxyUrl ? { proxyUrl: options.proxyUrl } : {}),
  }
  // Only this frame's messages count; what it may say is harmless to the host (ready, failed, close the dialog).
  const onMessage = (event: MessageEvent) => {
    if (stopped || event.source !== frame.contentWindow) return
    const message = event.data as { type?: unknown; message?: unknown } | null
    switch (message?.type) {
      case 'openapi-frame-loaded': frame.contentWindow?.postMessage({ type: 'openapi-frame-config', configuration, allowRequests }, '*'); break
      case 'openapi-frame-ready': callbacks.onReady(); break
      case 'openapi-frame-missing': fail(openapiStrings.scalarLoadFailed); break
      case 'openapi-frame-error': fail(openapiStrings.scalarRenderFailed(String(message.message))); break
      // Keys typed in the frame never reach the host's dialog: Esc asks the dialog around the frame to close, as it does everywhere else.
      case 'openapi-frame-escape': frame.closest('dialog')?.dispatchEvent(new Event('cancel', { cancelable: true })); break
    }
  }
  view?.addEventListener('message', onMessage)
  host.append(frame)
  return () => {
    if (stopped) return
    stopped = true; view?.removeEventListener('message', onMessage)
    frame.remove()
  }
}
