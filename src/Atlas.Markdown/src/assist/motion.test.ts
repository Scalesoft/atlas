// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Node, type Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '../core'
import { createAssistPlugin, type MarkdownAssistApplyContext, type MarkdownAssistBlock, type MarkdownAssistSuggestion } from './index'

// jsdom has no layout: ProseMirror measures ranges when it scrolls the selection into view.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

type Call = { element: Element; keyframes: Keyframe[]; options: KeyframeAnimationOptions; cancel: ReturnType<typeof vi.fn> }
let calls: Call[] = []
const controllers: MarkdownEditorController[] = []
beforeEach(() => {
  vi.useFakeTimers()
  calls = []
  Element.prototype.animate = vi.fn(function (this: Element, keyframes: Keyframe[] | PropertyIndexedKeyframes | null, options?: number | KeyframeAnimationOptions) {
    const cancel = vi.fn()
    calls.push({ element: this, keyframes: keyframes as Keyframe[], options: options as KeyframeAnimationOptions, cancel })
    return { finished: Promise.resolve(), cancel, finish() {}, addEventListener() {}, removeEventListener() {} } as unknown as Animation
  }) as typeof Element.prototype.animate
})
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  delete (Element.prototype as Partial<Element>).animate
  vi.useRealTimers(); vi.restoreAllMocks()
})
const settle = () => vi.advanceTimersByTimeAsync(0)
const text = 'Kontaktujte Jana Nováka, heslo Srv#2026!rack.'

/** An inline component like the ones Atlas inserts (contact, YouTrack issue). */
const chipPlugin: MarkdownPlugin = { id: 'assist-test-chip', apiVersion: 1, schemaVersion: 1, profiles: ['rich'], create: () => ({ extensions: [Node.create({
  name: 'assistTestChip', group: 'inline', inline: true, atom: true,
  parseHTML: () => [{ tag: 'span[data-test-chip]' }],
  renderHTML: () => ['span', { 'data-test-chip': '' }, 'Jan Novák'],
})] }) }

type Apply = (context: MarkdownAssistApplyContext) => boolean | Promise<boolean>
function mount(reduced = false, onApply?: Apply, plugins: MarkdownPlugin[] = [], onWarning?: Apply) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }
  const suggest = (block: MarkdownAssistBlock): MarkdownAssistSuggestion[] => {
    const name = block.text.indexOf('Jana Nováka'), secret = block.text.indexOf('Srv#2026!rack')
    if (name < 0) return []
    return [
      { id: 'contact', kind: 'contact.inline', title: 'Nahradit „Jana Nováka“ kontaktem', icon: 'user', range: block.rangeOf(name, name + 11),
        apply: onApply ?? (context => context.dispatch({ type: 'paste-markdown', text: 'Jan Novák' }, { replaceRange: context.range! })) },
      ...secret < 0 ? [] : [{ id: 'secret', kind: 'secret.plaintext', title: 'Možné heslo v textu', icon: 'triangle-alert' as const, tone: 'warning' as const,
        range: block.rangeOf(secret, secret + 13), apply: onWarning ?? (() => true) }],
    ]
  }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createAssistPlugin({ sources: [{ id: 'demo', suggest }], reducedMotion: () => reduced }), capture, ...plugins],
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Dokument' })
  editor.view.focus()
  return { editor, controller }
}
const within = (selector: string) => calls.filter(call => call.element.matches(selector))
async function showMarkers(editor: Editor) {
  editor.commands.insertContent(text)
  await vi.advanceTimersByTimeAsync(1500); await settle()
}
/** The block's list opens at its marker (Alt+Enter is core's „Co s tím jde dělat“, which lists the same suggestions without motion). */
const openList = (_editor: Editor) => { const marker = [...document.querySelectorAll<HTMLButtonElement>('.md-assist-marker:not([data-leaving])')].at(-1)!; marker.dispatchEvent(new PointerEvent('pointerdown')); marker.click() }
const enter = (editor: Editor) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))

