// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownRichValue } from '../../core'
import { createChartsPlugin } from '../../plugins/charts'

vi.mock('../../plugins/charts/chartRenderer', () => ({ createChartRenderer: () => ({ update() {}, destroy() {} }) }))

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })

/** J8.1 / J8.3: a block that is not drawn yet is a quiet skeleton of the finished block's size, and its text stays for assistive technology. */
describe('the stub of a block', () => {
  it('has the height of the finished block from the first frame and keeps its text readable', () => {
    const data = { version: 1, title: 'Příjmy', kind: 'bar', source: null, sourceStatus: null, grid: [['Čtvrtletí', 'Kč'], ['Q1', '1']] }
    const value = { format: 'atlas-rich', version: 1, plugins: [{ id: 'charts', version: 1 }], document: { type: 'doc', content: [{ type: 'chart', attrs: { data } }] } } as MarkdownRichValue
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createChartsPlugin()], source: { kind: 'local', value } })
    controllers.push(controller)
    const host = document.body.appendChild(document.createElement('div'))
    controller.attachView(host, { ariaLabel: 'Graf' })
    const block = host.querySelector<HTMLElement>('[data-md-module="chart"]')!
    const content = block.firstElementChild as HTMLElement
    expect(content.style.minHeight).toBe('389px')
    const stub = content.querySelector('.md-module-stub')
    expect(stub).not.toBeNull()
    expect(stub!.textContent).toContain('Příjmy')
  })
})
