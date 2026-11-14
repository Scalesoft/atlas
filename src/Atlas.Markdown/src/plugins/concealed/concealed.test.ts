// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DOMSerializer } from '@tiptap/pm/model'
import { createApp, h, nextTick } from 'vue'
import { createConcealedContentPlugin, setConcealedContentSettings, setConcealedRevealMode, toggleConcealedContent } from './index'
import { createMarkdownEditorController, migrateMarkdownValue, type MarkdownRichValue } from '../../core'
import MarkdownRenderer from '../../vue/MarkdownRenderer.vue'

const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
const value = (reveal: string | undefined = 'scratch'): MarkdownRichValue => ({
  format: 'atlas-rich', version: 1, plugins: [{ id: 'concealed', version: 1 }],
  document: { type: 'doc', content: [
    paragraph('Public intro.'),
    { type: 'concealedContent', ...reveal === undefined ? {} : { attrs: { reveal } }, content: [paragraph('SURPRISE party on Friday.'), paragraph('Second line.')] },
  ] },
} as MarkdownRichValue)
const plain: MarkdownRichValue = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [paragraph('Wrap me.'), paragraph('Keep me.')] } } as MarkdownRichValue
const disposers: (() => void)[] = []
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null) })
afterEach(() => { disposers.splice(0).reverse().forEach(stop => stop()); document.body.replaceChildren(); vi.useRealTimers(); vi.restoreAllMocks() })
// The invisible ink and the settings panel load lazily with the first reference. Warm both at file load so their cold
// import is not charged to a test timeout or waitFor on a loaded host; `flush` then only lets the settled import land.
await import('../../concealment'); await import('./settings')
// The invisible ink loads lazily with the first reference; let that import settle too.
const flush = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); await import('../../concealment'); for (let i = 0; i < 8; i++) await Promise.resolve(); await nextTick() }
function host() { const element = document.createElement('div'); document.body.append(element); return element }
function controller(source: MarkdownRichValue, extra: { editable?: boolean } = {}) {
  const instance = createMarkdownEditorController({ profile: 'rich', plugins: [createConcealedContentPlugin()], source: { kind: 'local', value: source }, ...extra })
  disposers.push(() => instance.destroy())
  const root = host(); instance.attachView(root, { ariaLabel: 'Edit' })
  return { instance, root }
}
async function reader(documentValue = value()) {
  const element = host()
  const app = createApp({ render: () => h(MarkdownRenderer, { value: documentValue, profile: 'rich', ariaLabel: 'Reader', plugins: [createConcealedContentPlugin()] }) })
  app.mount(element); disposers.push(() => app.unmount()); await flush()
  return element
}
const json = (instance: ReturnType<typeof controller>['instance']) => JSON.parse(JSON.stringify(instance.getProjection().rich))