describe('assist motion', () => {
  it('marker, menu, morph, ripple and conceal use the prototype keyframes and durations', async () => {
    let concealed: Promise<void> | undefined
    const { editor } = mount(false)
    await showMarkers(editor)
    const ring = within('.md-assist-marker .md-assist-ring')
    expect(ring[0]).toMatchObject({ keyframes: [{ transform: 'rotate(-140deg) scale(.5)', opacity: 0 }, { transform: 'none', opacity: 1 }],
      options: expect.objectContaining({ duration: 280, easing: 'cubic-bezier(.22,1,.36,1)' }) })
    const icon = within('.md-assist-marker .md-assist-marker-icon')
    expect(icon[0]).toMatchObject({ keyframes: [{ opacity: 0, transform: 'scale(.4)' }, { opacity: 1, transform: 'none' }], options: expect.objectContaining({ duration: 240, delay: 60 }) })
    const ripple = within('.md-assist-marker .md-assist-ripple')
    expect(ripple).toHaveLength(1)
    expect(ripple[0]).toMatchObject({ keyframes: [{ opacity: .7, transform: 'scale(1)' }, { opacity: 0, transform: 'scale(2.4)' }],
      options: expect.objectContaining({ duration: 600, easing: 'cubic-bezier(.2,.7,.3,1)' }) })
    const bar = within('.md-assist-warning')
    expect(bar[0]?.options).toMatchObject({ duration: 280 })

    calls = []
    openList(editor); await settle()
    const menu = within('.md-assist-menu')
    expect(menu[0]).toMatchObject({ keyframes: [{ opacity: 0, transform: 'scale(.94) translateY(-4px)' }, { opacity: 1, transform: 'none' }], options: expect.objectContaining({ duration: 160 }) })
    const hit = within('.md-assist-hit')
    expect(hit[0]).toMatchObject({ keyframes: [{ backgroundSize: '0% 1.5px' }, { backgroundSize: '100% 1.5px' }], options: expect.objectContaining({ duration: 200 }) })

    calls = []
    enter(editor); await settle()
    expect(within('.md-assist-menu')[0]).toMatchObject({ keyframes: [{ opacity: 1 }, { opacity: 0, transform: 'scale(.97)' }], options: expect.objectContaining({ duration: 120 }) })
    expect(within('.md-assist-marker')[0]).toMatchObject({ keyframes: [{ opacity: 1 }, { opacity: 0, transform: 'scale(.6)' }], options: expect.objectContaining({ duration: 160 }) })
    // Plain text has no inline component to morph into: the changed text glows for 1.6 s.
    const glow = calls.find(call => call.options.duration === 1600)!
    expect(glow.keyframes[0]).toHaveProperty('backgroundColor')
    expect(glow.keyframes[0]).toHaveProperty('boxShadow')

    // A new editor whose apply conceals first.
    const second = mount(false, async context => { concealed = context.effects.conceal(context.range!); await concealed; return false })
    await showMarkers(second.editor)
    openList(second.editor); await settle(); enter(second.editor)
    await vi.advanceTimersByTimeAsync(60)
    const scrambled = second.editor.view.dom.querySelector('.md-assist-conceal-glyphs')!.textContent!
    expect(scrambled).toHaveLength('Jana Nováka'.length)
    expect(scrambled).not.toBe('Jana Nováka')
    await vi.advanceTimersByTimeAsync(700)
    await concealed
    expect(second.editor.view.dom.querySelector('.md-assist-conceal-glyphs')?.textContent ?? '•'.repeat(11)).toBe('•'.repeat(11))
  })

  it('morphs an inserted inline component from the width of the span and lets it glow', async () => {
    const { editor } = mount(false, context => context.editor.commands.insertContentAt(context.range!, { type: 'assistTestChip' }), [chipPlugin])
    await showMarkers(editor)
    openList(editor); await settle()
    calls = []
    enter(editor); await settle()
    const morph = calls.find(call => call.options.duration === 380)!
    expect(morph.element.matches('[data-test-chip]')).toBe(true)
    expect(morph.keyframes).toHaveLength(3)
    expect(morph.keyframes[0]).toMatchObject({ opacity: .35, transform: 'scale(.96)' })
    expect(morph.keyframes[1]).toMatchObject({ opacity: 1, transform: 'scale(1.03)', offset: .7 })
    expect(morph.keyframes[2]).toMatchObject({ opacity: 1, transform: 'none' })
    expect(morph.options.fill).toBe('none')
    expect(calls.find(call => call.options.duration === 1600)?.element.matches('[data-test-chip]')).toBe(true)
  })

  it('draws a new link underline from the left before its glow', async () => {
    const { editor } = mount(false, context => context.dispatch({ type: 'paste-markdown', text: '[Jana Nováka](https://example.com/jan)' }, { replaceRange: context.range! }))
    await showMarkers(editor)
    openList(editor); await settle()
    calls = []
    enter(editor); await settle()
    const underline = calls.find(call => call.element.matches('a'))!
    expect(underline).toMatchObject({ keyframes: [{ backgroundSize: '0% 1px' }, { backgroundSize: '100% 1px' }], options: expect.objectContaining({ duration: 280 }) })
  })

  it('reduced motion keeps only opacity, colour and background within 160 ms', async () => {
    const { editor } = mount(true)
    await showMarkers(editor)
    openList(editor); await settle()
    enter(editor); await settle()
    expect(calls.length).toBeGreaterThan(4)
    for (const call of calls) {
      for (const frame of call.keyframes) {
        for (const property of Object.keys(frame)) expect(['opacity', 'backgroundColor', 'color', 'offset']).toContain(property)
      }
      expect(call.options.duration).toBeLessThanOrEqual(160)
      expect(call.options.easing).toBe('linear')
    }
  })

  it('conceal changes no document content and is instant under reduced motion', async () => {
    let done = false
    const { editor, controller } = mount(true, async context => { await context.effects.conceal(context.range!); done = true; return false })
    await showMarkers(editor)
    const before = JSON.stringify(controller.getValue())
    const transactions = vi.fn(); editor.on('transaction', ({ transaction }) => transactions(transaction.docChanged))
    openList(editor); await settle(); enter(editor); await settle()
    expect(editor.view.dom.querySelector('.md-assist-conceal-glyphs')?.textContent).toBe('•'.repeat(11))
    await vi.advanceTimersByTimeAsync(160)
    expect(done).toBe(true)
    expect(transactions.mock.calls.every(([changed]) => changed === false)).toBe(true)
    expect(JSON.stringify(controller.getValue())).toBe(before)
  })

  it('takes token durations as the host resolved them and scales only the fixed prototype literals', async () => {
    const { editor } = mount(false)
    // A host that slows its motion down passes slowed tokens (Atlas: --atlas-motion-* × --atlas-motion-scale);
    // the time scale multiplies only what has no token (ripple 600, icon 240/60), so nothing is slowed twice.
    editor.view.dom.style.setProperty('--md-assist-time-scale', '3')
    editor.view.dom.style.setProperty('--md-assist-slow', '840ms')
    await showMarkers(editor)
    expect(within('.md-assist-marker .md-assist-ring')[0]?.options.duration).toBe(840)
    expect(within('.md-assist-marker .md-assist-marker-icon')[0]?.options).toMatchObject({ duration: 720, delay: 180 })
    expect(within('.md-assist-marker .md-assist-ripple')[0]?.options.duration).toBe(1800)
  })

  it('reads the core motion tokens when the assist ones are not set, and the core time scale', async () => {
    const { editor } = mount(false)
    editor.view.dom.style.setProperty('--md-motion-slow', '420ms')
    editor.view.dom.style.setProperty('--md-motion-time-scale', '2')
    await showMarkers(editor)
    expect(within('.md-assist-marker .md-assist-ring')[0]?.options.duration).toBe(420)
    expect(within('.md-assist-marker .md-assist-ripple')[0]?.options.duration).toBe(1200)
  })

  it('collapses the warning bar as the conceal starts (prototype order) and brings it back when nothing is written', async () => {
    let release!: (written: boolean) => void
    let scrambled = ''
    const { editor } = mount(false, undefined, [], async context => {
      const concealed = context.effects.conceal(context.range!)
      scrambled = editor.view.dom.querySelector('.md-assist-conceal-glyphs')?.textContent ?? ''
      await concealed
      return new Promise<boolean>(resolve => { release = resolve })
    })
    await showMarkers(editor)
    const bar = editor.view.dom.querySelector<HTMLElement>('.md-assist-warning')!
    calls = []
    bar.querySelector<HTMLButtonElement>('.md-btn-primary')!.click(); await settle()
    const collapse = within('.md-assist-warning')
    expect(collapse).toHaveLength(1)
    expect(collapse[0]!.keyframes.at(-1)).toMatchObject({ height: '0px', opacity: 0 })
    expect(collapse[0]!.options.duration).toBe(200)
    // The bar folds while the characters are still being scrambled, not after the dots.
    expect(scrambled).toHaveLength(13)
    expect(scrambled).not.toBe('•'.repeat(13))
    await vi.advanceTimersByTimeAsync(800)
    expect(collapse[0]!.cancel).not.toHaveBeenCalled()
    release(false); await settle()
    expect(collapse[0]!.cancel).toHaveBeenCalledTimes(1)
    expect(bar.isConnected).toBe(true)
  })
})
