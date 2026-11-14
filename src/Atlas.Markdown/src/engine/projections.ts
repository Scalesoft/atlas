import type { Editor } from '@tiptap/core'
import type { MarkdownProjection, MarkdownRichValue } from '../core/types'
import { RICH_FORMAT } from '../core/types'
import { builtinMarkdownLossy } from './documentCodec'

const projectors = new WeakMap<Editor, () => MarkdownProjection>()

export function registerEditorProjection(editor: Editor, project: () => MarkdownProjection): void {
  projectors.set(editor, project)
}

/** Controller sessions and legacy editors use one projection contract. */
export function projectEditor(editor: Editor): MarkdownProjection {
  const project = projectors.get(editor)
  if (project) return project()
  const markdown = (editor.storage as { markdown?: { getMarkdown?: () => string } }).markdown?.getMarkdown?.()
  if (markdown === undefined) throw new Error('The editor has no Markdown serializer.')
  return {
    markdown, text: editor.getText({ blockSeparator: '\n\n' }),
    rich: { format: RICH_FORMAT, version: 1, document: editor.getJSON() as unknown as MarkdownRichValue['document'] },
    markdownLossy: builtinMarkdownLossy(editor.state.doc),
  }
}
