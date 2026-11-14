import type { MarkdownImportDegradedPart, MarkdownImportInfo } from './types'

export const markdownInputErrorEvent = 'markdown-input-error'
/** Non-blocking: a paste or `.md` drop kept some constructs as literal text. */
export const markdownImportNoticeEvent = 'markdown-import-notice'

export interface MarkdownInputErrorDetail {
  readonly message: string
  readonly source: 'paste' | 'transaction'
}

/** A colleague removed the block the caret was in, with text written there (contract J10.6): `restore` brings it back. */
export const markdownRemoteRemovalEvent = 'markdown-remote-removal'
export interface MarkdownRemoteRemovalDetail {
  /** „Jana Nováková odebral(a) blok, ve kterém jste psal(a).“ */
  readonly message: string
  /** Puts the block with the local text back at the nearest place, one undo step; false when the editor can no longer take it. */
  readonly restore: () => boolean
}

export interface MarkdownImportNoticeDetail {
  readonly source: MarkdownImportInfo['source']
  readonly fileName?: string
  readonly degraded: readonly MarkdownImportDegradedPart[]
}

function dispatch(target: HTMLElement, type: string, detail: unknown): void {
  const EventConstructor = target.ownerDocument.defaultView?.CustomEvent
  if (EventConstructor) target.dispatchEvent(new EventConstructor(type, { bubbles: true, detail }))
}

/** Input rejection is recoverable and must not replace the current document. */
export function dispatchMarkdownInputError(target: HTMLElement, detail: MarkdownInputErrorDetail): void {
  dispatch(target, markdownInputErrorEvent, detail)
}

export function dispatchMarkdownImportNotice(target: HTMLElement, detail: MarkdownImportNoticeDetail): void {
  dispatch(target, markdownImportNoticeEvent, detail)
}
