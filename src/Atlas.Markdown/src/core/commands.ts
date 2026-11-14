import type { ChainedCommands, Editor } from '@tiptap/core'
import type { CommandState, MarkdownCommand, MarkdownDispatchOptions, MarkdownProfile } from './types'
import type { MarkdownPluginRegistry } from './plugins/registry'
import { resetSelectedTableColumnWidths } from '../features/table/commands'
import { clearCellContent, selectedCellsHaveAttribute } from '../features/table/cellCommands'
import { canGrowSelectedTable, HEADING_LEVELS, isValidCellColor, isValidRowHeight, isValidTableSize, MAX_MARKDOWN_TEXT_LENGTH } from './documentConstraints'
import { safeMarkdownUrl } from './safeUrl'
import { sortSelectedColumn } from '../features/table/rowOrder'
import { closeMarkdownHistory } from '../engine/historyBoundary'
import { retypeList } from '../features/timed/timedList'

const richCommands = new Set(['toggle-underline', 'toggle-highlight', 'merge-cells', 'split-cell', 'set-cell-background', 'set-row-height', 'insert-callout'])

function permitted(editor: Editor, profile: MarkdownProfile, command: MarkdownCommand): boolean {
  if (profile === 'markdown-safe' && richCommands.has(command.type)) return false
  switch (command.type) {
    case 'insert-callout': return command.variant === undefined || ['note', 'tip', 'warning', 'important', 'caution'].includes(command.variant)
    case 'insert-table':
      return isValidTableSize(command.rows, command.columns)
    case 'toggle-code-block': return command.language === undefined || (typeof command.language === 'string' && /^[\w+#.-]{1,32}$/u.test(command.language))
    case 'set-heading': return (HEADING_LEVELS as readonly number[]).includes(command.level)
    case 'set-link': return safeMarkdownUrl(command.href) !== null
    case 'set-cell-background': return isValidCellColor(command.color)
    case 'set-row-height': return isValidRowHeight(command.height)
    case 'add-row': return ['before', 'after'].includes(command.side) && canGrowSelectedTable(editor, 'row')
    case 'add-column': return ['before', 'after'].includes(command.side) && canGrowSelectedTable(editor, 'column')
    case 'set-alignment': return ['left', 'center', 'right'].includes(command.alignment) && (profile === 'rich' || editor.isActive('table'))
    case 'paste-markdown': return typeof command.text === 'string' && command.text.length > 0 && command.text.length <= MAX_MARKDOWN_TEXT_LENGTH
    default: return true
  }
}

function apply(editor: Editor, chain: ChainedCommands, command: MarkdownCommand, plugins?: MarkdownPluginRegistry): boolean {
  switch (command.type) {
    case 'plugin-command': {
      const registered = plugins?.resolve(command)
      return !!registered && chain.command(registered.apply()).run()
    }
    case 'undo': return chain.undo().run()
    case 'redo': return chain.redo().run()
    case 'toggle-bold': return chain.toggleBold().run()
    case 'toggle-italic': return chain.toggleItalic().run()
    case 'toggle-underline': return chain.toggleUnderline().run()
    case 'toggle-strike': return chain.toggleStrike().run()
    case 'toggle-highlight': return chain.toggleHighlight().run()
    case 'toggle-code': return chain.toggleCode().run()
    case 'set-paragraph': return chain.setParagraph().run()
    case 'set-heading': return chain.setHeading({ level: command.level }).run()
    case 'toggle-blockquote': return chain.toggleBlockquote().run()
    case 'toggle-code-block': return chain.toggleCodeBlock(command.language ? { language: command.language } : undefined).run()
    case 'toggle-list':
      if (command.list === 'timed') return !!editor.schema.nodes.timedList && chain.toggleTimedList().run()
      // A timed list becomes the chosen list in place, its stamps dropped (toggleList cannot change the item type).
      return chain.command(({ tr, commands }) => retypeList(tr, ['timedList'], `${command.list}List`, command.list === 'task' ? 'taskItem' : 'listItem')
        || (command.list === 'bullet' ? commands.toggleBulletList() : command.list === 'ordered' ? commands.toggleOrderedList() : commands.toggleTaskList())).run()
    case 'set-link': return chain.extendMarkRange('link').setLink({ href: command.href }).run()
    // The link at the caret (that one link, not its neighbour), or only the selected part of one.
    case 'unset-link': return chain.unsetLink().run()
    case 'insert-horizontal-rule': return chain.setHorizontalRule().run()
    // Inside a callout the command changes its variant (the "turn into" target); elsewhere it inserts one.
    case 'insert-callout': return (editor.isActive('callout') && chain.setCalloutVariant(command.variant ?? 'note').run()) || chain.insertContent({ type: 'callout', attrs: { variant: command.variant ?? 'note' }, content: [{ type: 'paragraph' }] }).run()
    case 'set-alignment': return editor.isActive('table')
      ? chain.setCellTextAlign(command.alignment).run()
      : chain.setTextAlign(command.alignment).run()
    case 'insert-table': return chain.insertTable({ rows: command.rows, cols: command.columns, withHeaderRow: true }).run()
    case 'add-row': return command.side === 'before' ? chain.addRowBefore().run() : command.side === 'after' && chain.addRowAfter().run()
    case 'add-column': {
      if (command.side === 'before') chain.addColumnBefore()
      else if (command.side === 'after') chain.addColumnAfter()
      else return false
      return chain.command(({ tr, dispatch }) => { if (dispatch) resetSelectedTableColumnWidths(tr); return true }).run()
    }
    case 'sort-table': return chain.command(sortSelectedColumn(editor, command.direction)).run()
    case 'delete-row': return chain.deleteRow().run()
    case 'delete-column': return chain.deleteColumn().command(({ tr, dispatch }) => { if (dispatch) resetSelectedTableColumnWidths(tr); return true }).run()
    case 'delete-table': return chain.deleteTable().run()
    case 'merge-cells': return chain.mergeCells().run()
    case 'split-cell': return chain.splitCell().run()
    case 'toggle-header-row': return chain.toggleHeaderRow().run()
    case 'toggle-header-column': return chain.toggleHeaderColumn().run()
    case 'set-cell-background': return chain.setCellBackground(command.color).run()
    case 'clear-cell-content': return chain.command(clearCellContent).run()
    case 'paste-markdown': return chain.pasteMarkdown(command.text).run()
    case 'set-row-height': {
      const position = editor.state.selection.$from
      for (let depth = position.depth; depth > 0; depth--) {
        if (position.node(depth).type.name === 'tableRow') {
          return chain.setRowHeightAt(position.before(depth), command.height).run()
        }
      }
      return false
    }
    default: return false
  }
}

function active(editor: Editor, command: MarkdownCommand): boolean {
  switch (command.type) {
    case 'toggle-bold': return editor.isActive('bold')
    case 'toggle-italic': return editor.isActive('italic')
    case 'toggle-underline': return editor.isActive('underline')
    case 'toggle-strike': return editor.isActive('strike')
    case 'toggle-highlight': return editor.isActive('highlight')
    case 'toggle-code': return editor.isActive('code')
    case 'set-heading': return editor.isActive('heading', { level: command.level })
    case 'set-paragraph': return editor.isActive('paragraph')
    case 'toggle-blockquote': return editor.isActive('blockquote')
    case 'toggle-code-block': return editor.isActive('codeBlock')
    case 'toggle-list': return editor.isActive({ bullet: 'bulletList', ordered: 'orderedList', task: 'taskList', timed: 'timedList' }[command.list])
    case 'set-link': return editor.isActive('link')
    case 'set-alignment': return editor.isActive({ textAlign: command.alignment })
    case 'set-cell-background': return selectedCellsHaveAttribute(editor.state, 'backgroundColor', command.color)
    default: return false
  }
}

function validRange(editor: Editor, options?: MarkdownDispatchOptions): boolean {
  const range = options?.replaceRange
  return !range || (Number.isInteger(range.from) && Number.isInteger(range.to) && range.from >= 0
    && range.to >= range.from && range.to <= editor.state.doc.content.size)
}
function prepare(chain: ChainedCommands, options?: MarkdownDispatchOptions): ChainedCommands {
  const range = options?.replaceRange
  // Mutate only the chain's private transaction, including during can(). The dry-run
  // command must see the same document as execution after removing the slash query.
  return range ? chain.command(({ tr }) => { tr.delete(range.from, range.to); return true }) : chain
}
export function queryCommand(editor: Editor, profile: MarkdownProfile, command: MarkdownCommand, plugins?: MarkdownPluginRegistry, options?: MarkdownDispatchOptions): CommandState {
  return {
    enabled: editor.isEditable && validRange(editor, options) && permitted(editor, profile, command)
      && apply(editor, prepare(editor.can().chain(), options), command, plugins),
    active: command.type === 'plugin-command' ? plugins?.resolve(command)?.active(editor) ?? false : active(editor, command),
  }
}

export function dispatchCommand(editor: Editor, profile: MarkdownProfile, command: MarkdownCommand, plugins?: MarkdownPluginRegistry, options?: MarkdownDispatchOptions): boolean {
  if (!queryCommand(editor, profile, command, plugins, options).enabled) return false
  const discrete = command.type === 'set-link' || command.type === 'unset-link'
  const chain = prepare(editor.chain(), options)
  if (discrete) chain.command(({ tr }) => { closeMarkdownHistory(tr); return true })
  const applied = apply(editor, chain, command, plugins)
  if (applied && discrete && !editor.isDestroyed) {
    editor.view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
  }
  return applied
}
