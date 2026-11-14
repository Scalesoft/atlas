// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, reactive, ref, type App } from 'vue'
import type { Editor } from '@tiptap/core'
import { closeHistory } from '@tiptap/pm/history'
import * as markdown from '../index'
import { editorForController, registerCollaborationAdapter, type CollaborationUpdate } from '../core/internal'
import { openMarkdownContextPopover } from '../core'

// Cold import of the import notice (loaded on its first use), kept out of the test's wait on a loaded host.
await import('../ui/ImportNotice.vue')

const apps: App[] = []
const controllers: markdown.MarkdownEditorController[] = []
beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }) })
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})
function mount(render: () => ReturnType<typeof h>) {
  const host = document.createElement('div')
  document.body.append(host)
  const app = createApp({ render })
  apps.push(app)
  app.mount(host)
  return { host, app }
}

const insertCommand = markdown.defineMarkdownCommand({ id: 'test.toolbar-insert', apply: () => ({ commands }) => commands.insertContent('Plugin content') })
const toolbarPlugin: markdown.MarkdownPlugin = {
  id: 'test.toolbar-presentation', apiVersion: 1,
  create: () => ({ commands: [insertCommand], actions: { toolbar: [
    { id: 'test.first', label: 'První akce', icon: 'svg:LinkIcon', command: insertCommand() },
    { id: 'test.second', label: 'Druhá akce', command: insertCommand() },
  ] } }),
}

