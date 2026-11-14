// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { createMarkdownEditorController, type MarkdownPlugin } from '../core'
import { editorForController } from '../core/internal'
import { createAttachmentsPlugin } from './attachments'
import { createChartsPlugin } from './charts'
import { createContactsPlugin } from './contacts'
import { createDiagramsPlugin } from './diagrams'
import { createMathPlugin } from './math'
import { createOpenApiPlugin } from './openapi'
import { createTocPlugin } from './toc'

// UX ED-6: the slash menu showed „Graf“, „Diagram“, „API“… without the line saying what they are, unlike every other item.
describe('slash items of the block plugins', () => {
  it('each say in a second line what the block is for', () => {
    const plugins: MarkdownPlugin[] = [createAttachmentsPlugin(), createChartsPlugin(), createContactsPlugin(), createDiagramsPlugin(), createMathPlugin(), createOpenApiPlugin(), createTocPlugin()]
    const actions = plugins.flatMap(plugin => plugin.create({ profile: 'rich', signal: new AbortController().signal } as never).actions?.slash ?? [])
    expect(actions.length).toBeGreaterThanOrEqual(9)
    for (const action of actions) expect(action.description, action.label).toMatch(/\S{3}/)
  })
  // UX ED-9: `/graf koláč` and `/diagram sekvence` insert that kind straight away, like `/tabulka 3x4` and `/kód ts`.
  it('take the kind of a chart or a diagram after the name', () => {
    const inserted = (plugin: MarkdownPlugin, text: string) => {
      const controller = createMarkdownEditorController({ profile: 'rich', plugins: [plugin], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
      try {
        const host = document.createElement('div'); document.body.append(host)
        controller.attachView(host, { ariaLabel: 'Editor' })
        const command = controller.actions.slash[0]!.parameter!.command(text)
        if (!command || !controller.dispatch(command)) return undefined
        return (editorForController(controller)!.getJSON().content ?? []).find(node => node.type !== 'paragraph')?.attrs?.data
      } finally { controller.destroy() }
    }
    const chart = createChartsPlugin().create({ profile: 'rich', signal: new AbortController().signal } as never).actions!.slash![0]!.parameter!
    expect(chart.title!('koláč')).toBe('Graf: Koláčový')
    expect(inserted(createChartsPlugin(), 'kolac')).toMatchObject({ kind: 'pie' })
    expect(inserted(createChartsPlugin(), 'sloupce')).toMatchObject({ kind: 'bar' })
    expect(chart.command('hvězda')).toBeUndefined()
    const diagram = createDiagramsPlugin().create({ profile: 'rich', signal: new AbortController().signal } as never).actions!.slash![0]!.parameter!
    expect(diagram.title!('sekvence')).toBe('Sekvenční diagram')
    expect(inserted(createDiagramsPlugin(), 'sekvence')).toMatchObject({ source: expect.stringMatching(/^sequenceDiagram/) })
    expect(inserted(createDiagramsPlugin(), 'gantt')).toMatchObject({ source: expect.stringMatching(/^gantt/) })
    expect(diagram.command('xyz')).toBeUndefined()
  })
})
