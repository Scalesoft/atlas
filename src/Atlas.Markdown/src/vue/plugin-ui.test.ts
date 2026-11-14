// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, inject, nextTick, provide, ref, type App } from 'vue'
import { Node } from '@tiptap/core'
import { NodeViewWrapper, nodeViewProps, VueNodeViewRenderer } from '@tiptap/vue-3'
import { MarkdownEditor } from './index'
import { defineMarkdownCommand, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue, type MarkdownValue } from '../core'

const apps: App[] = []
beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }) })
afterEach(() => { apps.splice(0).forEach(app => app.unmount()); document.body.replaceChildren(); vi.unstubAllGlobals() })
const empty = (): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } })
const insert = defineMarkdownCommand({ id: 'test.insert', apply: () => ({ commands }) => commands.insertContent('Plugin content') })
const action = { id: 'test.insert', label: 'Insert from plugin', command: insert(), variants: ['document'] as const }
const plugin: MarkdownPlugin = { id: 'test.ui', apiVersion: 1, create: () => ({ commands: [insert], actions: { toolbar: [action], slash: [action] } }) }
function mount(component: ReturnType<typeof defineComponent>) {
  const host = document.createElement('div')
  document.body.append(host)
  const app = createApp(component)
  apps.push(app)
  app.mount(host)
  return host
}

it('connects toolbar actions, slots, reactive rich values and readonly on the same controller', async () => {
  const value = ref(empty())
  const readonly = ref(false)
  let controller!: MarkdownEditorController
  const changed = vi.fn((next: MarkdownValue) => {
    if (typeof next !== 'string') value.value = next
  })
  const host = mount(defineComponent({ setup: () => () => h(MarkdownEditor, {
    modelValue: value.value, profile: 'rich', plugins: [plugin], readonly: readonly.value, ariaLabel: 'Plugin editor',
    onCreated: (current: MarkdownEditorController) => { controller = current }, 'onUpdate:modelValue': changed,
  }, { inspector: ({ state }: { state: { editable: boolean } }) => h('aside', state.editable ? 'Editing' : 'Preview') }) }))
  await nextTick()
  host.querySelector<HTMLButtonElement>('[data-command="test.insert"]')!.click()
  await nextTick()
  expect(host.querySelector('.ProseMirror')?.textContent).toBe('Plugin content')
  expect(changed).toHaveBeenCalledTimes(1)
  expect(host.querySelector('aside')?.textContent).toBe('Editing')
  readonly.value = true
  await nextTick()
  expect(host.querySelector('[data-command]')).toBeNull()
  expect(host.querySelector('.ProseMirror')?.getAttribute('contenteditable')).toBe('false')
  expect(host.querySelector('aside')?.textContent).toBe('Preview')
  expect(controller.dispatch(insert())).toBe(false)
  readonly.value = false
  await nextTick()
  expect(host.querySelector('[data-command]')).not.toBeNull()
  expect(controller.state.lifecycle).toBe('ready')
})

it('respects plugin surface variants and a host toolbar slot', async () => {
  const host = mount(defineComponent({ setup: () => () => h(MarkdownEditor, {
    modelValue: empty(), profile: 'rich', plugins: [plugin], variant: 'compact', ariaLabel: 'Compact plugins',
  }) }))
  await nextTick()
  expect(host.querySelector('[data-command]')).toBeNull()
  const overridden = mount(defineComponent({ setup: () => () => h(MarkdownEditor, {
    modelValue: empty(), profile: 'rich', plugins: [plugin], ariaLabel: 'Custom toolbar',
  }, { toolbar: () => h('nav', 'Host toolbar') }) }))
  await nextTick()
  expect(overridden.querySelector('nav')?.textContent).toBe('Host toolbar')
  expect(overridden.querySelector('[data-command]')).toBeNull()
})

