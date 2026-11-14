import type { Editor } from '@tiptap/core'
import { dispatchMarkdownInputError } from '../../core/inputErrors'
import { modulesStrings } from '../../ui/strings/modules'

const kept = new WeakMap<Editor, Map<string, unknown>>()
const keyOf = (kind: string, base: unknown) => `${kind}\n${JSON.stringify(base)}`

/**
 * A component's unfinished input when writing stops under it (a lock, a revoked right, offline): kept for the next edit of the same
 * node (`base` is the node's data it was started from) and announced on the page. Nothing is written.
 */
export function retainMarkdownDraft(editor: Editor, kind: string, label: string, base: unknown, draft: unknown) {
  if (editor.isDestroyed || editor.isEditable) return
  let drafts = kept.get(editor)
  if (!drafts) kept.set(editor, drafts = new Map())
  drafts.set(keyOf(kind, base), draft)
  dispatchMarkdownInputError(editor.view.dom, { message: modulesStrings.draftNotSaved(label), source: 'transaction' })
}

/** The draft retained for a node whose data is still `base`, once; a node a colleague changed meanwhile gets none (rule 6). */
export function takeMarkdownDraft<T>(editor: Editor, kind: string, base: unknown): T | undefined {
  const key = keyOf(kind, base), drafts = kept.get(editor), draft = drafts?.get(key) as T | undefined
  drafts?.delete(key)
  return draft
}
