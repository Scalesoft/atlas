// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import type { MarkdownEditorState } from '../core/types'
import { slashItems } from '../extensions/SlashCommands'
import { tocRuntime } from '../plugins/toc/runtime'
import CollaborationNotice from './CollaborationNotice.vue'
import { markdownStrings, registerMarkdownStrings, setMarkdownStrings } from './strings'
import { tableStrings } from './strings/table'

const original = { cancel: markdownStrings.cancel, collaboration: { ...markdownStrings.collaboration }, slash: { ...markdownStrings.slash }, table: { ...tableStrings }, toc: { ...markdownStrings.toc } }
const apps: App[] = []
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount()); document.body.replaceChildren()
  setMarkdownStrings(original)
})

it('merges a section key by key and leaves the rest of the dictionary alone', () => {
  const rowAbove = markdownStrings.table.insertRowAboveLabel
  setMarkdownStrings({ cancel: 'Cancel', table: { deleteRowLabel: 'Delete row' } })
  expect(markdownStrings.cancel).toBe('Cancel')
  expect(markdownStrings.table.deleteRowLabel).toBe('Delete row')
  expect(tableStrings.deleteRowLabel).toBe('Delete row')
  expect(markdownStrings.table.insertRowAboveLabel).toBe(rowAbove)
  expect(markdownStrings.apply).toBe('Použít')
})

it('keeps overrides made before a section registers, whatever the import order', () => {
  const root = markdownStrings as unknown as Record<string, unknown>
  const saved = root.emoji
  delete root.emoji
  try {
    setMarkdownStrings({ emoji: { pickerLabel: 'Emoji' } })
    const section = registerMarkdownStrings('emoji', { pickerLabel: 'Emoji (výchozí)', pickerDescription: 'Vložit emoji', emptyResults: 'Nic' })
    expect(section).toBe(markdownStrings.emoji)
    expect(section.pickerLabel).toBe('Emoji')
    expect(section.pickerDescription).toBe('Vložit emoji')
    setMarkdownStrings({ emoji: { pickerDescription: 'Insert emoji' } })
    expect(section.pickerDescription).toBe('Insert emoji')
  } finally { root.emoji = saved }
})

it('renders an overridden core string instead of the Czech default', async () => {
  setMarkdownStrings({ collaboration: { pendingChangesNotice: 'Some changes are not confirmed yet.' } })
  const state: MarkdownEditorState = {
    contentRevision: 0, changeOrigin: 'local', lifecycle: 'ready', binding: 'collaborative',
    editable: false, focused: false, connection: 'reconnecting', documentSave: 'pending',
    projectionSave: 'pending', pendingChanges: 2, error: null, peers: [], link: null,
  }
  const host = document.createElement('div'); document.body.append(host)
  const app = createApp({ render: () => h(CollaborationNotice, { state }) }); apps.push(app); app.mount(host)
  await new Promise(resolve => setTimeout(resolve, 1300)); await nextTick()
  expect(host.textContent).toContain('Some changes are not confirmed yet.')
  expect(host.textContent).not.toContain('Některé změny ještě server nepotvrdil')
})

it('renders an overridden plugin string instead of the Czech default', async () => {
  setMarkdownStrings({ toc: { emptyDocument: 'Add headings to build the table of contents.' } })
  const root = document.createElement('div'); root.innerHTML = '<section></section>'; document.body.append(root)
  const container = root.firstElementChild as HTMLElement
  await tocRuntime.render({ container, rootContainer: root, data: { version: 1, title: 'Obsah', minLevel: 1, maxLevel: 3 }, document: { type: 'doc' }, signal: new AbortController().signal })
  expect(container.textContent).toContain('Add headings to build the table of contents.')
  expect(container.textContent).not.toContain('Přidejte do dokumentu nadpisy')
})

it('reads module-level lists at use time, so an override after import still applies', () => {
  const heading = slashItems.find(item => item.id === 'heading-1')!
  expect(heading.title).toBe('Nadpis 1')
  setMarkdownStrings({ slash: { heading1Title: 'Heading 1' } })
  expect(heading.title).toBe('Heading 1')
})
