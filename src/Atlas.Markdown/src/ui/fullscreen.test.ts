// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import PoolEditor from '../PoolEditor.vue'
import MarkdownEditor from '../vue/MarkdownEditor.vue'
import { acquireFullscreenLock } from './fullscreen'

const apps: App[] = []
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  document.body.replaceChildren()
  document.body.removeAttribute('data-editor-fullscreen')
})

it('keeps page scrolling locked until the last fullscreen editor leaves', async () => {
  for (let index = 0; index < 2; index++) {
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => h(PoolEditor, { modelValue: 'Text', hideBlockHandle: true }) })
    apps.push(app)
    app.mount(host)
  }
  await nextTick()
  const buttons = [...document.querySelectorAll<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')]
  buttons[0].click()
  buttons[1].click()
  await nextTick()
  expect(document.querySelectorAll('.md-editor.is-fullscreen')).toHaveLength(2)
  apps.shift()!.unmount()
  await nextTick()
  expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(true)
  buttons[1].click()
  await nextTick()
  expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(false)
})

it('shows the document title and a labelled close button in the fullscreen header', async () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  const host = document.createElement('div')
  document.body.append(host)
  const app = createApp({ render: () => h(MarkdownEditor, { modelValue: 'Text', ariaLabel: 'Obsah stránky', documentTitle: 'Moje stránka' }) })
  apps.push(app)
  app.mount(host)
  await nextTick()
  host.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
  await nextTick()
  // Fullscreen teleports the editor to <body>, so it is no longer inside `host`.
  expect(document.querySelector('.md-fs-header-label')?.textContent).toBe('Moje stránka')
  const close = document.querySelector<HTMLButtonElement>('.md-fs-header-close')!
  expect(close.getAttribute('aria-label')).toBe('Zavřít')
  expect(document.querySelector('.md-fs-header-actions kbd')?.textContent).toBe('Esc')
  close.click()
  await nextTick()
  expect(document.querySelector('.md-editor.is-fullscreen')).toBeNull()
  vi.unstubAllGlobals()
})

it('isolates fullscreen ownership per document realm', () => {
  const iframe = document.createElement('iframe')
  const mainOwner = document.createElement('div')
  const mainBackground = document.createElement('button')
  document.body.append(mainOwner, mainBackground, iframe)
  const frameDocument = iframe.contentDocument!
  const frameOwner = frameDocument.createElement('div')
  const frameBackground = frameDocument.createElement('button')
  frameDocument.body.append(frameOwner, frameBackground)

  const releaseMain = acquireFullscreenLock(mainOwner, () => undefined, null)
  const releaseFrame = acquireFullscreenLock(frameOwner, () => undefined, null)
  expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(true)
  expect(frameDocument.body.hasAttribute('data-editor-fullscreen')).toBe(true)
  expect(mainBackground.inert).toBe(true)
  expect(frameBackground.inert).toBe(true)

  releaseMain(false)
  expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(false)
  expect(frameDocument.body.hasAttribute('data-editor-fullscreen')).toBe(true)
  expect(frameBackground.inert).toBe(true)
  releaseFrame(false)
  expect(frameDocument.body.hasAttribute('data-editor-fullscreen')).toBe(false)
})
