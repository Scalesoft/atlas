import { openMarkdownReaderPopover, type MarkdownModuleContext } from '../../core'
import type { ContactsData } from './data'
import styles from './style.css?inline'

/**
 * Reader action: anchored details stay available without an editable editor or NodeSelection. `hover`: the card a resting pointer
 * opens (runtime.ts bindHoverCard), which leaves the focus where it is.
 */
export function openContactReader(context: MarkdownModuleContext<ContactsData>, anchor: HTMLElement, title: string, render: (panel: HTMLElement) => void | (() => void), hover = false): () => void {
  return openMarkdownReaderPopover({
    anchor, editor: context.editor, root: context.rootContainer ?? context.editor?.view.dom ?? context.container,
    title, signal: context.signal, className: hover ? 'md-contact-reader md-contact-hover' : 'md-contact-reader', autoFocus: !hover,
    render(body) {
      // The panel lives in the document body, outside the plugin's scoped styles.
      const css = body.ownerDocument.createElement('style'); css.textContent = styles
      body.before(css)
      return render(body)
    },
  })
}
