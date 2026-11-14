// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import { MarkdownEditor } from './index'
import type { MarkdownPlugin } from '../core'
import { registerCollaborationAdapter, type CollaborationUpdate } from '../core/internal'
let app: App | undefined

afterEach(() => { app?.unmount(); app = undefined; document.body.replaceChildren(); vi.unstubAllGlobals() })

it('keeps editing chrome mounted but disabled through offline, reconnecting and failure states; explicit readonly stays minimal', async () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  let publish!: (state: CollaborationUpdate) => void
  const adapter = registerCollaborationAdapter(update => {
    publish = update
    return { extensions: [], isRemoteTransaction: () => false, destroy() {}, detachEditor() {},
      attachEditor() { update({ hydrated: true, writable: true, connection: 'online' }) } }
  })
  const readonly = ref(false)
  const plugin: MarkdownPlugin = { id: 'geometry-actions', apiVersion: 1, create: () => ({ actions: {
    toolbar: [{ id: 'paragraph', label: 'Paragraph', command: { type: 'set-paragraph' } }],
  } }) }
  const host = document.createElement('div'); document.body.append(host)
  app = createApp({ render: () => h(MarkdownEditor, { collaboration: adapter, profile: 'rich',
    ariaLabel: 'Stable layout', readonly: readonly.value, plugins: [plugin], toolbarPresentation: 'insert-menu', showPresence: true }) })
  app.mount(host); await nextTick()
  const toolbar = host.querySelector('.md-plugin-toolbar')
  const footer = host.querySelector('.paper-footer')
  const presence = host.querySelector('.md-editor-presence')
  expect(presence?.getAttribute('aria-hidden')).toBe('true')
  publish({ peers: [{ clientId: 10, name: 'Peer', color: '#123456' }] }); await nextTick()
  expect(host.querySelector('.md-editor-presence')).toBe(presence)
  expect(presence?.getAttribute('aria-hidden')).toBeNull()
  publish({ peers: [] }); await nextTick()
  expect(host.querySelector('.md-editor-presence')).toBe(presence)
  expect(presence?.getAttribute('aria-hidden')).toBe('true')
  const content = host.querySelector('.ProseMirror')
  expect(toolbar).not.toBeNull(); expect(footer).not.toBeNull()
  // Connection feedback is an overlay anchored to the shell. If any of it ever becomes a flow
  // sibling of the content again, the document moves on every reconnect — the flicker this guards
  // against. The anchors stay mounted in every state; whatever appears does so inside them.
  const overlay = host.querySelector('.md-editor-connection')
  const liveRegion = host.querySelector('.md-collaboration-live')
  const dot = host.querySelector('.md-link-dot')
  expect(overlay).not.toBeNull(); expect(liveRegion).not.toBeNull(); expect(dot).not.toBeNull()
  // The column owns the right-hand corner; the left gutter belongs to the block handle.
  expect(dot!.parentElement).toBe(overlay)
  expect(liveRegion!.parentElement).toBe(overlay)
  for (const connection of ['offline', 'reconnecting', 'offline'] as const) {
    publish({ connection, writable: false })
    await nextTick()
    expect(host.querySelector('.md-collaboration-live')).toBe(liveRegion)
    expect(host.querySelector('.md-link-dot')).toBe(dot)
    expect(host.querySelector('[data-testid="collaboration-notice"]')?.parentElement ?? liveRegion).toBe(liveRegion)
    expect(host.querySelector('.md-plugin-toolbar')).toBe(toolbar)
    expect(host.querySelector('.paper-footer')).toBe(footer)
    expect(host.querySelector('.md-editor')?.classList.contains('readonly')).toBe(false)
    expect(host.querySelector<HTMLButtonElement>('.md-insert-trigger')?.disabled).toBe(true)
    // Reading in fullscreen needs no connection.
    expect(host.querySelector<HTMLButtonElement>('.paper-footer-fs')?.disabled).toBe(false)
    expect(content?.getAttribute('contenteditable')).toBe('false')
  }
  publish({ connection: 'online', writable: true }); await nextTick()
  expect(host.querySelector('.ProseMirror')).toBe(content)
  expect(host.querySelector<HTMLButtonElement>('.md-insert-trigger')?.disabled).toBe(false)
  host.querySelector<HTMLButtonElement>('.paper-footer-fs')!.click(); await nextTick()
  const fullscreen = document.querySelector('.md-editor[data-fullscreen="true"]')
  expect(fullscreen).not.toBeNull()
  publish({ connection: 'offline', writable: false, error: { kind: 'collaboration', message: 'Unavailable', recoverable: false } })
  await nextTick()
  expect(document.querySelector('.md-editor[data-fullscreen="true"]')).toBe(fullscreen)
  expect(document.querySelector('.md-plugin-toolbar')).toBe(toolbar)
  expect(document.querySelector('.paper-footer')).toBe(footer)
  expect(document.querySelector<HTMLButtonElement>('.md-insert-trigger')?.disabled).toBe(true)
  const fullscreenExit = document.querySelector<HTMLButtonElement>('.paper-footer-fs')!
  expect(fullscreenExit.disabled).toBe(false)
  fullscreenExit.click(); await nextTick()
  expect(document.querySelector('.md-editor[data-fullscreen="true"]')).toBeNull()
  publish({ connection: 'online', writable: true, peers: [{ clientId: 10, name: 'Peer', color: '#123456' }] }); await nextTick()
  readonly.value = true; await nextTick()
  expect(host.querySelector('.md-plugin-toolbar')).toBeNull()
  // A reading feature stays: the footer keeps „Celá obrazovka“ alone, in its place (a locked page moves nothing).
  expect(host.querySelector('.paper-footer')).toBe(footer)
  expect([...footer!.children].map(child => child.className)).toEqual(['paper-footer-fs'])
  expect(host.querySelector('.md-editor')?.classList.contains('readonly')).toBe(true)
  expect(host.querySelector('.md-editor-presence')).not.toBeNull()
  expect(host.querySelector('.md-editor-presence')?.getAttribute('aria-hidden')).toBeNull()
})