describe('public Vue bindings', () => {
  it('updates Markdown fidelity after formatting, component insertion, removal and undo without remounting', async () => {
    let editor!: Editor
    let controller!: markdown.MarkdownEditorController
    const probe: markdown.MarkdownPlugin = {
      id: 'test.fidelity-probe', apiVersion: 1,
      create: () => ({ onAttach: context => { editor = context.editor } }),
    }
    const value: markdown.MarkdownRichValue = { format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Text' }] }],
    } }
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: value, profile: 'rich',
      plugins: [probe], ariaLabel: 'Content fidelity',
      onCreated: (current: markdown.MarkdownEditorController) => { controller = current },
    }))
    await nextTick()
    const surface = host.querySelector('.ProseMirror')
    const label = () => host.querySelector('.paper-footer [role="status"]')?.textContent
    expect(label()).toBe('Markdown')
    editor.commands.selectAll()
    editor.commands.setUnderline()
    await nextTick()
    expect(label()).toBe('Rich dokument')
    editor.commands.unsetUnderline()
    await nextTick()
    expect(label()).toBe('Markdown')
    editor.commands.insertContent({ type: 'paragraph', content: [{ type: 'text', text: 'Note', marks: [{ type: 'underline' }] }] })
    await nextTick()
    expect(label()).toBe('Rich dokument')
    const rich = controller.getValue()
    editor.view.dispatch(closeHistory(editor.state.tr))
    editor.commands.selectAll()
    editor.commands.deleteSelection()
    await nextTick()
    expect(label()).toBe('Markdown')
    editor.commands.undo()
    await nextTick()
    expect(label()).toBe('Rich dokument')
    expect(controller.getValue()).toEqual(rich)
    expect(controller.profile).toBe('rich')
    expect(host.querySelector('.ProseMirror')).toBe(surface)
  })

  it('offers a live read-only Markdown projection only when enabled in document mode', async () => {
    const enabled = ref(false)
    const variant = ref<'document' | 'compact'>('document')
    let controller!: markdown.LocalMarkdownEditorController<'markdown-safe'>
    const created = (value: markdown.MarkdownEditorController) => { controller = value as typeof controller }
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: '# First', ariaLabel: 'Source demo',
      showMarkdownSource: enabled.value, variant: variant.value, readonly: true, onCreated: created }))
    await nextTick()
    expect(host.querySelector('.md-source-trigger')).toBeNull()
    const editor = host.querySelector('.ProseMirror')
    enabled.value = true
    await nextTick()
    host.querySelector<HTMLButtonElement>('.md-source-trigger')!.click()
    await nextTick()
    const source = host.querySelector<HTMLTextAreaElement>('.md-source textarea')!
    expect(source.readOnly).toBe(true)
    expect(source.value).toBe('# First')
    controller.setValue('## Updated')
    await nextTick()
    expect(source.value).toBe('## Updated')
    source.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await nextTick()
    expect(host.querySelector('.md-source textarea')).toBeNull()
    expect(document.activeElement).toBe(host.querySelector('.md-source-trigger'))
    expect(host.querySelector('.ProseMirror')).toBe(editor)
    variant.value = 'compact'
    await nextTick()
    expect(host.querySelector('.md-source')).toBeNull()
    expect(controller.getValue()).toBe('## Updated')
  })

  it('changes formatting configuration without recreating the editor or losing rich content and history', async () => {
    const formatting = ref<readonly markdown.MarkdownFormattingItem[] | undefined>(['headings', 'underline'])
    let controller!: markdown.MarkdownEditorController
    const created = vi.fn((value: markdown.MarkdownEditorController) => { controller = value })
    const value: markdown.MarkdownRichValue = { format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [{ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Keep me', marks: [{ type: 'underline' }] }] }, { type: 'paragraph' }],
    } }
    mount(() => h(markdown.MarkdownEditor, { modelValue: value, profile: 'rich', variant: 'compact',
      ariaLabel: 'Configured compact', formatting: formatting.value, onCreated: created }))
    await nextTick()
    const before = controller.getValue()
    const element = document.querySelector('.ProseMirror')
    expect(element).not.toBeNull()
    expect(controller.dispatch({ type: 'set-paragraph' })).toBe(true)
    const edited = controller.getValue()

    formatting.value = []
    await nextTick()
    expect(document.querySelector('.bubble-menu')).toBeNull()
    formatting.value = undefined
    await nextTick()
    expect(document.querySelector('.ProseMirror')).toBe(element)
    expect(controller.getValue()).toEqual(edited)
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getValue()).toEqual(before)
    expect(created).toHaveBeenCalledTimes(1)
  })

  it('forwards host attributes and native editor events to the rendered shell', async () => {
    const uploadError = vi.fn()
    const { host } = mount(() => h(markdown.MarkdownEditor, {
      modelValue: '', ariaLabel: 'Attributed editor', id: 'host-editor', class: 'host-theme',
      'data-host-attribute': 'ready', onMdImageUploadError: uploadError,
    }))
    await nextTick()
    const shell = host.querySelector<HTMLElement>('#host-editor')!
    expect([...shell.classList]).toEqual(expect.arrayContaining(['md-editor', 'host-theme']))
    expect(shell.getAttribute('data-host-attribute')).toBe('ready')
    host.querySelector('.ProseMirror')!.dispatchEvent(new CustomEvent('md-image-upload-error', { bubbles: true }))
    expect(uploadError).toHaveBeenCalledTimes(1)
  })

  it('keeps slash command availability coherent when the surface variant changes', async () => {
    const variant = ref<'document' | 'compact'>('document')
    let editor!: Editor
    const probe: markdown.MarkdownPlugin = {
      id: 'test.variant-probe', apiVersion: 1,
      create: () => ({ onAttach: context => { editor = context.editor } }),
    }
    mount(() => h(markdown.MarkdownEditor, {
      modelValue: '', ariaLabel: 'Variant editor', variant: variant.value, plugins: [probe],
    }))
    await nextTick()
    const items = () => editor.extensionManager.extensions.find(extension => extension.name === 'slashCommands')!
      .options.suggestion.items({ query: '', editor }).map((item: { id?: string }) => item.id)
    expect(items()).toContain('table')

    variant.value = 'compact'
    await nextTick()
    expect(document.querySelector('.md-editor')?.classList.contains('compact')).toBe(true)
    expect(items()).not.toContain('table')
    expect(items()).not.toContain('code-block')
    expect(items()).not.toContain('horizontal-rule')
  })

  it('updates the placeholder through live view configuration', async () => {
    const placeholder = ref<string | undefined>('First placeholder')
    const { host } = mount(() => h(markdown.MarkdownEditor, {
      modelValue: '', ariaLabel: 'Placeholder editor', placeholder: placeholder.value,
    }))
    await nextTick()
    const emptyParagraph = () => host.querySelector<HTMLElement>('.ProseMirror p.is-empty')
    expect(emptyParagraph()?.getAttribute('data-placeholder')).toBe('First placeholder')

    placeholder.value = 'Second placeholder'
    await nextTick()
    expect(emptyParagraph()?.getAttribute('data-placeholder')).toBe('Second placeholder')

    placeholder.value = undefined
    await nextTick()
    expect(emptyParagraph()?.getAttribute('data-placeholder')).toBe('')
  })

  it('leaves fullscreen and restores editor focus when live surface policy disables it', async () => {
    const variant = ref<'document' | 'compact'>('document')
    const fullscreenable = ref(true)
    const readonly = ref(false)
    mount(() => h(markdown.MarkdownEditor, {
      modelValue: 'Text', ariaLabel: 'Policy editor', variant: variant.value, fullscreenable: fullscreenable.value,
      readonly: readonly.value,
    }))
    await nextTick()

    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()
    expect(document.querySelector('.md-editor')?.getAttribute('data-fullscreen')).toBe('true')
    variant.value = 'compact'
    await nextTick()
    expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(false)
    expect(document.querySelector('.md-editor')?.hasAttribute('data-fullscreen')).toBe(false)
    expect(document.activeElement).toBe(document.querySelector('.ProseMirror'))

    variant.value = 'document'
    await nextTick()
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()
    fullscreenable.value = false
    await nextTick()
    expect(document.body.hasAttribute('data-editor-fullscreen')).toBe(false)
    expect(document.querySelector('.md-editor')?.hasAttribute('data-fullscreen')).toBe(false)
    expect(document.activeElement).toBe(document.querySelector('.ProseMirror'))

    // Reading keeps it: a document turned read-only (a locked page) stays in fullscreen and still offers it, the footer in its place.
    fullscreenable.value = true
    await nextTick()
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()
    const footer = document.querySelector('.paper-footer')
    readonly.value = true
    await nextTick()
    expect(document.querySelector('.md-editor')?.getAttribute('data-fullscreen')).toBe('true')
    expect(document.querySelector('.paper-footer')).toBe(footer)
    expect(document.querySelector('.paper-footer .md-keyboard-hint')).toBeNull()
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()
    expect(document.querySelector('.md-editor')?.hasAttribute('data-fullscreen')).toBe(false)
    document.querySelector<HTMLButtonElement>('[data-testid="editor-fullscreen-toggle"]')!.click()
    await nextTick()
    expect(document.querySelector('.md-editor')?.getAttribute('data-fullscreen')).toBe('true')
  })

  it('uses the supplied controller for custom commands and does not destroy it on root unmount', async () => {
    expect(markdown.MarkdownEditorRoot).toBeDefined()
    const controller = markdown.createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: 'Text' } })
    controllers.push(controller)
    const { host, app } = mount(() => h(markdown.MarkdownEditorRoot, { controller }, {
      default: ({ dispatch, query }: any) => [
        h('button', { 'aria-pressed': query({ type: 'toggle-bold' }).active, onClick: () => dispatch({ type: 'toggle-bold' }) }, 'Bold'),
        h(markdown.MarkdownEditorContent, { ariaLabel: 'Custom input' }),
      ],
    }))
    await nextTick()
    host.querySelector('button')!.click()
    await nextTick()
    expect(host.querySelector('button')?.getAttribute('aria-pressed')).toBe('true')
    expect(host.querySelector('[role="textbox"]')?.getAttribute('aria-label')).toBe('Custom input')
    app.unmount()
    apps.splice(apps.indexOf(app), 1)
    expect(controller.state.lifecycle).toBe('idle')
    const second = document.createElement('div')
    controller.attachView(second, { ariaLabel: 'Remounted' })
    expect(second.textContent).toBe('Text')
  })

  it('publishes local changes synchronously and never echoes a host replacement', async () => {
    expect(markdown.MarkdownEditor).toBeDefined()
    const value = ref('Original')
    const changed = vi.fn((next: markdown.MarkdownValue) => {
      if (typeof next === 'string') value.value = next
    })
    let controller!: markdown.LocalMarkdownEditorController
    const { host } = mount(() => h(markdown.MarkdownEditor, {
      modelValue: value.value, ariaLabel: 'Description', variant: 'compact', visibleLines: 2,
      'onUpdate:modelValue': changed, onCreated: (next: markdown.MarkdownEditorController) => {
        controller = next as markdown.LocalMarkdownEditorController
      },
    }))
    await nextTick()
    controller.dispatch({ type: 'set-heading', level: 2 })
    expect(value.value).toBe('## Original')
    await nextTick()
    expect(host.querySelector('h2')?.textContent).toBe('Original')
    const calls = changed.mock.calls.length
    value.value = '*Replacement*'
    await nextTick()
    expect(host.querySelector('em')?.textContent).toBe('Replacement')
    expect(changed).toHaveBeenCalledTimes(calls)
  })

  it('retains rich v-model and renders readonly without editable controls', async () => {
    expect(markdown.MarkdownRenderer).toBeDefined()
    const value = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
      { type: 'paragraph', content: [{ type: 'text', text: 'Rich', marks: [{ type: 'underline' }] }] },
    ] } } as const
    const { host } = mount(() => h(markdown.MarkdownRenderer, { value, profile: 'rich', ariaLabel: 'Preview' }))
    await nextTick()
    expect(host.querySelector('u')?.textContent).toBe('Rich')
    expect(host.querySelector('[contenteditable="true"]')).toBeNull()
    expect(host.querySelector('button')).toBeNull()
  })

  it('accepts rich ref values, nested reactive nodes and replacement without echoing', async () => {
    const value = ref<markdown.MarkdownRichValue>({ format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [reactive({ type: 'paragraph', content: [{ type: 'text', text: 'Original rich' }] })],
    } })
    const changed = vi.fn((next: markdown.MarkdownValue) => {
      if (typeof next !== 'string') value.value = next
    })
    let controller!: markdown.MarkdownEditorController
    const { host } = mount(() => h(markdown.MarkdownEditor, {
      modelValue: value.value, profile: 'rich', ariaLabel: 'Reactive rich', variant: 'compact',
      'onUpdate:modelValue': changed, onCreated: (next: markdown.MarkdownEditorController) => { controller = next },
    }))
    await nextTick()
    expect(host.querySelector('p')?.textContent).toBe('Original rich')
    controller.dispatch({ type: 'set-heading', level: 2 })
    await nextTick()
    expect(host.querySelector('h2')?.textContent).toBe('Original rich')
    expect(changed).toHaveBeenCalledTimes(1)
    value.value = { format: 'atlas-rich', version: 1, document: reactive({ type: 'doc', content: [
      { type: 'paragraph', content: [{ type: 'text', text: 'Replacement rich', marks: [{ type: 'underline' }] }] },
    ] }) }
    await nextTick()
    expect(host.querySelector('u')?.textContent).toBe('Replacement rich')
    expect(changed).toHaveBeenCalledTimes(1)
  })

  it('renders and updates a rich reactive preview', async () => {
    const value = ref<markdown.MarkdownRichValue>({ format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Preview' }] }],
    } })
    const { host } = mount(() => h(markdown.MarkdownRenderer, { value: value.value, profile: 'rich', ariaLabel: 'Reactive preview' }))
    await nextTick()
    expect(host.querySelector('p')?.textContent).toBe('Preview')
    value.value = { format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [reactive({ type: 'paragraph', content: [{ type: 'text', text: 'New preview' }] })],
    } }
    await nextTick()
    expect(host.querySelector('p')?.textContent).toBe('New preview')
    expect(host.querySelector('[contenteditable="true"]')).toBeNull()
  })

  it('collapses plugin actions into one Vložit menu and closes it on Escape', async () => {
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: '', ariaLabel: 'Insert menu editor',
      toolbarPresentation: 'insert-menu', plugins: [toolbarPlugin] }))
    await nextTick()
    expect(host.querySelectorAll('.md-plugin-toolbar .md-plugin-action')).toHaveLength(0)
    const trigger = host.querySelector<HTMLButtonElement>('button[aria-haspopup="menu"][aria-label="Vložit"]')!
    expect(trigger).not.toBeNull()
    expect(trigger.textContent).toContain('Vložit')
    expect(trigger.getAttribute('aria-expanded')).toBe('false')

    trigger.click()
    await nextTick()
    expect(trigger.getAttribute('aria-expanded')).toBe('true')
    const menu = host.querySelector('[role="menu"].md-context-popover')!
    const items = menu.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')
    expect([...items].map(item => item.textContent?.trim())).toEqual(['První akce', 'Druhá akce'])
    // `icon: 'svg:LinkIcon'` resolves through the registry (legacy component names included);
    // an unresolved name must never leak as literal "svg:…" text next to the label.
    expect(items[0]!.querySelector('svg[data-icon="link"]')).not.toBeNull()
    expect(menu.textContent).not.toContain('svg:')
    // The trigger is a member of the shared text-button family, not an unstyled `.bb` orphan.
    expect(trigger.classList.contains('md-btn')).toBe(true)

    // Focus lives on the first item once the menu opens; Escape is handled on the popup itself.
    items[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await nextTick()
    expect(host.querySelector('[role="menu"]')).toBeNull()
    expect(trigger.getAttribute('aria-expanded')).toBe('false')
    expect(document.activeElement).toBe(trigger)

    trigger.click()
    await nextTick()
    expect(host.querySelectorAll('[role="menuitem"]')).toHaveLength(2)
    host.querySelector<HTMLButtonElement>('[role="menuitem"]')!.click()
    await nextTick()
    expect(host.querySelector('[role="menu"]')).toBeNull()
    expect(host.querySelector('.ProseMirror')?.textContent).toBe('Plugin content')
  })

  it('passes pasted front matter to an @markdown-import listener and applies its heading decision', async () => {
    vi.stubGlobal('ClipboardEvent', class extends Event { readonly clipboardData = null })
    const onMarkdownImport = vi.fn(({ title, leadingHeading }: { title?: string; leadingHeading?: string }) => ({ omitLeadingHeading: title === leadingHeading }))
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: '', ariaLabel: 'Import hook', onMarkdownImport }))
    await nextTick()
    const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true })
    const text = '---\ntitle: Release Process\n---\n# Release Process\n\nTag the release.'
    Object.defineProperty(event, 'clipboardData', { value: { getData: (type: string) => type === 'text/plain' ? text : '', types: ['text/plain'], files: [], items: [] } })
    host.querySelector('.ProseMirror')!.dispatchEvent(event)
    expect(onMarkdownImport).toHaveBeenCalledWith(expect.objectContaining({ source: 'paste', title: 'Release Process' }))
    expect(host.querySelector('.ProseMirror h1')).toBeNull()
    expect(host.querySelector('.ProseMirror')?.textContent).toBe('Tag the release.')
  })

  it('resolves wiki links through the resolveWikiLink prop and shows a status notice for constructs kept as text', async () => {
    vi.stubGlobal('ClipboardEvent', class extends Event { readonly clipboardData = null })
    const resolveWikiLink = vi.fn(({ target }: { target: string }) => target === 'Home' ? { href: '/wiki/home' } : null)
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: '', ariaLabel: 'Wiki import', resolveWikiLink }))
    await nextTick()
    const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true })
    const text = 'See [[Home]], [[Missing]] and <kbd>Ctrl</kbd>+<kbd>C</kbd>.'
    Object.defineProperty(event, 'clipboardData', { value: { getData: (type: string) => type === 'text/plain' ? text : '', types: ['text/plain'], files: [], items: [] } })
    host.querySelector('.ProseMirror')!.dispatchEvent(event)
    await nextTick()
    expect(host.querySelector('.ProseMirror a[href="/wiki/home"]')?.textContent).toBe('Home')
    // The notice component loads on its first use.
    await vi.waitFor(() => expect(host.querySelector('[data-testid="markdown-import-notice"]')).not.toBeNull())
    const notice = host.querySelector<HTMLElement>('[data-testid="markdown-import-notice"]')!
    expect(notice.getAttribute('role')).toBe('status')
    expect(notice.querySelector('summary')?.textContent).toBe('3 části byly vloženy jako text')
    expect([...notice.querySelectorAll('li')].map(item => item.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
      'Wiki odkaz: [[Missing]]', 'HTML (2×): <kbd>',
    ])
    notice.querySelector<HTMLButtonElement>('button[aria-label="Zavřít"]')!.click()
    await nextTick()
    expect(host.querySelector('[data-testid="markdown-import-notice"]')).toBeNull()
  })

  it('renders no plugin toolbar at all when the host asks for the hidden presentation', async () => {
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: '', ariaLabel: 'Hidden toolbar',
      toolbarPresentation: 'hidden', plugins: [toolbarPlugin] }))
    await nextTick()
    expect(host.querySelector('.md-plugin-toolbar')).toBeNull()
    expect(host.querySelector('button[aria-haspopup="menu"][aria-label="Vložit"]')).toBeNull()
  })

  it('falls back to the expanded toolbar for an unsupported presentation value', async () => {
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: '', ariaLabel: 'Fallback toolbar',
      toolbarPresentation: 'compact-menu' as unknown as 'expanded', plugins: [toolbarPlugin] }))
    await nextTick()
    expect(host.querySelectorAll('.md-plugin-toolbar .md-plugin-action')).toHaveLength(2)
    expect(host.querySelector('button[aria-haspopup="menu"][aria-label="Vložit"]')).toBeNull()
  })

  it('hides only the fidelity label when the host suppresses the profile label', async () => {
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: 'Text', ariaLabel: 'No profile label',
      showProfileLabel: false }))
    await nextTick()
    expect(host.querySelector('.paper-footer')).not.toBeNull()
    expect(host.querySelector('.paper-footer [role="status"]')).toBeNull()
    expect(host.querySelector('.paper-footer .md-keyboard-hint')).not.toBeNull()
    const fullscreen = host.querySelector('[data-testid="editor-fullscreen-toggle"]')!
    expect(fullscreen).not.toBeNull()
    expect(fullscreen.getAttribute('aria-label')).toBe('Celá obrazovka')
  })

  it('renders presence only for online remote peers', async () => {
    let publish!: (update: CollaborationUpdate) => void
    const adapter = registerCollaborationAdapter(publisher => {
      publish = publisher
      return { extensions: [], isRemoteTransaction: () => false,
        attachEditor: () => publisher({ hydrated: true, writable: true, connection: 'online' }),
        detachEditor() {}, destroy() {} }
    })
    const { host } = mount(() => h(markdown.MarkdownEditor, { collaboration: adapter, profile: 'rich',
      ariaLabel: 'Presence editor', showPresence: true }))
    await nextTick()
    expect(host.querySelector('[data-testid="presence-stack"]')).toBeNull()

    publish({ connection: 'online', peers: [{ clientId: 7, name: 'Jana Nováková', color: '#a11' }] })
    await nextTick()
    const stack = host.querySelector('[data-testid="presence-stack"]')!
    expect(stack).not.toBeNull()
    expect(stack.getAttribute('aria-label')).toContain('Jana Nováková')

    publish({ connection: 'reconnecting' })
    await nextTick()
    expect(host.querySelector('[data-testid="presence-stack"]')).toBeNull()

    publish({ connection: 'online' })
    await nextTick()
    expect(host.querySelector('[data-testid="presence-stack"]')).not.toBeNull()
  })

  it('omits presence unless the host asks for it', async () => {
    const adapter = registerCollaborationAdapter(publish => ({ extensions: [], isRemoteTransaction: () => false,
      attachEditor: () => publish({ hydrated: true, writable: true, connection: 'online',
        peers: [{ clientId: 3, name: 'Petr Malý', color: '#1a1' }] }),
      detachEditor() {}, destroy() {} }))
    const { host } = mount(() => h(markdown.MarkdownEditor, { collaboration: adapter, profile: 'rich',
      ariaLabel: 'No presence editor' }))
    await nextTick()
    expect(host.querySelector('[data-testid="presence-stack"]')).toBeNull()
  })

  // UX BD-10: on a board the editor sits in a zoomed, clipping canvas; the selection bubble scaled and was cut off there.
  it('puts the selection bubble on <body> in an editor-classed host when the host asks (positioned fixed)', async () => {
    // jsdom has no layout; floating-ui and the caret ask for rectangles.
    Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
    Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }
    let controller!: markdown.MarkdownEditorController
    const value: markdown.MarkdownRichValue = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Poznámka' }] }] } }
    const { host } = mount(() => h(markdown.MarkdownEditor, { modelValue: value, profile: 'rich', ariaLabel: 'Poznámka', bubbleOnBody: true, onCreated: (created: markdown.MarkdownEditorController) => { controller = created } }))
    await nextTick()
    const editor = editorForController(controller)!
    editor.view.focus()
    editor.commands.setTextSelection({ from: 1, to: 5 })
    await vi.waitFor(() => expect(document.querySelector('[role=group].bubble-menu')?.parentElement?.parentElement).toBe(document.body))
    const bubble = document.querySelector<HTMLElement>('[role=group].bubble-menu')!
    expect(host.contains(bubble)).toBe(false)
    expect(bubble.parentElement!.matches('.md-editor')).toBe(true)
    await vi.waitFor(() => expect(bubble.style.position).toBe('fixed'))
  })

  // A node view's own field sits under the bubble's parent, so the menu's blur check kept the bubble over it (and over its popover).
  it('takes the selection bubble away when the focus goes to a field of a block or to a popover', async () => {
    Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
    Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }
    let controller!: markdown.MarkdownEditorController
    const value: markdown.MarkdownRichValue = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Poznámka' }] }] } }
    mount(() => h(markdown.MarkdownEditor, { modelValue: value, profile: 'rich', ariaLabel: 'Poznámka', onCreated: (created: markdown.MarkdownEditorController) => { controller = created } }))
    await nextTick()
    const editor = editorForController(controller)!
    const bubble = () => document.querySelector('[role=group].bubble-menu')
    const select = async () => { editor.view.dom.tabIndex = 0; editor.view.focus(); editor.commands.setTextSelection({ from: 1, to: 5 }); await vi.waitFor(() => expect(bubble()).not.toBeNull()) }
    await select()
    const field = document.createElement('input')
    editor.view.dom.parentElement!.append(field)
    field.focus()
    await vi.waitFor(() => expect(bubble()).toBeNull())
    await select()
    openMarkdownContextPopover({ editor, anchor: field, title: 'Od koho', render: body => { body.append(document.createElement('input')) } })
    await vi.waitFor(() => expect(bubble()).toBeNull())
  })
})
