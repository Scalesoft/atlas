import { Extension, InputRule } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { markMarkdownAutoConversion, registerMarkdownChipText } from '../../core'

// Back to text, an inline formula is the `$…$` it was typed as.
registerMarkdownChipText('mathInline', node => `$${(node.attrs.data as { expression?: string } | null)?.expression ?? ''}$`)

/** Asks the block that was just made to open its source field once it renders (`$$` + Enter). */
const wanted = new WeakSet<object>()
export const requestMathEdit = (editor: object) => { wanted.add(editor) }
export const takeMathEditRequest = (editor: object) => wanted.delete(editor)

/** Digits, signs and separators only is a price („$5 a $10“), not a formula. */
const money = /^[\d\s.,+\-–]+$/
// `$x$` after a space, a bracket or the line start; the closing `$` is followed by the character that was just typed.
// Typed after an opening `$` that is still open: the text is a formula in the making, unless it starts as a price („$5 a pak…“)
// with nothing of a formula in it, which would otherwise switch the text rules off for the rest of the paragraph.
const opened = /(?:^|[\s(])\$([^\s$][^$]*)$/
const formulaInMaking = (before: string) => { const open = opened.exec(before)?.[1]; return open !== undefined && (!/^\d/.test(open) || /[\\^_{}]/.test(open)) }
const inline = /(^|[\s(])\$([^\s$](?:[^$]*[^\s$])?)\$([\s.,;:!?)\]])$/

/**
 * Typing `$x^2$ ` makes an inline formula (Backspace right after gives the text back), `$$` + Enter in an empty paragraph a block
 * one whose field opens. Never in code or links, never for prices.
 */
export function createMathInput(blockDefault: string) {
  return Extension.create({
    name: 'mathInput',
    priority: 1000,
    addInputRules() {
      return [new InputRule({
        find: inline,
        handler({ state, range, match }) {
          const [, lead = '', expression = '', after = ''] = match
          const type = state.schema.nodes.mathInline
          if (!type || money.test(expression) || expression.length > 10_000) return null
          const from = range.from + lead.length
          const code = state.schema.marks.code, link = state.schema.marks.link
          if ([code, link].some(mark => mark && (state.doc.rangeHasMark(from, range.to, mark) || mark.isInSet(state.doc.resolve(range.to).marks())))) return null
          const node = type.create({ data: { version: 1, expression } })
          // An automatic chip: Backspace right after deletes the space that made it (`$x$` comes back), Ctrl+Z undoes the formula and not the typing.
          markMarkdownAutoConversion(state.tr.replaceWith(from, range.to, [node, state.schema.text(after)]), { from, to: from + node.nodeSize + after.length, text: `$${expression}$` })
          return undefined
        },
      })]
    },
    addProseMirrorPlugins() {
      // Typography (`^2` → ², `--`, quotes) and the other text rules must not rewrite a formula while it is typed.
      return [new Plugin({ props: { handleTextInput(view, from, to, text) {
        if (view.composing || !formulaInMaking(view.state.doc.resolve(from).parent.textBetween(0, view.state.doc.resolve(from).parentOffset, undefined, '￼'))) return false
        view.dispatch(view.state.tr.insertText(text, from, to)); return true
      } } })]
    },
    addKeyboardShortcuts() {
      return { Enter: ({ editor }) => {
        const { $from, empty } = editor.state.selection
        if (!empty || $from.parent.type.name !== 'paragraph' || $from.parent.textContent !== '$$' || $from.parentOffset !== 2 || !editor.schema.nodes.mathBlock) return false
        requestMathEdit(editor)
        return editor.chain().insertContentAt({ from: $from.before(), to: $from.after() }, { type: 'mathBlock', attrs: { data: { version: 1, expression: blockDefault } } }).run()
      } }
    },
  })
}
