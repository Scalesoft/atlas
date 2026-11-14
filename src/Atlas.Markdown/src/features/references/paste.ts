import type { Editor } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import type { MarkdownReferenceProvider } from '../../core/plugins/types'
import { clonePlainData, deepFreezePlainData } from '../../core/plainData'

/** Cold and loaded pointer handling use the same immutable projection and navigation policy. */
export function referenceValue(provider: MarkdownReferenceProvider, node: Node) {
  try {
    const value = provider.read(deepFreezePlainData(clonePlainData(node.toJSON())))
    if (!value || !value.url.trim() || !value.label.trim() || value.url !== value.url.trim() || /[\u0000-\u001f\u007f]/.test(value.url)) return null
    const url = new URL(value.url, 'https://editor.invalid/')
    return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? value : null
  } catch { return null }
}

export function matchesReferenceUrl(provider: MarkdownReferenceProvider, value: string): boolean {
  if (!/^https?:\/\/[^\s]+$/i.test(value) || /[\u0000-\u001f\u007f]/.test(value)) return false
  try {
    const url = new URL(value)
    return !url.username && !url.password && Boolean(provider.resolveUrl && provider.matchUrl?.(value))
  } catch { return false }
}

/** Pure paste policy; enrichment and UI are loaded only after this recognizes a URL. */
export function referencePaste(editor: Editor, providers: readonly MarkdownReferenceProvider[], event: ClipboardEvent, shift: boolean) {
  const data = event.clipboardData, selection = editor.state.selection
  if (!editor.isEditable || editor.view.composing || !selection.empty || shift || (event as ClipboardEvent & { shiftKey?: boolean }).shiftKey || !data || data.files.length || data.getData('text/html')) return null
  const url = data.getData('text/plain'), marks = editor.state.storedMarks ?? selection.$from.marks()
  if (selection.$from.parent.type.spec.code || marks.some(mark => mark.type.spec.code)) return null
  const choices = providers.filter(provider => matchesReferenceUrl(provider, url)), link = editor.schema.marks.link
  if (!choices.length || !link) return null
  const pastedMarks = [...marks.filter(mark => mark.type.name !== 'link'), link.create({ href: url })]
  return selection.$from.parent.type.allowsMarks(pastedMarks) ? { url, choices, marks: pastedMarks } : null
}
