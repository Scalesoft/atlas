// @vitest-environment jsdom
import { expect, it } from 'vitest'
import { createMarkdownEditorController } from '../../core'
import { createAttachmentsPlugin, insertAttachment } from './index'
it('dispatches exported attachment commands with host upload options and preserves metadata', () => {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } }, plugins: [createAttachmentsPlugin({ upload: { upload: async () => ({ name: 'x', url: '/x' }) } })] })
  const container = document.createElement('div'); document.body.append(container)
  try {
    controller.attachView(container, { ariaLabel: 'Přílohy' })
    expect(controller.dispatch(insertAttachment({ version: 1, name: 'Zpráva.pdf', url: '/files/report.pdf', size: 2048, mimeType: 'application/pdf' }))).toBe(true)
    const projection = controller.getProjection()
    expect(projection.markdown).toContain('/files/report.pdf')
    expect(projection.markdownLossy).toBe(true)
    expect(projection.rich.plugins).toContainEqual({ id: 'attachments', version: 1 })
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getProjection().text).not.toContain('Zpráva.pdf')
  } finally { controller.destroy(); container.remove() }
})
