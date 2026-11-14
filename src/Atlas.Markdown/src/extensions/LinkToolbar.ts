import { Extension, getMarkRange, posToDOMRect } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { dispatchCommand } from '../core/commands'
import { MARKDOWN_FOLLOW_LINK_EVENT, MARKDOWN_OPEN_LINK_EVENT } from '../core/keyboardShortcuts'
import { safeMarkdownUrl } from '../core/safeUrl'
import { createMarkdownContextToolbar, type MarkdownContextToolbar } from '../features/modules/contextToolbar'
import { registerMarkdownIntentions } from '../features/intentions/intentions'
import { referencesStrings } from '../ui/strings/references'
import { toolbarStrings } from '../ui/strings/toolbar'

/**
 * The bar of the link the caret is in (a click puts the caret there, it opens nothing): its address, which opens it, „Upravit odkaz“ (the
 * selection bubble's link field) and „Odebrat odkaz“. The same three are rows of „Co s tím jde dělat“ (Ctrl+Space), so no mouse is needed.
 * It only reads the selection: no transaction of its own.
 */
export const LinkToolbar = Extension.create({
  name: 'markdownLinkToolbar',
  addProseMirrorPlugins() {
    const editor = this.editor
    return [new Plugin({ view: view => {
      const doc = view.dom.ownerDocument
      /** The link a caret is inside of: at its edges typed text is no part of it, so neither is the caret. */
      const linkAt = () => {
        const { selection } = editor.state
        return editor.isEditable && selection.empty ? selection.$from.marks().find(mark => mark.type.name === 'link') : undefined
      }
      const range = () => { const link = linkAt(); return link && getMarkRange(editor.state.selection.$from, link.type, link.attrs) }
      const href = () => safeMarkdownUrl(linkAt()?.attrs.href)
      const open = () => {
        const target = href()
        // A host that routes its own addresses (a wiki page in this window) takes the event; anything else opens in a new tab.
        if (target && view.dom.dispatchEvent(new CustomEvent(MARKDOWN_FOLLOW_LINK_EVENT, { bubbles: true, cancelable: true, detail: { href: target } })))
          doc.defaultView?.open(target, '_blank', 'noopener,noreferrer')
      }
      const edit = () => { view.dom.dispatchEvent(new CustomEvent(MARKDOWN_OPEN_LINK_EVENT, { cancelable: true })) }
      // The command's own path (one undo step); the profile only gates rich commands, which this is not.
      const unlink = () => { dispatchCommand(editor, 'markdown-safe', { type: 'unset-link' }); view.focus() }

      let toolbar: MarkdownContextToolbar | undefined, anchor: HTMLElement | undefined, shown = ''
      const close = () => { toolbar?.destroy(); anchor?.remove(); toolbar = anchor = undefined; shown = '' }
      const update = () => {
        const link = linkAt(), at = range()
        const key = link && at ? `${at.from} ${link.attrs.href}` : ''
        if (key === shown) return
        close()
        if (!link || !at || !view.dom.parentElement) return
        shown = key
        // The toolbar needs an element of the page; where it stands is the link's own text (read when placed, not every frame).
        anchor = doc.createElement('span'); anchor.style.display = 'none'; view.dom.parentElement.append(anchor)
        const place = { contextElement: view.dom, getBoundingClientRect: () => { const now = range() ?? at; try { return posToDOMRect(view, now.from, now.to) } catch { return view.dom.getBoundingClientRect() } } }
        const bar: MarkdownContextToolbar = toolbar = createMarkdownContextToolbar({ editor, anchor, positionAnchor: place, placement: 'bottom-start', label: toolbarStrings.linkTitle, selectOnClick: false, markAnchor: false, followFrames: false,
          getPos: () => { const { $from } = editor.state.selection; return $from.depth ? $from.before() : undefined },
          isVisible: (): boolean => !!linkAt() && (view.hasFocus() || bar.element.contains(doc.activeElement)) })
        const address = String(link.attrs.href)
        // The address is in sight: a link whose text was edited (a bare URL) still shows where it leads.
        toolbar.addButton({ label: referencesStrings.open, icon: 'external-link', text: address.length > 48 ? `${address.slice(0, 47)}…` : address, disabled: !href(), onClick: open })
        toolbar.addSeparator()
        toolbar.addButton({ label: toolbarStrings.editLinkAriaLabel, icon: 'edit', onClick: edit })
        toolbar.addButton({ label: toolbarStrings.removeLinkLabel, icon: 'unlink', onClick: unlink })
        toolbar.refresh()
      }
      // The block's menu already lists the bar's buttons (contextToolbar.ts).
      const stopIntentions = registerMarkdownIntentions(editor, ({ block }) => block || !linkAt() ? [] : [
        ...href() ? [{ id: 'link.open', label: referencesStrings.open, icon: 'svg:external-link', run: open }] : [],
        { id: 'link.edit', label: toolbarStrings.editLinkAriaLabel, icon: 'svg:pencil', run: edit },
        { id: 'link.remove', label: toolbarStrings.removeLinkLabel, icon: 'svg:unlink', run: unlink },
      ])
      update()
      return { update, destroy: () => { stopIntentions(); close() } }
    } })]
  },
})
