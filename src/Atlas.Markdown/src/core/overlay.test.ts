// @vitest-environment jsdom

import { expect, it } from 'vitest'
import type { Editor } from '@tiptap/core'
import {
  markdownOverlayConsumesEscapeAttribute, markdownOverlayForAttribute, markdownOverlayModalAttribute,
  registerMarkdownOverlay, registerMarkdownOverlayForElement,
} from './overlay'
import { registerControllerEngine } from './internal'
import type { MarkdownEditorController } from './types'
import type { MarkdownPluginController } from './plugins/types'

it('associates a body-level overlay with exactly one editor surface', () => {
  const first = document.createElement('div')
  first.setAttribute('data-md-overlay-owner', 'first')
  const editorDom = document.createElement('div')
  first.append(editorDom)
  const overlay = document.createElement('div')

  const release = registerMarkdownOverlay({ view: { dom: editorDom } } as never, overlay)

  expect(overlay.getAttribute(markdownOverlayForAttribute)).toBe('first')
  release()
  expect(overlay.hasAttribute(markdownOverlayForAttribute)).toBe(false)
})

it('registers an overlay through the public controller without exposing its editor', () => {
  const editorDom = document.createElement('div')
  editorDom.setAttribute('data-md-overlay-owner', 'controller-owner')
  const controller = {} as MarkdownPluginController
  registerControllerEngine(controller as MarkdownEditorController, () => ({ view: { dom: editorDom }, isDestroyed: false }) as unknown as Editor)
  const overlay = document.createElement('div')

  const release = registerMarkdownOverlay(controller, overlay)

  expect(overlay.getAttribute(markdownOverlayForAttribute)).toBe('controller-owner')
  release()
  expect(overlay.hasAttribute(markdownOverlayForAttribute)).toBe(false)
})

it('creates and releases an owner for custom editor shells', () => {
  const owner = document.createElement('div')
  const overlay = document.createElement('div')
  const release = registerMarkdownOverlayForElement(owner, overlay, { consumesEscape: false, modal: true })
  expect(overlay.getAttribute(markdownOverlayForAttribute)).toMatch(/^markdown-editor-/)
  expect(overlay.getAttribute(markdownOverlayConsumesEscapeAttribute)).toBe('false')
  expect(overlay.getAttribute(markdownOverlayModalAttribute)).toBe('true')
  expect(owner.getAttribute('data-md-overlay-owner')).toBe(overlay.getAttribute(markdownOverlayForAttribute))
  release()
  expect(owner.hasAttribute('data-md-overlay-owner')).toBe(false)
  expect(overlay.hasAttribute(markdownOverlayForAttribute)).toBe(false)
})

it('rejects a portal from a different document instead of silently losing ownership', () => {
  const frame = document.createElement('iframe')
  document.body.append(frame)
  const frameDocument = frame.contentDocument!
  const owner = frameDocument.createElement('div')
  expect(() => registerMarkdownOverlayForElement(owner, document.createElement('div'))).toThrow(/ownerDocument/)
  frame.remove()
})

it('rejects competing ownership of the same overlay', () => {
  const first = document.createElement('div')
  const second = document.createElement('div')
  const overlay = document.createElement('div')
  const release = registerMarkdownOverlayForElement(first, overlay)
  expect(() => registerMarkdownOverlayForElement(second, overlay)).toThrow(/only be registered once/)
  release()
})
