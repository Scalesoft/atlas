// @vitest-environment jsdom

import { createApp, h, nextTick, ref, type App } from 'vue'
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import PoolEditor from '../PoolEditor.vue'
import MarkdownEditor from '../vue/MarkdownEditor.vue'
import { createYouTrackPlugin } from '../plugins/youtrack'
import { registerMarkdownOverlayForElement } from '../core/overlay'
import { acquireFullscreenLock, hasActiveEditorOverlay, hasActiveModalDialog } from './fullscreen'

// The YouTrack picker's lazy runtime: imported cold inside the 1 s wait for its dialog, it missed it in a loaded editor suite.
beforeAll(async () => { await import('../plugins/youtrack/pickerRuntime') })

const releases: Array<() => void> = []
const apps: App[] = []

function dialogWithInput() {
  const dialog = document.createElement('dialog')
  dialog.inert = false
  dialog.setAttribute('aria-modal', 'true')
  const input = document.createElement('input')
  dialog.append(input)
  return { dialog, input }
}

function openDialog(dialog: HTMLDialogElement) {
  // jsdom has no native showModal focusing steps or top layer. Exercise the
  // semantic fallback while emitting the same beforetoggle/open transition.
  const before = new Event('beforetoggle')
  Object.defineProperty(before, 'newState', { value: 'open' })
  dialog.dispatchEvent(before)
  dialog.open = true
}

function markOwner(shell: HTMLElement, id: string) {
  shell.setAttribute('data-md-overlay-owner', id)
  return shell
}

afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  releases.splice(0).forEach(release => release())
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})

