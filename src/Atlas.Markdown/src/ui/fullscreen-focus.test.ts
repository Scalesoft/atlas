// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import PoolEditor from '../PoolEditor.vue'
import MarkdownEditor from '../vue/MarkdownEditor.vue'

const apps: App[] = []
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})

it('keeps the remaining fullscreen owner interactive and restores host inert state after teardown', async () => {
  // jsdom has no layout observer; the fullscreen lifecycle, not block geometry, is under test.
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
  const background = document.createElement('button')
  background.textContent = 'Host action'
  const previouslyInert = document.createElement('div')
  previouslyInert.inert = true
  document.body.append(background, previouslyInert)
  for (const render of [
    () => h(MarkdownEditor, { modelValue: 'Text', ariaLabel: 'Document' }),
    () => h(PoolEditor, { modelValue: 'Text', fullscreenLabel: 'Document', hideBlockHandle: true }),
  ]) {
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render })
    apps.push(app)
    app.mount(host)
  }
  await nextTick()
  const toggles = [...document.querySelectorAll<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')]
  toggles[0].click()
  await nextTick()
  toggles[1].click()
  await nextTick()
  const dialogs = [...document.querySelectorAll<HTMLElement>('[role="dialog"]')]
  expect(dialogs).toHaveLength(2)
  const modern = dialogs.find(dialog => dialog.classList.contains('paper'))!
  const legacy = dialogs.find(dialog => !dialog.classList.contains('paper'))!
  expect(modern.inert).toBe(true)
  expect(legacy.contains(document.activeElement)).toBe(true)
  apps.pop()!.unmount()
  await nextTick()
  expect(modern.inert).not.toBe(true)
  expect(modern.contains(document.activeElement)).toBe(true)
  expect(background.inert).toBe(true)
  expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(true)
  apps.pop()!.unmount()
  await nextTick()
  expect(background.inert).not.toBe(true)
  expect(previouslyInert.inert).toBe(true)
  expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(false)
  background.focus()
  expect(document.activeElement).toBe(background)
})
