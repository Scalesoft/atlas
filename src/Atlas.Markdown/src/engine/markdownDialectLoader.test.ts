// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'

afterEach(() => { document.body.replaceChildren() })

// A fresh module graph: the setup file has not loaded this copy of the dialect.
it('reads Markdown without a- components before the dialect loads, and fails one with a component until it has', async () => {
  vi.resetModules()
  const { createMarkdownEditorController, loadMarkdownDialect, parseMarkdownContent, serializeMarkdownContent } = await import('../core')
  const { editorForController } = await import('../core/internal')
  const { chartNode, createChartsPlugin } = await import('../plugins/charts')
  const plugins = [createChartsPlugin()]
  const controller = createMarkdownEditorController({ profile: 'rich', plugins, source: { kind: 'local', value: {
    format: 'atlas-rich', version: 1, plugins: [{ id: 'charts', version: plugins[0]!.schemaVersion ?? 1 }], document: { type: 'doc', content: [{ type: 'paragraph' }] },
  } } })
  controller.attachView(document.body.appendChild(document.createElement('div')), { ariaLabel: 'Dialect' })
  const editor = editorForController(controller)!
  const chart = '```a-chart\n---\n|  | A |\n| --- | --- |\n| x | 1 |\n```'
  const withChart = editor.schema.nodeFromJSON({ type: 'doc', content: [chartNode({ version: 1, title: '', kind: 'bar', grid: [['', 'A'], ['x', '1']], source: null, sourceStatus: null })] })

  expect(serializeMarkdownContent(editor, parseMarkdownContent(editor, '# Text [link](https://a.cz)'))).toBe('# Text [link](https://a.cz)')
  expect(() => parseMarkdownContent(editor, chart)).toThrow(/not loaded/)
  expect(() => serializeMarkdownContent(editor, withChart)).toThrow(/not loaded/)

  await loadMarkdownDialect()
  expect(serializeMarkdownContent(editor, parseMarkdownContent(editor, chart))).toBe(chart)
  expect(serializeMarkdownContent(editor, withChart)).toBe(chart)
  controller.destroy()
})
