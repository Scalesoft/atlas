import { mergeAttributes, Node } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { loadedMarkdownDialect } from '../engine/markdownDialectLoader'

interface ParagraphMarkdownState { write(content?: string): void; renderInline(node: PMNode): void; closeBlock(node: PMNode): void }

/**
 * Tiptap's paragraph (same schema, priority, command and shortcut; StarterKit's own is disabled in kit.ts) with one
 * Markdown fix. tiptap-markdown records where a mark such as bold opens before the block's pending separator and line
 * prefix are written, and its whitespace trimming then eats that prefix: a later paragraph of a quote starting with
 * bold came out as `> ****Bold**`. A paragraph that starts with a mark writes the pending prefix first; any other
 * paragraph serializes exactly as prosemirror-markdown's default (renderInline, closeBlock).
 */
export const MarkdownParagraph = Node.create<{ HTMLAttributes: Record<string, unknown> }>({
  name: 'paragraph',
  priority: 1000,
  addOptions: () => ({ HTMLAttributes: {} }),
  group: 'block',
  content: 'inline*',
  parseHTML: () => [{ tag: 'p' }],
  renderHTML({ HTMLAttributes }) { return ['p', mergeAttributes(this.options.HTMLAttributes, HTMLAttributes), 0] },
  addCommands() { return { setParagraph: () => ({ commands }) => commands.setNode(this.name) } },
  addKeyboardShortcuts() { return { 'Mod-Alt-0': () => this.editor.commands.setParagraph() } },
  addStorage: () => ({ markdown: { serialize(state: ParagraphMarkdownState, node: PMNode) {
    // Text that reads as a chart's linked-table id (`{#id}` after a table) is escaped (dialect §2.5); only a chart names one.
    if (loadedMarkdownDialect()?.namesChartTable(node)) { state.write(`\\${node.textContent}`); state.closeBlock(node); return }
    if (node.firstChild?.marks.length) state.write()
    state.renderInline(node)
    state.closeBlock(node)
  } } }),
})
