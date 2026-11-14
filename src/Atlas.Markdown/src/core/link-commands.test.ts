// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { createMarkdownEditorController } from './index'
import { editorForController } from './internal'

const controllers: ReturnType<typeof createMarkdownEditorController>[] = []
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
})

function mount() {
  const controller = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: 'Label' } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Links' })
  editorForController(controller)!.commands.selectAll()
  return { controller, host }
}

describe('link targets and Markdown fidelity', () => {
  it.each(['https://example.test/a%20b', 'http://example.test', 'mailto:user@example.test', 'tel:+420123456789', '/wiki/page#section', '../page', '#section'])(
    'roundtrips the accepted target %s and keeps removal undoable', href => {
      const { controller, host } = mount()
      expect(controller.dispatch({ type: 'set-link', href })).toBe(true)
      const markdown = controller.getValue()
      controller.setValue(markdown)
      expect(host.querySelector('a')?.getAttribute('href')).toBe(href)
      expect(controller.dispatch({ type: 'unset-link' })).toBe(true)
      expect(host.querySelector('a')).toBeNull()
      expect(controller.dispatch({ type: 'undo' })).toBe(true)
      expect(host.querySelector('a')?.getAttribute('href')).toBe(href)
      expect(controller.dispatch({ type: 'undo' })).toBe(true)
      expect(controller.getValue()).toBe('Label')
    },
  )
  it.each(['', 'Page with spaces', '/wiki/a\nb', 'java\tscript:alert(1)', 'javascript:alert(1)', 'data:text/html,hello', 'file:///private'])(
    'rejects %j without changing content or undo', href => {
      const { controller } = mount()
      expect(controller.query({ type: 'set-link', href }).enabled).toBe(false)
      expect(controller.dispatch({ type: 'set-link', href })).toBe(false)
      expect(controller.getValue()).toBe('Label')
      expect(controller.dispatch({ type: 'undo' })).toBe(false)
    },
  )
})
