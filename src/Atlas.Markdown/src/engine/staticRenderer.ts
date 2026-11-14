import type { Editor, JSONContent } from '@tiptap/core'
import { DOMSerializer } from '@tiptap/pm/model'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import type { MarkdownPlugin } from '../core/plugins/types'
import type { MarkdownProfile, MarkdownValueInput } from '../core/types'
import { clonePlainData, deepFreezePlainData } from '../core/plainData'
import { MarkdownDocumentCodec } from './documentCodec'
import { destroyEditor, initializationFailure } from './editorLifecycle'
import { createTiptapEditor } from './editorFactory'
import type { MarkdownResourcePolicy } from '../features/images/resourcePolicy'
import { decorateStaticCallouts } from '../extensions/ContentBlocks'
import { decorateStaticTimedLists } from '../features/timed/timedList'

export interface PreparedMarkdownRender {
  /** Insert the prepared fragment, then run static plugin hooks. May only be called once. */
  mount(container: HTMLElement): () => void
  /** Abort an unused preparation. Idempotent, and also disposes a mounted render. */
  dispose(): void
}

/** Validate and serialize without changing the eventual target container. */
export function prepareMarkdownValue<P extends MarkdownProfile>(
  value: MarkdownValueInput<P>,
  profile: P,
  plugins: readonly MarkdownPlugin[],
  ownerDocument: Document,
  resourcePolicy?: MarkdownResourcePolicy,
): PreparedMarkdownRender {
  const registry = new MarkdownPluginRegistry(plugins, profile)
  let editor: Editor | null = null
  let fragment: DocumentFragment
  let document: Readonly<JSONContent>
  try {
    const codec = new MarkdownDocumentCodec(profile, registry, { profile, resourcePolicy })
    const normalized = codec.normalize(value)
    editor = createTiptapEditor({
      element: null,
      editable: false,
      extensions: codec.extensions,
      content: (typeof normalized === 'string' ? normalized : normalized.document) as string | JSONContent,
    })
    codec.validateParsedDocument(editor.state.doc)
    document = deepFreezePlainData(clonePlainData(editor.state.doc.toJSON()))
    const rendered = DOMSerializer.fromSchema(editor.schema).serializeFragment(editor.state.doc.content, { document: ownerDocument })
    fragment = rendered.nodeType === ownerDocument.DOCUMENT_FRAGMENT_NODE
      ? rendered as DocumentFragment
      : ownerDocument.createDocumentFragment()
    if (fragment !== rendered) fragment.append(rendered)
    decorateStaticCallouts(fragment)
    decorateStaticTimedLists(fragment)
  } catch (error) {
    const errors = destroyEditor(editor)
    try { registry.destroy() } catch (error) { errors.push(error) }
    throw initializationFailure(error, errors)
  }

  const editorErrors = destroyEditor(editor)
  if (editorErrors.length) {
    try { registry.destroy() } catch (error) { editorErrors.push(error) }
    throw new AggregateError(editorErrors, 'Static renderer setup cleanup failed.')
  }

  let disposed = false
  let mounted = false
  let mountedContainer: HTMLElement | null = null
  const dispose = () => {
    if (disposed) return
    disposed = true
    const errors: unknown[] = []
    try { registry.destroy() } catch (error) { errors.push(error) }
    mountedContainer?.replaceChildren()
    if (errors.length) throw new AggregateError(errors, 'Static renderer cleanup failed.')
  }

  return {
    mount(container) {
      if (disposed) throw new Error('Prepared static render is already disposed.')
      if (mounted) throw new Error('Prepared static render may only be mounted once.')
      if (container.ownerDocument !== ownerDocument) throw new Error('Prepared static render belongs to a different document.')
      mounted = true
      mountedContainer = container
      try {
        container.replaceChildren(fragment)
        registry.render(container, document, ownerDocument)
      }
      catch (error) {
        const errors: unknown[] = []
        try { dispose() } catch (cleanupError) { errors.push(cleanupError) }
        throw initializationFailure(error, errors)
      }
      return dispose
    },
    dispose,
  }
}

/** Convenience wrapper for callers that do not need an atomic render swap. */
export function renderMarkdownValue<P extends MarkdownProfile>(
  value: MarkdownValueInput<P>,
  profile: P,
  plugins: readonly MarkdownPlugin[],
  container: HTMLElement,
  resourcePolicy?: MarkdownResourcePolicy,
): () => void {
  return prepareMarkdownValue(value, profile, plugins, container.ownerDocument, resourcePolicy).mount(container)
}