describe('fullscreen and native modal ownership', () => {
  it('allows an existing modal dialog to take focus and Tab ownership, then resumes the fullscreen trap', async () => {
    const shell = markOwner(document.createElement('div'), 'first-editor')
    const editorInput = document.createElement('input')
    shell.append(editorInput)
    const background = document.createElement('button')
    document.body.append(shell, background)
    const release = acquireFullscreenLock(shell, () => editorInput.focus(), null)
    releases.push(release)

    const { dialog, input } = dialogWithInput()
    releases.push(registerMarkdownOverlayForElement(shell, dialog, { modal: true }))
    document.body.append(dialog)

    openDialog(dialog)
    expect(dialog.inert).toBe(false)
    input.focus()
    await Promise.resolve()
    expect(hasActiveModalDialog(shell)).toBe(true)
    expect(document.activeElement).toBe(input)
    expect(dialog.inert).toBe(false)
    expect(background.inert).toBe(true)
    const tab = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true })
    input.dispatchEvent(tab)
    expect(tab.defaultPrevented).toBe(false)
    expect(document.activeElement).toBe(input)

    dialog.open = false
    await Promise.resolve()
    expect(hasActiveModalDialog(shell)).toBe(false)
    background.focus()
    expect(document.activeElement).toBe(editorInput)
    release()
    expect(dialog.inert).toBe(false)
    expect(background.inert).not.toBe(true)
  })

  it('does not inert a modal inserted by a plugin after fullscreen starts', async () => {
    const shell = markOwner(document.createElement('div'), 'plugin-editor')
    const editorInput = document.createElement('input')
    shell.append(editorInput)
    document.body.append(shell)
    releases.push(acquireFullscreenLock(shell, () => editorInput.focus(), null))
    const { dialog, input } = dialogWithInput()
    releases.push(registerMarkdownOverlayForElement(shell, dialog, { modal: true }))
    document.body.append(dialog)
    openDialog(dialog)
    input.focus()
    await Promise.resolve()
    expect(dialog.inert).toBe(false)
    expect(document.activeElement).toBe(input)
  })

  it('recognizes a registered ARIA modal without requiring a native dialog element', () => {
    const shell = markOwner(document.createElement('div'), 'aria-modal-editor')
    const modal = document.createElement('div')
    modal.setAttribute('role', 'dialog')
    modal.setAttribute('aria-modal', 'true')
    releases.push(registerMarkdownOverlayForElement(shell, modal, { modal: true }))
    document.body.append(shell, modal)
    releases.push(acquireFullscreenLock(shell, () => undefined, null))

    expect(hasActiveModalDialog(shell)).toBe(true)
    expect(modal.inert).not.toBe(true)
  })

  it('does not give modal focus policy to a nonmodal open dialog', () => {
    const shell = markOwner(document.createElement('div'), 'nonmodal-editor')
    const dialog = document.createElement('dialog')
    dialog.open = true
    releases.push(registerMarkdownOverlayForElement(shell, dialog, { modal: false }))
    document.body.append(shell, dialog)
    releases.push(acquireFullscreenLock(shell, () => undefined, null))

    expect(hasActiveModalDialog(shell)).toBe(false)
    expect(dialog.inert).not.toBe(true)
  })

  it('keeps portals from another editor outside the active fullscreen focus scope', async () => {
    const first = markOwner(document.createElement('div'), 'first')
    const firstInput = document.createElement('input')
    first.append(firstInput)
    const second = markOwner(document.createElement('div'), 'second')
    const { dialog, input: secondInput } = dialogWithInput()
    const sharedPortal = document.createElement('div')
    const firstDecoration = document.createElement('div')
    const secondPopup = document.createElement('div')
    releases.push(registerMarkdownOverlayForElement(first, firstDecoration, { consumesEscape: false }))
    releases.push(registerMarkdownOverlayForElement(second, dialog, { modal: true }))
    releases.push(registerMarkdownOverlayForElement(second, secondPopup))
    sharedPortal.append(firstDecoration, dialog, secondPopup)
    document.body.append(first, second, sharedPortal)
    releases.push(acquireFullscreenLock(first, () => firstInput.focus(), null))

    openDialog(dialog)
    await Promise.resolve()
    expect(hasActiveModalDialog(first)).toBe(false)
    expect(hasActiveModalDialog(second)).toBe(true)
    expect(hasActiveEditorOverlay(first)).toBe(false)
    expect(hasActiveEditorOverlay(second)).toBe(true)
    expect(sharedPortal.inert).not.toBe(true)
    expect(firstDecoration.inert).not.toBe(true)
    expect(dialog.inert).toBe(true)
    expect(secondPopup.inert).toBe(true)
    secondInput.focus()
    expect(document.activeElement).toBe(firstInput)
  })

  it('keeps the real plugin picker interactive and restores its toolbar action after cancel', async () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
    const host = document.createElement('div')
    document.body.append(host)
    const plugin = createYouTrackPlugin({ client: { search: async () => [] } })
    const app = createApp({ render: () => h(MarkdownEditor, {
      profile: 'rich', ariaLabel: 'Plugin document', plugins: [plugin],
      modelValue: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } },
    }) })
    apps.push(app)
    app.mount(host)
    await nextTick()
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()
    const action = document.querySelector<HTMLButtonElement>('[data-command="youtrack.search"]')!
    action.click()
    await vi.waitFor(() => expect(document.querySelector('[role="dialog"][aria-label="Vybrat úkol z YouTracku"]')).not.toBeNull())
    const dialog = document.querySelector<HTMLElement>('[role="dialog"][aria-label="Vybrat úkol z YouTracku"]')!
    expect(dialog).not.toBeNull()
    expect(dialog.inert).not.toBe(true)
    expect(document.activeElement).toBe(dialog.querySelector('input'))
    expect(action.disabled).toBe(true)
    dialog.querySelector('input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    await vi.waitFor(() => expect(action.disabled).toBe(false))
    expect(dialog.isConnected).toBe(false)
    expect(document.querySelector('.md-editor.is-fullscreen')).not.toBeNull()
  })

  it.each(['modern', 'legacy'] as const)('preserves %s fullscreen on modal Escape and exits on the next Escape', async surface => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => surface === 'modern'
      ? h(MarkdownEditor, { modelValue: 'Text', ariaLabel: 'Document' })
      : h(PoolEditor, { modelValue: 'Text', fullscreenLabel: 'Document', hideBlockHandle: true }) })
    apps.push(app)
    app.mount(host)
    await nextTick()
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()
    const { dialog, input } = dialogWithInput()
    const editorShell = document.querySelector<HTMLElement>('.md-editor.is-fullscreen')!
    releases.push(registerMarkdownOverlayForElement(editorShell, dialog, { modal: true }))
    document.body.append(dialog)
    openDialog(dialog)
    input.focus()
    const escape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
    input.dispatchEvent(escape)
    await nextTick()
    expect(document.querySelector('.md-editor.is-fullscreen')).not.toBeNull()
    expect(escape.defaultPrevented).toBe(false)
    // The browser sends cancel to the native dialog after its Escape keydown.
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }))
    dialog.remove()
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await nextTick()
    expect(document.querySelector('.md-editor.is-fullscreen')).toBeNull()
    expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(false)
  })

  it.each(['modern', 'legacy'] as const)('does not leave %s fullscreen while Escape cancels IME composition', async surface => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => surface === 'modern'
      ? h(MarkdownEditor, { modelValue: 'Text', ariaLabel: 'Document' })
      : h(PoolEditor, { modelValue: 'Text', fullscreenLabel: 'Document', hideBlockHandle: true }) })
    apps.push(app)
    app.mount(host)
    await nextTick()
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()

    const escape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })
    Object.defineProperty(escape, 'isComposing', { value: true })
    document.querySelector('.ProseMirror')!.dispatchEvent(escape)
    await nextTick()
    expect(document.querySelector('.md-editor.is-fullscreen')).not.toBeNull()
  })

  it.each(['fullscreenable', 'readonly', 'compact'] as const)('returns the legacy editor from fullscreen when %s policy changes', async policy => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
    const fullscreenable = ref(true)
    const readonly = ref(false)
    const compact = ref(false)
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => h(PoolEditor, {
      modelValue: 'Text', fullscreenLabel: 'Document', hideBlockHandle: true,
      fullscreenable: fullscreenable.value, readonly: readonly.value, compact: compact.value,
    }) })
    apps.push(app)
    app.mount(host)
    await nextTick()
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()

    if (policy === 'fullscreenable') fullscreenable.value = false
    else if (policy === 'readonly') readonly.value = true
    else compact.value = true
    await nextTick()
    expect(document.querySelector('.md-editor.is-fullscreen')).toBeNull()
    expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(false)
    if (policy !== 'readonly') expect(document.activeElement).toBe(document.querySelector('.ProseMirror'))
  })

  it('does not offer fullscreen when the legacy editor starts compact', async () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => h(PoolEditor, { modelValue: 'Text', compact: true, hideBlockHandle: true }) })
    apps.push(app)
    app.mount(host)
    await nextTick()

    expect(document.querySelector('[data-testid="editor-fullscreen-toggle"]')).toBeNull()
    expect(document.querySelector('.md-editor.is-fullscreen')).toBeNull()
  })
})
