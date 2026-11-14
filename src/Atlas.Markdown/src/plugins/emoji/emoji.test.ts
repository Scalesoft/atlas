// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../../core'
import { renderMarkdownValue } from '../../engine/staticRenderer'
import { emojis } from '@tiptap/extension-emoji'
import { createEmojiPlugin, emojiNode, insertEmoji, searchEmoji } from './index'
import table from './unicode.json'

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
function mount(profile: 'rich' | 'markdown-safe' = 'rich', text = '') {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture-emoji', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }
  const value = profile === 'rich' ? { format: 'atlas-rich' as const, version: 1 as const, document: { type: 'doc', content: [{ type: 'paragraph', content: text ? [{ type: 'text', text }] : [] }] } } : text
  const controller = createMarkdownEditorController({ profile, plugins: [createEmojiPlugin(), capture], source: { kind: 'local', value } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Emoji' })
  return { controller, editor, host }
}
describe('Unicode emoji plugin', () => {
  it('stores upstream versioned rich nodes and declares identity loss in Unicode exports', () => {
    const { controller, host } = mount()
    expect(controller.dispatch(insertEmoji('grinning'))).toBe(true)
    expect(controller.getValue()).toMatchObject({ plugins: [{ id: 'emoji', version: 1 }], document: { content: [{ content: [emojiNode('grinning')] }] } })
    expect(controller.getProjection()).toMatchObject({ text: '😀', markdown: '😀', markdownLossy: true })
    expect(host.querySelector('[data-type="emoji"]')?.textContent).toBe('😀')
    expect(host.querySelector('img[src]')).toBeNull()
  })
  it('keeps markdown-safe entirely textual and roundtrips Unicode without a schema extension', () => {
    const { controller, editor } = mount('markdown-safe')
    expect(editor.schema.nodes.emoji).toBeUndefined()
    expect(controller.dispatch(insertEmoji('grinning'))).toBe(true)
    expect(controller.getValue()).toBe('😀')
    expect(controller.getProjection()).toMatchObject({ text: '😀', markdownLossy: false })
    controller.setValue(controller.getValue())
    expect(controller.getProjection().text).toBe('😀')
  })
  it('preserves existing emoticons, skin tones and unknown joined sequences as text', () => {
    const text = ':) ✌🏽 👩🏽‍💻 🫩'
    for (const profile of ['rich', 'markdown-safe'] as const) {
      const { controller, editor } = mount(profile, text)
      expect(controller.getProjection().text).toBe(text)
      expect(editor.getJSON().content?.[0]?.content).toEqual([{ type: 'text', text }])
    }
  })
  it('rejects unknown names, respects readonly and separates two insertion undo steps', () => {
    const { controller } = mount()
    expect(controller.dispatch(insertEmoji('not-an-emoji'))).toBe(false)
    expect(controller.dispatch(insertEmoji('grinning'))).toBe(true)
    expect(controller.dispatch(insertEmoji('heart'))).toBe(true)
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getProjection().text).toBe('😀')
    controller.setEditable(false)
    expect(controller.dispatch(insertEmoji('heart'))).toBe(false)
    expect(controller.getProjection().text).toBe('😀')
  })
  it('renders persisted rich nodes without images and reopens their identity', () => {
    const { controller } = mount(); controller.dispatch(insertEmoji('heart'))
    const value = controller.getValue() as MarkdownRichValue
    const root = document.createElement('div')
    const cleanup = renderMarkdownValue(value, 'rich', [createEmojiPlugin()], root)
    expect(root.textContent).toBe('❤'); expect(root.querySelector('img')).toBeNull()
    controller.setValue(value)
    expect(controller.getValue()).toEqual(value)
    cleanup()
  })
  it('searches upstream names, shortcodes and Czech aliases with a bounded result list', async () => {
    expect((await searchEmoji('srdce')).some(item => item.name === 'heart')).toBe(true)
    expect((await searchEmoji('grinning'))[0]?.emoji).toBe('😀')
    expect((await searchEmoji('', 5)).map(item => item.name)).toEqual(['grinning', 'smile', 'joy', 'heart_eyes', 'wink'])
    expect(await searchEmoji('regional indicator')).toEqual([])
    expect(await searchEmoji('skin tone')).toEqual([])
    expect(await searchEmoji('totally nonexistent query')).toEqual([])
  })
  it('preserves pasted Unicode variation selectors, modifiers, joined sequences and shortcodes', () => {
    const { editor, controller } = mount()
    const text = '❤️ ✌🏽 👩🏽‍💻 🫩 :heart: :)'
    editor.commands.insertContent({ type: 'text', text })
    expect(editor.getJSON().content?.[0]?.content).toEqual([{ type: 'text', text }])
    expect(controller.getProjection().text).toBe(text)
  })
  it.each(['rich', 'markdown-safe'] as const)('selects from the shared keyboard picker and undoes only its replacement (%s)', async profile => {
    const { editor, controller } = mount(profile)
    editor.commands.focus(undefined, { scrollIntoView: false })
    editor.commands.insertContent(':grinning')
    await vi.waitFor(() => expect(document.querySelector('[data-testid="emoji-menu"] [role="option"]')).not.toBeNull())
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    expect(controller.getProjection().text).toBe('😀')
    expect(document.querySelector('[data-testid="emoji-menu"]')).toBeNull()
    controller.dispatch({ type: 'undo' })
    expect(controller.getProjection().text).toBe(':grinning')
  })
  it.each(['https://host/:gr', '12:30', 'email:gr'])('does not activate inside %s', async text => {
    const { editor } = mount()
    editor.commands.focus(undefined, { scrollIntoView: false }); editor.commands.insertContent(text)
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(document.querySelector('[data-testid="emoji-menu"]')).toBeNull()
  })
  it('selects by mouse without leaving text focus and closes on readonly', async () => {
    const { editor, controller } = mount()
    editor.commands.focus(undefined, { scrollIntoView: false }); editor.commands.insertContent(':grinning')
    await vi.waitFor(() => expect(document.querySelector('[data-testid="emoji-menu"] [role="option"]')).not.toBeNull())
    document.querySelector<HTMLButtonElement>('[data-testid="emoji-menu"] [role="option"]')!.click()
    expect(controller.getProjection().text).toBe('😀')
    editor.commands.insertContent(' :heart')
    await vi.waitFor(() => expect(document.querySelector('[data-testid="emoji-menu"]')).not.toBeNull())
    controller.setEditable(false)
    await vi.waitFor(() => expect(document.querySelector('[data-testid="emoji-menu"]')).toBeNull())
    expect(controller.getProjection().text).toBe('😀 :heart')
  })
  it.each(['code', 'codeBlock'])('does not activate in %s', async kind => {
    const { editor } = mount()
    editor.commands.focus(undefined, { scrollIntoView: false })
    if (kind === 'code') editor.commands.toggleCode()
    else editor.commands.toggleCodeBlock()
    editor.commands.insertContent(':grinning')
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(document.querySelector('[data-testid="emoji-menu"]')).toBeNull()
  })
  it('keeps unicode.json equal to the upstream Unicode catalog (names, first owner of every shortcode, emoticons)', async () => {
    // After a Tiptap upgrade: ATLAS_UPDATE_EMOJI=1 pnpm --filter @atlas/markdown exec vitest run --configLoader runner src/plugins/emoji/emoji.test.ts
    const unicode = emojis.filter(item => item.emoji)
    const owner = (key: string) => unicode.find(item => key === item.name || item.shortcodes.includes(key))!
    const expected = { names: {} as Record<string, string>, shortcodes: {} as Record<string, string>, emoticons: {} as Record<string, string> }
    for (const item of unicode) expected.names[item.name] ??= item.emoji!
    for (const key of unicode.flatMap(item => [item.name, ...item.shortcodes])) if (owner(key).name !== key) expected.shortcodes[key] = owner(key).name
    for (const item of unicode) for (const face of item.emoticons ?? []) expected.emoticons[face] ??= item.name
    if ((globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ATLAS_UPDATE_EMOJI === '1') {
      const fs = await import(/* @vite-ignore */ `node:${'fs'}`) as { writeFileSync(path: string, data: string): void }
      const { fileURLToPath } = await import(/* @vite-ignore */ `node:${'url'}`) as { fileURLToPath(url: string): string }
      fs.writeFileSync(fileURLToPath(import.meta.url).replace(/emoji\.test\.ts$/, 'unicode.json'), `${JSON.stringify(expected, null, 1)}\n`)
      return
    }
    expect(table).toEqual(expected)
  })
})
