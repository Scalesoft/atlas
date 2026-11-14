import type { Command, Editor } from '@tiptap/core'

declare const commandDefinitionBrand: unique symbol
declare const commandCallBrand: unique symbol

export interface MarkdownPluginCommandDefinition {
  readonly id: string
  readonly [commandDefinitionBrand]: true
}
export interface MarkdownPluginCommand<Args = void> extends MarkdownPluginCommandDefinition {
  (args: Args): MarkdownPluginCommandCall
}
export interface MarkdownPluginCommandCall {
  readonly type: 'plugin-command'
  readonly id: string
  readonly [commandCallBrand]: true
}
export interface MarkdownPluginCommandOptions<Args> {
  readonly id: string
  readonly apply: (args: Args) => Command
  readonly active?: (editor: Editor, args: Args) => boolean
}

export type MarkdownCommand =
  | MarkdownPluginCommandCall
  | { readonly type: 'undo' | 'redo' }
  | { readonly type: 'toggle-bold' | 'toggle-italic' | 'toggle-underline' | 'toggle-strike' | 'toggle-highlight' | 'toggle-code' }
  | { readonly type: 'set-paragraph' | 'toggle-blockquote' }
  /** `language` (letters, digits, `+#.-`, at most 32) names the highlighting of a new code block. */
  | { readonly type: 'toggle-code-block'; readonly language?: string }
  | { readonly type: 'set-heading'; readonly level: 1 | 2 | 3 | 4 | 5 | 6 }
  /** `timed`: the timed list (rich profile), its items stamped with now. */
  | { readonly type: 'toggle-list'; readonly list: 'bullet' | 'ordered' | 'task' | 'timed' }
  | { readonly type: 'set-link'; readonly href: string }
  | { readonly type: 'unset-link' }
  | { readonly type: 'insert-horizontal-rule' }
  | { readonly type: 'insert-callout'; readonly variant?: 'note' | 'tip' | 'warning' | 'important' | 'caution' }
  | { readonly type: 'set-alignment'; readonly alignment: 'left' | 'center' | 'right' }
  | { readonly type: 'insert-table'; readonly rows: number; readonly columns: number }
  | { readonly type: 'add-row' | 'add-column'; readonly side: 'before' | 'after' }
  | { readonly type: 'delete-row' | 'delete-column' | 'delete-table' | 'merge-cells' | 'split-cell' | 'toggle-header-row' | 'toggle-header-column' }
  | { readonly type: 'set-cell-background'; readonly color: string | null }
  | { readonly type: 'clear-cell-content' }
  | { readonly type: 'sort-table'; readonly direction: 'ascending' | 'descending' }
  | { readonly type: 'set-row-height'; readonly height: number | null }
  /** Insert Markdown text (for example read from the clipboard) through the paste import boundary. */
  | { readonly type: 'paste-markdown'; readonly text: string }

export interface CommandState { readonly enabled: boolean; readonly active: boolean }
export interface MarkdownDispatchOptions {
  readonly replaceRange?: { readonly from: number; readonly to: number }
}
