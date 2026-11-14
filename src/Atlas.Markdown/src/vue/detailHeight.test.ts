// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import { MarkdownDetailWorkspace } from './index'

let app: App | undefined
afterEach(() => { app?.unmount(); app = undefined; document.body.replaceChildren(); vi.unstubAllGlobals() })

it('fills the visible height below its header and follows window resizes', async () => {
  Element.prototype.scrollIntoView = () => {}
  vi.stubGlobal('innerHeight', 900)
  const open = ref(false)
  const host = document.createElement('div'); document.body.append(host)
  app = createApp({ render: () => h(MarkdownDetailWorkspace, { open: open.value, active: 'api', title: 'openapi.yaml',
    tabs: [{ id: 'document', label: 'Dokument' }, { id: 'api', label: 'API' }] }, { document: () => h('p', 'Dokument'), api: () => h('p', 'API') }) })
  app.mount(host)
  const anchor = host.querySelector<HTMLElement>('.md-detail-anchor')!
  const pages = host.querySelector<HTMLElement>('.md-detail-pages')!
  anchor.style.scrollMarginTop = '40px'
  // 36 px of the workspace header above the pages.
  anchor.getBoundingClientRect = () => ({ top: 100 }) as DOMRect
  pages.getBoundingClientRect = () => ({ top: 136 }) as DOMRect
  open.value = true
  await vi.waitFor(() => expect(anchor.style.getPropertyValue('--workspace-height')).toBe(`${900 - 40 - 36 - 12}px`))
  vi.stubGlobal('innerHeight', 300)
  window.dispatchEvent(new Event('resize'))
  await nextTick()
  expect(anchor.style.getPropertyValue('--workspace-height')).toBe('320px')
})

// UX ED-7: Esc in the API or Zdroj view dropped focus on <body> when the button that opened it (the block's toolbar, the
// Dokument | Zdroj switch) was gone by then. The document's text takes it instead, where Enter reopens the block.
it('returns focus to the document when the opener is gone', async () => {
  Element.prototype.scrollIntoView = () => {}
  const open = ref(false)
  const host = document.createElement('div'); document.body.append(host)
  const opener = document.createElement('button'); document.body.append(opener); opener.focus()
  app = createApp({ render: () => h(MarkdownDetailWorkspace, { open: open.value, active: 'api', title: 'a', tabs: [{ id: 'document', label: 'Dokument' }, { id: 'api', label: 'API' }] },
    { document: () => h('div', { class: 'ProseMirror', tabindex: 0 }, 'Text'), api: () => h('p', 'API') }) })
  app.mount(host)
  open.value = true
  await vi.waitFor(() => expect(document.activeElement?.getAttribute('role')).toBe('tab'))
  opener.remove()
  open.value = false
  await vi.waitFor(() => expect(document.activeElement?.classList.contains('ProseMirror')).toBe(true), { timeout: 3000 })
})

it('is bounded by a scrolling host pane instead of the window', async () => {
  Element.prototype.scrollIntoView = () => {}
  vi.stubGlobal('innerHeight', 1200)
  const open = ref(false)
  const pane = document.createElement('main'); pane.style.overflowY = 'auto'; document.body.append(pane)
  Object.defineProperty(pane, 'clientHeight', { value: 700 }); Object.defineProperty(pane, 'scrollHeight', { value: 3000 })
  const host = document.createElement('div'); pane.append(host)
  app = createApp({ render: () => h(MarkdownDetailWorkspace, { open: open.value, active: 'api', title: 'a', tabs: [{ id: 'document', label: 'Dokument' }, { id: 'api', label: 'API' }] }) })
  app.mount(host)
  open.value = true
  const anchor = host.querySelector<HTMLElement>('.md-detail-anchor')!
  await vi.waitFor(() => expect(anchor.style.getPropertyValue('--workspace-height')).toBe(`${700 - 12}px`))
})
