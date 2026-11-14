import { markdownStrings } from '../../core'
import { openapiStrings } from './strings'
import type { OpenApiData } from './data'
import type { OpenApiPluginOptions } from './index'

/** Runtime options of one reference view; never stored in the document. */
export type OpenApiReferenceOptions = Pick<OpenApiPluginOptions, 'allowRequests' | 'proxyUrl' | 'customFetch'>

/** Longest wait for the Scalar bundle before the view offers a retry. A late success still renders. */
export const OPENAPI_REFERENCE_TIMEOUT_MS = 30_000

/**
 * Mounts the Scalar reference for `data` into `host` and returns its disposer. The view shows its own
 * loading and error state (with retry) and dispatches `openapi-render-ready` / `openapi-render-error`
 * (`detail`: message) on `host`. The frame code and the Scalar bundle load only on this call.
 */
export function mountOpenApiReference(host: HTMLElement, data: OpenApiData, options: OpenApiReferenceOptions = {}): () => void {
  const doc = host.ownerDocument
  let stopped = false
  let attempt = 0
  let status: HTMLElement | undefined
  let timer: ReturnType<typeof setTimeout> | undefined
  let disposeFrame: (() => void) | undefined
  const settle = () => { if (timer !== undefined) clearTimeout(timer); timer = undefined }
  const show = (kind: 'loading' | 'error', text: string) => {
    // A fresh live region per state, so assistive technology announces the change.
    const next = doc.createElement('div')
    next.className = kind === 'error' ? 'md-openapi-status is-error' : 'md-openapi-status'
    next.setAttribute('role', kind === 'error' ? 'alert' : 'status')
    const message = doc.createElement('p'); message.textContent = text; next.append(message)
    if (kind === 'error') {
      const retry = doc.createElement('button'); retry.type = 'button'; retry.className = 'md-btn'
      retry.textContent = markdownStrings.retry; retry.onclick = start; next.append(retry)
    }
    if (status?.isConnected) status.replaceWith(next); else host.prepend(next)
    status = next
  }
  const fail = (token: number, text: string, keepFrame = false) => {
    if (stopped || token !== attempt) return
    settle()
    if (!keepFrame) { disposeFrame?.(); disposeFrame = undefined }
    show('error', text)
    host.dispatchEvent(new CustomEvent('openapi-render-error', { detail: text }))
  }
  const ready = (token: number) => {
    if (stopped || token !== attempt) return
    settle(); status?.remove(); status = undefined
    host.dispatchEvent(new CustomEvent('openapi-render-ready'))
  }
  function start() {
    if (stopped) return
    const token = ++attempt
    settle(); disposeFrame?.(); disposeFrame = undefined
    show('loading', openapiStrings.loadingDocumentation)
    // Keep the frame on timeout: a slow network may still deliver the bundle and render.
    timer = setTimeout(() => fail(token, openapiStrings.timeoutError, true), OPENAPI_REFERENCE_TIMEOUT_MS)
    import('./scalarView').then(({ mountScalarFrame }) => {
      if (stopped || token !== attempt) return
      disposeFrame = mountScalarFrame(host, data, options, { onReady: () => ready(token), onError: message => fail(token, message) })
    }, (cause: unknown) => fail(token, openapiStrings.loadFailedError(cause instanceof Error ? cause.message : String(cause))))
  }
  start()
  return () => {
    if (stopped) return
    stopped = true; settle()
    disposeFrame?.(); disposeFrame = undefined
    status?.remove(); status = undefined
  }
}
