import { openMarkdownReaderPopover, type MarkdownModuleContext } from '../../core'
import type { ContactsData } from './data'
import styles from './style.css?inline'

/** Reader action: anchored details stay available without an editable editor or NodeSelection. */
export function openContactReader(context: MarkdownModuleContext<ContactsData>, anchor: HTMLElement, title: string, render: (panel: HTMLElement) => void): () => void {
  return openMarkdownReaderPopover({
    anchor, editor: context.editor, root: context.rootContainer ?? context.editor?.view.dom ?? context.container,
    title, signal: context.signal, className: 'md-contact-reader',
    render(body) {
      // The panel lives in the document body, outside the plugin's scoped styles.
      const css = body.ownerDocument.createElement('style'); css.textContent = styles
      body.before(css)
      render(body)
    },
  })
}
