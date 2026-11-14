import type { Editor } from '@tiptap/core'
import { editorForController } from './internal'
import type { MarkdownPluginController } from './plugins/types'
import type { MarkdownEditorController } from './types'

export const markdownOverlayOwnerAttribute = 'data-md-overlay-owner'
export const markdownOverlayForAttribute = 'data-md-overlay-for'
export const markdownOverlayConsumesEscapeAttribute = 'data-md-overlay-consumes-escape'
export const markdownOverlayModalAttribute = 'data-md-overlay-modal'

export interface MarkdownOverlayOptions {
  /** The overlay handles Escape before the fullscreen surface. Default true. */
  readonly consumesEscape?: boolean
  /** The overlay temporarily owns focus and Tab navigation. Default false. */
  readonly modal?: boolean
}

let nextOwner = 0
const generatedOwners = new WeakMap<Element, { id: string; references: number }>()

/** Stable identity used to associate body-level portals with one editor surface. */
export function createMarkdownOverlayOwnerId(): string {
  nextOwner += 1
  return `markdown-editor-${nextOwner}`
}

export function markdownOverlayOwnerOf(element: Element | null | undefined): string | null {
  return element?.closest(`[${markdownOverlayOwnerAttribute}]`)?.getAttribute(markdownOverlayOwnerAttribute) ?? null
}

/** Connected portal roots explicitly owned by this editor surface. */
export function markdownOverlaysFor(ownerElement: Element | null | undefined): HTMLElement[] {
  const owner = markdownOverlayOwnerOf(ownerElement)
  if (!owner) return []
  const ownerDocument = ownerElement?.ownerDocument
  if (!ownerDocument) return []
  return [...ownerDocument.querySelectorAll<HTMLElement>(`[${markdownOverlayForAttribute}]`)]
    .filter(overlay => overlay.getAttribute(markdownOverlayForAttribute) === owner)
}

function acquireOwner(ownerElement: Element): { id: string; release: () => void } {
  const generated = generatedOwners.get(ownerElement)
  if (generated) {
    generated.references += 1
    return { id: generated.id, release: () => releaseGeneratedOwner(ownerElement, generated) }
  }
  const inherited = markdownOverlayOwnerOf(ownerElement)
  if (inherited) return { id: inherited, release: () => undefined }
  const state = { id: createMarkdownOverlayOwnerId(), references: 1 }
  generatedOwners.set(ownerElement, state)
  ownerElement.setAttribute(markdownOverlayOwnerAttribute, state.id)
  return { id: state.id, release: () => releaseGeneratedOwner(ownerElement, state) }
}

function releaseGeneratedOwner(ownerElement: Element, state: { id: string; references: number }): void {
  if (generatedOwners.get(ownerElement) !== state || --state.references > 0) return
  generatedOwners.delete(ownerElement)
  if (ownerElement.getAttribute(markdownOverlayOwnerAttribute) === state.id) ownerElement.removeAttribute(markdownOverlayOwnerAttribute)
}

/**
 * Associate a body-level popup or dialog with the editor that opened it. Fullscreen focus
 * isolation uses this marker so one editor never adopts another editor's portal.
 */
export function registerMarkdownOverlay(controller: MarkdownPluginController, overlay: HTMLElement, options?: MarkdownOverlayOptions): () => void
export function registerMarkdownOverlay(editor: Editor, overlay: HTMLElement, options?: MarkdownOverlayOptions): () => void
export function registerMarkdownOverlay(
  owner: MarkdownPluginController | Editor,
  overlay: HTMLElement,
  options?: MarkdownOverlayOptions,
): () => void {
  const editor = 'view' in owner ? owner : editorForController(owner as MarkdownEditorController)
  if (!editor || editor.isDestroyed) throw new Error('Attach the Markdown editor view before registering an overlay.')
  return registerMarkdownOverlayForElement(editor.view.dom, overlay, options)
}

/** DOM-level registration for reader overlays, including static renderers without an Editor. */
export function registerMarkdownOverlayForElement(
  ownerElement: Element | null | undefined,
  overlay: HTMLElement,
  options: MarkdownOverlayOptions = {},
): () => void {
  if (!ownerElement) return () => undefined
  if (ownerElement.ownerDocument !== overlay.ownerDocument) {
    throw new TypeError('Markdown overlays must be mounted in the editor ownerDocument.')
  }
  if (overlay.hasAttribute(markdownOverlayForAttribute)) {
    throw new Error('A Markdown overlay may only be registered once at a time.')
  }
  const owner = acquireOwner(ownerElement)
  const previous = new Map([
    [markdownOverlayForAttribute, overlay.getAttribute(markdownOverlayForAttribute)],
    [markdownOverlayConsumesEscapeAttribute, overlay.getAttribute(markdownOverlayConsumesEscapeAttribute)],
    [markdownOverlayModalAttribute, overlay.getAttribute(markdownOverlayModalAttribute)],
  ])
  overlay.setAttribute(markdownOverlayForAttribute, owner.id)
  overlay.setAttribute(markdownOverlayConsumesEscapeAttribute, String(options.consumesEscape ?? true))
  overlay.setAttribute(markdownOverlayModalAttribute, String(options.modal ?? false))
  let released = false
  return () => {
    if (released) return
    released = true
    if (overlay.getAttribute(markdownOverlayForAttribute) === owner.id) {
      for (const [attribute, value] of previous) {
        if (value === null) overlay.removeAttribute(attribute)
        else overlay.setAttribute(attribute, value)
      }
    }
    owner.release()
  }
}