it('renders ordered plugin actions with icons and overflow, and owns panel cleanup', async () => {
  const cleanup = vi.fn()
  let panelSignal!: AbortSignal
  let surfaceController!: MarkdownEditorController
  const actions = Array.from({ length: 7 }, (_, index) => ({
    id: `test.action-${index}`,
    label: `Action ${index}`,
    icon: index === 0 ? '★' : undefined,
    group: index < 3 ? 'primary' : index < 6 ? 'secondary' : undefined,
    priority: index,
    command: insert(),
  }))
  const uiPlugin: MarkdownPlugin = {
    id: 'test.surfaces', apiVersion: 1,
    create: () => ({
      commands: [insert],
      actions: { toolbar: actions },
      panels: [
        { id: 'test.inspector', label: 'Test inspector', placement: 'inspector', priority: 2,
          mount: ({ container, signal }) => {
            panelSignal = signal
            container.append(container.ownerDocument.createTextNode('Inspector content'))
            return cleanup
          },
        },
        { id: 'test.status', label: 'Test status', placement: 'status', priority: 1,
          mount: ({ container }) => { container.textContent = 'Status content' },
        },
      ],
    }),
  }
  const host = mount(defineComponent({ setup: () => () => h(MarkdownEditor, {
    modelValue: empty(), profile: 'rich', plugins: [uiPlugin], ariaLabel: 'Plugin surfaces',
    onCreated: (controller: MarkdownEditorController) => { surfaceController = controller },
  }) }))
  await nextTick()

  expect([...host.querySelectorAll(':scope .md-plugin-toolbar > .md-plugin-action')]).toHaveLength(3)
  expect([...host.querySelectorAll('.md-plugin-overflow-menu .md-plugin-action')]).toHaveLength(4)
  expect(host.querySelector('[data-command="test.action-0"] .md-plugin-action-icon')?.textContent).toBe('★')
  expect(host.querySelector('[data-command="test.action-0"]')?.getAttribute('data-group')).toBe('primary')
  expect(host.querySelector(':scope .md-plugin-toolbar > [data-group="secondary"]')).toBeNull()
  expect([...host.querySelectorAll('.md-plugin-overflow-menu [data-group="secondary"]')]).toHaveLength(3)
  expect(host.querySelector('[data-plugin-panel="test.inspector"]')?.textContent).toBe('Inspector content')
  expect(host.querySelector('[data-plugin-panel="test.status"]')?.textContent).toBe('Status content')

  surfaceController.destroy()
  expect(panelSignal.aborted).toBe(true)
  expect(cleanup).toHaveBeenCalledTimes(1)
  const app = apps.pop()!
  app.unmount()
  expect(cleanup).toHaveBeenCalledTimes(1)
})

it('aborts a panel immediately when its runtime cleanup contract is invalid', async () => {
  const report = vi.spyOn(console, 'error').mockImplementation(() => undefined)
  let signal!: AbortSignal
  const invalidPanel: MarkdownPlugin = {
    id: 'test.invalid-panel', apiVersion: 1,
    create: () => ({ panels: [{
      id: 'test.invalid', label: 'Invalid panel', placement: 'inspector',
      mount: ((context: { signal: AbortSignal }) => {
        signal = context.signal
        return Promise.resolve(() => undefined)
      }) as never,
    }] }),
  }
  const host = mount(defineComponent({ setup: () => () => h(MarkdownEditor, {
    modelValue: empty(), profile: 'rich', plugins: [invalidPanel], ariaLabel: 'Invalid panel contract',
  }) }))
  await nextTick()

  expect(signal.aborted).toBe(true)
  expect(host.querySelector('[data-plugin-panel="test.invalid"] [role="status"]')?.textContent).toContain('nepodařilo')
  expect(report).toHaveBeenCalledWith(expect.stringContaining('test.invalid failed to mount'), expect.any(TypeError))
})

it('provides the host Vue context to plugin node views and preserves rich snapshots', async () => {
  const view = defineComponent({ props: nodeViewProps, setup() {
    const label = inject('plugin-label')
    return () => h(NodeViewWrapper, { 'data-testid': 'plugin-node' }, String(label))
  } })
  const widget = Node.create({ name: 'testWidget', group: 'block', atom: true,
    parseHTML: () => [{ tag: 'test-widget' }], renderHTML: () => ['test-widget'],
    addNodeView: () => VueNodeViewRenderer(view), renderText: () => 'Widget',
    addStorage: () => ({ markdown: { serialize(state: { write(value: string): void; closeBlock(node: unknown): void }, node: unknown) { state.write('Widget'); state.closeBlock(node) } } }),
  })
  const widgetPlugin: MarkdownPlugin = { id: 'test.widget', apiVersion: 1, schemaVersion: 1, create: () => ({ extensions: [widget] }) }
  const value: MarkdownRichValue = { ...empty(), plugins: [{ id: 'test.widget', version: 1 }], document: { type: 'doc', content: [{ type: 'testWidget' }] } }
  let controller!: MarkdownEditorController
  const host = mount(defineComponent({ setup() {
    provide('plugin-label', 'Provided by host')
    return () => h(MarkdownEditor, { modelValue: value, profile: 'rich', plugins: [widgetPlugin], ariaLabel: 'Plugin node view',
      onCreated: (current: MarkdownEditorController) => { controller = current },
    })
  } }))
  await nextTick()
  expect(host.querySelector('[data-testid="plugin-node"]')?.textContent).toBe('Provided by host')
  expect(controller.getValue()).toEqual(value)
  expect(controller.getProjection().markdownLossy).toBe(true)
})