describe('concealed content block', () => {
  it('wraps the selected blocks and unwraps them again, each as its own undo step', () => {
    const { instance } = controller(plain)
    expect(instance.dispatch(toggleConcealedContent(), { replaceRange: { from: 2, to: 2 } })).toBe(true)
    expect(json(instance).document.content[0]).toMatchObject({ type: 'concealedContent', attrs: { reveal: 'scratch', reconcealAfterMs: 1500, completeAtPercent: 70 }, content: [{ type: 'paragraph' }] })
    expect(json(instance).document.content[1].content[0].text).toBe('Keep me.')
    expect(instance.dispatch(setConcealedRevealMode({ reveal: 'button' }))).toBe(true)
    expect(json(instance).document.content[0].attrs.reveal).toBe('button')
    expect(instance.dispatch(setConcealedRevealMode({ reveal: 'blur' } as never))).toBe(false)
    expect(instance.dispatch(toggleConcealedContent())).toBe(true)
    expect(json(instance).document.content[0].type).toBe('paragraph')
    instance.dispatch({ type: 'undo' })
    expect(json(instance).document.content[0].attrs.reveal).toBe('button')
    instance.dispatch({ type: 'undo' }); instance.dispatch({ type: 'undo' })
    expect(json(instance).document.content.map((node: { type: string }) => node.type)).toEqual(['paragraph', 'paragraph'])
  })

  it('validates the reveal mode and round-trips through HTML without extra data', () => {
    expect(() => controller(value('blur'))).toThrow()
    let html = ''
    const instance = createMarkdownEditorController({ profile: 'rich', plugins: [createConcealedContentPlugin(), {
      id: 'html-probe', apiVersion: 1, create: () => ({ onCreate({ editor }) {
        const exported = document.createElement('div')
        exported.append(DOMSerializer.fromSchema(editor.schema).serializeFragment(editor.state.doc.content))
        html = exported.innerHTML
      } }),
    }], source: { kind: 'local', value: value('button') } })
    disposers.push(() => instance.destroy()); instance.attachView(host(), { ariaLabel: 'Edit' })
    expect(html).toContain('data-md-concealed=""'); expect(html).toContain('data-reveal="button"')
    expect(json(instance as never).document.content[1].attrs.reveal).toBe('button')
    // Markdown has no concealment: the text is kept and the wrapper is reported as lost.
    expect(instance.getProjection()).toMatchObject({ markdownLossy: true })
    expect(instance.getProjection().markdown).toContain('SURPRISE party on Friday.')
  })

  it('defaults to scratching when no mode is stored', () => {
    const { instance } = controller(value(undefined))
    expect(json(instance).document.content[1].attrs).toEqual({ reveal: 'scratch', reconcealAfterMs: 1500, completeAtPercent: 70 })
  })

  it('migrates v1 blocks to v2 unchanged and validates the reader settings', () => {
    const migrated = migrateMarkdownValue(value(), 'rich', [createConcealedContentPlugin()])
    expect(migrated.plugins).toEqual([{ id: 'concealed', version: 2 }])
    expect(migrated.document).toEqual(value().document)
    const settled = (attrs: Record<string, unknown>) => ({ ...value(), document: { type: 'doc', content: [{ type: 'concealedContent', attrs, content: [paragraph('x')] }] } }) as MarkdownRichValue
    for (const attrs of [{ reconcealAfterMs: -1 }, { reconcealAfterMs: 1.5 }, { reconcealAfterMs: 600001 }, { completeAtPercent: 5 }, { completeAtPercent: 101 }, { completeAtPercent: '70' }])
      expect(() => controller(settled(attrs)), JSON.stringify(attrs)).toThrow()
    const { instance } = controller(settled({ reveal: 'scratch', reconcealAfterMs: null, completeAtPercent: null }))
    expect(json(instance).document.content[0].attrs).toEqual({ reveal: 'scratch', reconcealAfterMs: null, completeAtPercent: null })
  })

  it('changes the reader settings of the surrounding block as one undo step', () => {
    const instance = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: value() }, plugins: [createConcealedContentPlugin(), {
      id: 'selection-probe', apiVersion: 1, create: () => ({ onCreate({ editor }) { editor.commands.setTextSelection(18) } }),
    }] })
    disposers.push(() => instance.destroy()); instance.attachView(host(), { ariaLabel: 'Edit' })
    const attrs = () => json(instance).document.content[1].attrs
    expect(instance.dispatch(setConcealedContentSettings({ reconcealAfterMs: null, completeAtPercent: 50 }))).toBe(true)
    expect(attrs()).toEqual({ reveal: 'scratch', reconcealAfterMs: null, completeAtPercent: 50 })
    for (const bad of [{ reconcealAfterMs: -5 }, { completeAtPercent: 3 }, { secret: true }])
      expect(instance.dispatch(setConcealedContentSettings(bad as never))).toBe(false)
    instance.dispatch({ type: 'undo' })
    expect(attrs()).toEqual({ reveal: 'scratch', reconcealAfterMs: 1500, completeAtPercent: 70 })
  })

  it('edits the settings in an anchored panel as one undo step and never while readonly', async () => {
    const { instance, root } = controller(value())
    const attrs = () => json(instance).document.content[1].attrs
    const settings = () => document.querySelector<HTMLElement>('.md-concealed-settings')
    const open = async () => {
      // The panel preloads for authors while the block's toolbar appears; wait for it like a real editing session.
      await import('./settings'); await flush()
      // The edit where it is shown (P1): a click on the label opens the settings at once, a second one closes them.
      const chip = root.querySelector<HTMLElement>('.md-concealed-block__chip')!
      chip.click()
      expect(settings()).not.toBeNull()
      chip.click()
      expect(settings()).toBeNull()
      // The toolbar's „Upravit“ leads to the same panel.
      const button = () => document.querySelector<HTMLButtonElement>('button[aria-label="Upravit"]')
      await vi.waitFor(() => expect(button()).not.toBeNull())
      button()!.click()
      expect(settings()).not.toBeNull()
      return settings()!
    }
    const panel = await open()
    const tile = (selector: string) => panel.querySelector<HTMLButtonElement>(selector)!
    expect(tile('[data-reveal="scratch"]').getAttribute('aria-checked')).toBe('true')
    expect(tile('[data-reconceal="1500"]').getAttribute('aria-checked')).toBe('true')
    expect(tile('[data-complete="70"]').getAttribute('aria-checked')).toBe('true')
    expect(panel.textContent).toContain('ne zabezpečení')
    tile('[data-reconceal="never"]').click(); tile('[data-complete="90"]').click()
    expect(attrs().reconcealAfterMs).toBe(1500)
    tile('[data-reveal="button"]').click()
    // Completion applies to scratching only.
    expect(tile('[data-complete="50"]').disabled).toBe(true)
    panel.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(attrs()).toEqual({ reveal: 'button', reconcealAfterMs: null, completeAtPercent: 90 })
    expect(root.querySelector('.md-concealed-block__hint')!.textContent).toBe('Tlačítkem Odkrýt · nezakrývá')
    instance.dispatch({ type: 'undo' })
    expect(attrs()).toEqual({ reveal: 'scratch', reconcealAfterMs: 1500, completeAtPercent: 70 })
    instance.setEditable(false); await flush()
    expect(root.querySelector('[data-md-concealed-view] > .md-concealed-block__chip')).not.toBeNull()
    expect(root.querySelector('[data-md-concealed-view]')!.hasAttribute('data-readonly')).toBe(true)
  })

  it('covers the content for readers and uncovers a copy only on Reveal', async () => {
    const element = await reader(value('button'))
    const block = element.querySelector<HTMLElement>('[data-md-concealed]')!
    expect(block.hasAttribute('data-sealed')).toBe(true)
    const surface = block.querySelector<HTMLElement>('[data-concealed-surface]')!
    expect(surface.textContent).not.toContain('SURPRISE')
    surface.dispatchEvent(new Event('pointerenter')); surface.dispatchEvent(new Event('pointermove')); await flush()
    expect(surface.textContent).not.toContain('SURPRISE')
    const reveal = [...surface.querySelectorAll('button')].find(button => button.textContent === 'Odkrýt')!
    reveal.click(); await flush()
    expect(surface.querySelector('.md-concealed-block__copy')!.textContent).toContain('SURPRISE party on Friday.')
    // The original content keeps laying out the block but stays hidden and single.
    expect([...block.querySelectorAll('p')].filter(p => !p.closest('.md-concealed')).length).toBe(2)
  })

  it('offers scratching without a Reveal button by default', async () => {
    const element = await reader()
    const surface = element.querySelector<HTMLElement>('[data-md-concealed] [data-concealed-surface]')!
    expect([...surface.querySelectorAll('button')].some(button => button.textContent === 'Odkrýt' && !button.hidden)).toBe(false)
  })

  it('lets authors edit the content and preview the reader view; locking the editor changes nothing it shows', async () => {
    const { instance, root } = controller(value())
    const view = root.querySelector<HTMLElement>('[data-md-concealed-view]')!
    expect(view.hasAttribute('data-sealed')).toBe(false)
    expect(view.querySelector('[data-concealed-surface]')).toBeNull()
    expect(view.querySelector('.md-concealed-block__content')!.textContent).toContain('SURPRISE')
    expect(view.querySelector('.md-concealed-block__hint')!.textContent).toBe('Setřením · zakryje za 1,5 s')
    view.querySelector<HTMLElement>('.md-concealed-block__chip')!.click()
    const preview = () => document.querySelector<HTMLButtonElement>('button[aria-label="Náhled pro čtenáře"]') ?? undefined
    await vi.waitFor(() => expect(preview()).toBeDefined())
    preview()!.click(); await flush()
    expect(view.hasAttribute('data-sealed')).toBe(true)
    expect(view.querySelector('[data-concealed-surface]')).not.toBeNull()
    preview()!.click()
    expect(view.querySelector('[data-concealed-surface]')).toBeNull()
    // A lock (Jen pro čtení) guards against accidental edits: the content stays shown, only its editing controls go.
    instance.setEditable(false); await flush()
    expect(view.hasAttribute('data-readonly')).toBe(true)
    expect(view.hasAttribute('data-sealed')).toBe(false)
    expect(view.querySelector('[data-concealed-surface]')).toBeNull()
    expect(view.querySelector('.md-concealed-block__content')!.textContent).toContain('SURPRISE')
    instance.setEditable(true); await flush()
    expect(view.querySelector('[data-concealed-surface]')).toBeNull()
  })

  it('covers the content for a reader of a read-only editor, who may reveal it', async () => {
    const { root } = controller(value('button'), { editable: false }); await flush()
    const view = root.querySelector<HTMLElement>('[data-md-concealed-view]')!
    expect(view.hasAttribute('data-sealed')).toBe(true)
    const surface = view.querySelector<HTMLElement>('[data-concealed-surface]')!
    expect([...surface.querySelectorAll('button')].some(button => button.textContent === 'Odkrýt' && !button.hidden)).toBe(true)
  })

  it('offers the toggle in the slash menu, the toolbar and over a selection, active inside a block', () => {
    const { instance } = controller(value())
    for (const surface of ['slash', 'toolbar', 'selection'] as const)
      expect(instance.actions[surface].map(action => action.id), surface).toContain('concealed.toggle')
    const action = instance.actions.selection[0]!
    expect(instance.query(action.command, { replaceRange: { from: 3, to: 3 } }).active).toBe(false)
    instance.dispatch(toggleConcealedContent(), { replaceRange: { from: 3, to: 3 } })
    expect(instance.query(action.command).active).toBe(true)
  })

  it('rejects a plugin whose selection actions are not a list', () => {
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: plain }, plugins: [{
      id: 'broken', apiVersion: 1, create: () => ({ actions: { selection: {} as never } }),
    }] })).toThrow(/invalid plugin instance/)
  })
})