it('emits an initial collaboration failure before created so hosts can offer retry', async () => {
  let publish!: (state: CollaborationUpdate) => void
  const adapter = registerCollaborationAdapter(update => {
    publish = update
    return { extensions: [], isRemoteTransaction: () => false, destroy() {}, detachEditor() {}, attachEditor() {} }
  })
  const created = vi.fn()
  const failed = vi.fn()
  const host = document.createElement('div'); document.body.append(host)
  app = createApp({ render: () => h(MarkdownEditor, { collaboration: adapter, profile: 'rich',
    ariaLabel: 'Failed startup', variant: 'compact', onCreated: created, onFailed: failed }) })
  app.mount(host); await nextTick()
  const error = { kind: 'collaboration' as const, message: 'Unauthorized', recoverable: false }
  publish({ connection: 'offline', writable: false, error }); await nextTick()
  expect(failed).toHaveBeenCalledExactlyOnceWith(error)
  expect(created).not.toHaveBeenCalled()
  expect(host.querySelector('[role="status"]')?.textContent).toContain('Dokument nelze upravovat')
  publish({ connection: 'offline' }); await nextTick()
  expect(failed).toHaveBeenCalledTimes(1)
})

it.each(['expanded', 'hidden'] as const)('preserves the %s toolbar option when collaboration is paused', async toolbarPresentation => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  let publish!: (state: CollaborationUpdate) => void
  const adapter = registerCollaborationAdapter(update => {
    publish = update
    return { extensions: [], isRemoteTransaction: () => false, destroy() {}, detachEditor() {},
      attachEditor() { update({ hydrated: true, writable: true, connection: 'online' }) } }
  })
  const plugin: MarkdownPlugin = { id: 'offline-actions', apiVersion: 1, create: () => ({ actions: {
    toolbar: [{ id: 'bold', label: 'Bold', command: { type: 'toggle-bold' } }],
  } }) }
  const host = document.createElement('div'); document.body.append(host)
  app = createApp({ render: () => h(MarkdownEditor, { collaboration: adapter, profile: 'rich',
    ariaLabel: 'Toolbar presentation', plugins: [plugin], toolbarPresentation }) })
  app.mount(host); await nextTick()
  publish({ hydrated: true, writable: true, connection: 'online' }); await nextTick()
  const toolbar = host.querySelector('.md-plugin-toolbar')
  const action = host.querySelector<HTMLButtonElement>('[data-command="bold"]')
  if (toolbarPresentation === 'expanded') {
    expect(toolbar).not.toBeNull()
    expect(action?.disabled).toBe(false)
  } else expect(toolbar).toBeNull()
  publish({ connection: 'offline', writable: false }); await nextTick()
  expect(host.querySelector('.md-plugin-toolbar')).toBe(toolbar)
  if (toolbarPresentation === 'expanded') expect(action?.disabled).toBe(true)
  publish({ connection: 'online', writable: true }); await nextTick()
  if (toolbarPresentation === 'expanded') expect(action?.disabled).toBe(false)
  else expect(host.querySelector('.md-plugin-toolbar')).toBeNull()
})
