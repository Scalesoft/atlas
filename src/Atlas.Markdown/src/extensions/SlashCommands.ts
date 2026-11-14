import { Extension, type Editor, type Range } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { whenMarkdownIdle } from '../features/modules/lazy'
import Suggestion, { findSuggestionMatch, type SuggestionOptions } from '@tiptap/suggestion'
import type { MarkdownCommand, MarkdownEditorController } from '../core'
import { createMarkdownSuggestionRenderer, closeMarkdownSuggestion, foldSearchText, pickNamedChoice, rankSuggestion, rankSuggestions, shouldShowMarkdownSuggestion } from '../features/suggestions'
import { blocksStrings } from '../ui/strings/blocks'
import { pasteMarkdownFromClipboard, pasteMarkdownShortcutLabel } from '../engine/markdownInput'
import { markdownStrings } from '../ui/strings'
import { slashStrings } from '../ui/strings/slash'
import { HEADING_LEVELS } from '../core/documentConstraints'
import { formatMarkdownShortcut } from '../core/keyboardShortcuts'

// The keys of the editor's own items (tiptap's defaults, core/keyboardShortcuts.ts), shown in their rows.
const itemKeys: Readonly<Record<string, string>> = {
  ...Object.fromEntries(HEADING_LEVELS.map(level => [`heading-${level}`, `Mod Shift ${level}`])),
  'bullet-list': 'Mod Shift 8', 'ordered-list': 'Mod Shift 7', 'task-list': 'Mod Shift 9', 'timed-list': 'Mod Shift D', blockquote: 'Mod Shift B', 'code-block': 'Mod Alt C',
}

const SlashCommandsKey = new PluginKey<{ active: boolean; range: Range }>('slashCommands')
export interface SlashCommandItem {
  id?: string
  title: string
  description: string
  searchTerms: string[]
  icon: string
  variants?: readonly ('document' | 'compact')[]
  commandValue?: MarkdownCommand
  /**
   * The item continues after a space: `/<title or search term> text` offers one row, "{label}: vyhledat „text“" (or
   * `title(text)`), that dispatches `command(text)` in place of `commandValue`; no row when `command` returns nothing.
   * Needs the controller (`commandValue` items).
   */
  parameter?: { label: string; command(text: string): MarkdownCommand | undefined; title?(text: string): string }
  available?: () => boolean
  command: (props: { editor: Editor; range: Range }) => void
}

const tableSize = (text: string) => {
  const match = /^(\d{1,2})\s*[x×*]\s*(\d{1,2})$/u.exec(text.trim())
  return match ? { columns: Number(match[1]), rows: Number(match[2]) } : undefined
}

const calloutVariants = ['note', 'tip', 'warning', 'important', 'caution'] as const
type CalloutVariant = (typeof calloutVariants)[number]
const calloutLabel = (variant: CalloutVariant) => blocksStrings[`callout${variant[0]!.toUpperCase()}${variant.slice(1)}` as `callout${Capitalize<CalloutVariant>}`]
const calloutVariant = (text: string) => pickNamedChoice(text, calloutVariants.map(variant => [variant, calloutLabel(variant), variant] as const))

// `icon` names an entry in the one icon registry (`ui/iconMarkup.ts`), prefixed `svg:`.
// No two items may share a name: in a filtered menu the drawing is what tells them apart.
function headingItem(level: (typeof HEADING_LEVELS)[number], searchTerms: string[]): SlashCommandItem {
  return {
    id: `heading-${level}`, commandValue: { type: 'set-heading', level },
    get title() { return slashStrings[`heading${level}Title`] },
    get description() { return slashStrings[`heading${level}Description`] },
    searchTerms, icon: `svg:heading-${level}`,
    command: ({ editor, range }) => editor.chain().focus().deleteRange(range).setNode('heading', { level }).run(),
  }
}

export const slashItems: SlashCommandItem[] = [
  {
    id: 'callout', variants: ['document'], commandValue: { type: 'insert-callout' }, get title() { return slashStrings.calloutTitle },
    get description() { return slashStrings.calloutDescription }, searchTerms: ['callout', 'box', 'tip', 'poznamka', 'upozorneni'], icon: 'svg:callout',
    // `/box tip`, `/box upozornění`: that kind of box.
    parameter: { get label() { return slashStrings.calloutTitle }, command: text => { const variant = calloutVariant(text); return variant ? { type: 'insert-callout', variant } : undefined },
      title: text => `${slashStrings.calloutTitle}: ${calloutLabel(calloutVariant(text)!)}` },
    command: ({ editor, range }) => { if (editor.schema.nodes.callout) editor.chain().deleteRange(range).insertContent({ type: 'callout', content: [{ type: 'paragraph' }] }).run() },
  },
  headingItem(1, ['h1', 'nadpis', 'title', 'heading']),
  headingItem(2, ['h2', 'subtitle']),
  ...HEADING_LEVELS.slice(2).map(level => headingItem(level, [`h${level}`])),
  {
    id: 'bullet-list', commandValue: { type: 'toggle-list', list: 'bullet' },
    get title() { return slashStrings.bulletListTitle },
    get description() { return slashStrings.bulletListDescription },
    searchTerms: ['bullet', 'list', 'seznam', 'odrazky'],
    icon: 'svg:list',
    command: ({ editor, range }) => editor.chain().focus().deleteRange(range).toggleBulletList().run(),
  },
  {
    id: 'ordered-list', commandValue: { type: 'toggle-list', list: 'ordered' },
    get title() { return slashStrings.orderedListTitle },
    get description() { return slashStrings.orderedListDescription },
    searchTerms: ['ordered', 'numbered', '1.'],
    icon: 'svg:list-ordered',
    command: ({ editor, range }) => editor.chain().focus().deleteRange(range).toggleOrderedList().run(),
  },
  {
    id: 'task-list', commandValue: { type: 'toggle-list', list: 'task' },
    get title() { return slashStrings.taskListTitle },
    get description() { return slashStrings.taskListDescription },
    searchTerms: ['task', 'todo', 'check', 'checkbox', 'ukoly'],
    icon: 'svg:list-checks',
    command: ({ editor, range }) => editor.chain().focus().deleteRange(range).toggleTaskList().run(),
  },
  {
    // The only way in (no input rule): a list of dated notes, each item stamped with now (features/timed/timedList.ts).
    id: 'timed-list', commandValue: { type: 'toggle-list', list: 'timed' },
    get title() { return slashStrings.timedListTitle },
    get description() { return slashStrings.timedListDescription },
    searchTerms: ['cas', 'casova', 'poznamka', 'datum', 'timed', 'time', 'timestamp', 'date', 'log', 'historie'],
    icon: 'svg:clock',
    command: ({ editor, range }) => { if (editor.schema.nodes.timedList) editor.chain().focus().deleteRange(range).toggleTimedList().run() },
  },
  {
    id: 'blockquote', commandValue: { type: 'toggle-blockquote' },
    get title() { return slashStrings.blockquoteTitle },
    get description() { return slashStrings.blockquoteDescription },
    searchTerms: ['quote', 'citace', 'blockquote'],
    icon: 'svg:quote',
    command: ({ editor, range }) => editor.chain().focus().deleteRange(range).toggleBlockquote().run(),
  },
  {
    id: 'code-block', variants: ['document'], commandValue: { type: 'toggle-code-block' },
    get title() { return slashStrings.codeBlockTitle },
    get description() { return slashStrings.codeBlockDescription },
    searchTerms: ['code', 'kod', 'snippet'],
    icon: 'svg:square-code',
    // `/kód ts`: a code block in that language.
    parameter: { get label() { return slashStrings.codeBlockTitle }, command: text => /^[\w+#.-]{1,32}$/u.test(text) ? { type: 'toggle-code-block', language: text.toLowerCase() } : undefined,
      title: text => slashStrings.codeLanguageTitle(text.toLowerCase()) },
    command: ({ editor, range }) => editor.chain().focus().deleteRange(range).toggleCodeBlock().run(),
  },
  {
    id: 'horizontal-rule', variants: ['document'], commandValue: { type: 'insert-horizontal-rule' },
    get title() { return slashStrings.horizontalRuleTitle },
    get description() { return slashStrings.horizontalRuleDescription },
    searchTerms: ['hr', 'divider', 'ruler', 'cara'],
    icon: 'svg:minus',
    command: ({ editor, range }) => editor.chain().focus().deleteRange(range).setHorizontalRule().run(),
  },
  {
    id: 'table', variants: ['document'], commandValue: { type: 'insert-table', rows: 3, columns: 3 },
    get title() { return slashStrings.tableTitle },
    get description() { return slashStrings.tableDescription },
    searchTerms: ['table', 'tabulka', 'grid'],
    icon: 'svg:table',
    // `/tabulka 3x4`: columns × rows, as a table is drawn (the width first).
    parameter: { get label() { return slashStrings.tableTitle }, command: text => {
      const size = tableSize(text)
      return size ? { type: 'insert-table', rows: size.rows, columns: size.columns } : undefined
    }, title: text => { const size = tableSize(text)!; return slashStrings.tableSizeTitle(size.columns, size.rows) } },
    command: ({ editor, range }) =>
      editor.chain().focus().deleteRange(range).insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run(),
  },
  {
    // Ctrl+Shift+Alt+V (⌥⇧⌘V) does the same from the keyboard; Ctrl+Shift+V stays the literal plain-text paste.
    id: 'paste-markdown', get title() { return markdownStrings.pasteAsMarkdown },
    get description() { return `${markdownStrings.pasteAsMarkdownDescription} (${pasteMarkdownShortcutLabel()})` },
    searchTerms: ['markdown', 'md', 'paste', 'vlozit', 'schranka', 'clipboard', 'wiki'],
    icon: 'svg:clipboard-paste',
    available: () => typeof navigator !== 'undefined' && typeof navigator.clipboard?.readText === 'function',
    command: ({ editor, range }) => {
      editor.chain().focus().deleteRange(range).run()
      void pasteMarkdownFromClipboard(editor)
    },
  },
]

/**
 * YouTrack mention slash item — projector-specific. Built separately so the
 * core editor stays free of YT dependencies. Consumers add it via the
 * `extras` parameter.
 */
export interface SlashSuggestionExtras {
  controller?: Pick<MarkdownEditorController, 'dispatch' | 'query'>
  /** Optional image-upload command — added if uploadUrl is configured. */
  withImage?: () => SlashCommandItem | null
  /** Optional project-specific extras (e.g. YouTrack issue chip). */
  extra?: SlashCommandItem[]
  /**
   * When the editor runs in compact mode (BlockerDialog 80/140 px,
   * CatalogPage 80/120 px), filter out block-heavy items that don't fit
   * the surrounding form (table, code block, hr, image). Round-4 audit
   * caught the missing filter — `/Tabulka` in an 80 px editor wrecks
   * the dialog layout.
   */
  compact?: boolean | (() => boolean)
}

const WORD_SEPARATOR = /\s+/u

/**
 * The parameter item a query continues, if any: the query starts with the folded title or a folded search term of an
 * item that has a `parameter`, followed by whitespace. The longest name wins; `text` is what follows, trimmed.
 */
function parameterTarget(query: string, items: readonly SlashCommandItem[]): { item: SlashCommandItem; text: string } | undefined {
  const words = query.trimStart().split(WORD_SEPARATOR)
  const typed = foldSearchText(words.join(' '))
  let best: { item: SlashCommandItem; length: number } | undefined
  for (const item of items) {
    if (!item.parameter || !item.commandValue) continue
    // The words still spell the item's own name („/úkol z youtracku“ by its keyword „úkol“): no parameter text yet.
    if ([item.title, ...item.searchTerms].some(name => foldSearchText(name).trim().split(WORD_SEPARATOR).join(' ').startsWith(typed))) continue
    for (const name of [item.title, ...item.searchTerms]) {
      const folded = foldSearchText(name).trim().split(WORD_SEPARATOR)
      // The query needs at least one more word than the name: the parameter text.
      if (folded.length >= words.length || (best && folded.length <= best.length)) continue
      if (foldSearchText(words.slice(0, folded.length).join(' ')) === folded.join(' ')) best = { item, length: folded.length }
    }
  }
  if (!best) return undefined
  // Drop the words of the matched name; what remains is the parameter text.
  return { item: best.item, text: words.slice(best.length).join(' ').trim() }
}

// How often each item was picked (by id), for ranking. Kept per browser; a page without storage keeps it per session.
const usageKey = 'atlas-markdown-slash-usage'
let usage: Map<string, number> | undefined
function slashUsage(): Map<string, number> {
  if (usage) return usage
  usage = new Map()
  try { for (const [id, count] of Object.entries(JSON.parse(globalThis.localStorage?.getItem(usageKey) ?? '{}') as Record<string, unknown>)) if (typeof count === 'number') usage.set(id, count) }
  catch { /* No storage or a damaged entry: start empty. */ }
  return usage
}
function recordSlashUse(id: string): void {
  const counts = slashUsage()
  counts.set(id, (counts.get(id) ?? 0) + 1)
  try { globalThis.localStorage?.setItem(usageKey, JSON.stringify(Object.fromEntries(counts))) } catch { /* In-memory only. */ }
}

export const slashSuggestion = (extras?: SlashSuggestionExtras): Omit<SuggestionOptions, 'editor'> => {
  const compactNow = () => !!(typeof extras?.compact === 'function' ? extras.compact() : extras?.compact)
  const known = () => {
    const items = [...slashItems]
    if (extras?.extra?.length) items.push(...extras.extra)
    const img = extras?.withImage?.()
    if (img) items.push({ ...img, variants: img.variants ?? ['document'] })
    return items
  }
  const visible = () => known().filter(item => (!item.variants || item.variants.includes(compactNow() ? 'compact' : 'document')) && (item.available?.() ?? true))
  const parameterItems = () => extras?.controller ? visible().filter(item => item.parameter && item.commandValue) : []
  const read = (item: SlashCommandItem) => ({ title: item.title, keywords: item.searchTerms })
  // Which rows can run here is asked once per open menu, not at every keystroke (a dry run per row, ~4 ms; J12.5): each check removes
  // the typed query first, so the answer stays while the query is typed.
  // shortcut: the place is keyed by the menu's start, the document size without the query and the parent block; a same-size remote edit elsewhere keeps the old answer until the menu reopens.
  const enabledHere = new WeakMap<Editor, { key: string; answers: Map<string, boolean> }>()
  const enabled = (editor: Editor, item: SlashCommandItem, options?: { replaceRange: Range }) => {
    const range = options?.replaceRange
    const key = range ? `${range.from}|${editor.state.doc.content.size - range.to + range.from}|${editor.state.doc.resolve(range.from).parent.type.name}|${editor.isEditable}` : ''
    let cache = enabledHere.get(editor)
    if (!range || cache?.key !== key) enabledHere.set(editor, cache = { key, answers: new Map() })
    const id = item.id ?? item.title
    let answer = cache.answers.get(id)
    if (answer === undefined) cache.answers.set(id, answer = extras!.controller!.query(item.commandValue!, options).enabled)
    return answer
  }
  const items = ({ query, editor }: { query: string; editor: Editor }): SlashCommandItem[] => {
    const suggestion = SlashCommandsKey.getState(editor.state)
    const options = suggestion?.active ? { replaceRange: suggestion.range } : undefined
    const filtered = visible().filter(item => !item.commandValue || !extras?.controller || enabled(editor, item, options))
    const target = parameterTarget(query, filtered)
    const withText: SlashCommandItem[] = []
    const commandValue = target?.text ? target.item.parameter!.command(target.text) : undefined
    if (target && commandValue && extras!.controller!.query(commandValue, options).enabled) withText.push({
      ...target.item, id: `${target.item.id ?? target.item.title}:parameter`,
      title: target.item.parameter!.title?.(target.text) ?? slashStrings.parameterTitle(target.item.parameter!.label, target.text),
      searchTerms: [], parameter: undefined, commandValue,
    })
    return [...withText, ...rankSuggestions(query, filtered, read, { usage: item => slashUsage().get(item.id ?? item.title) ?? 0, abbreviations: true })]
      .map(item => {
        const id = (item.id ?? item.title).replace(/:parameter$/u, '')
        const run = item.commandValue && extras?.controller
          ? ({ editor, range }: { editor: Editor; range: Range }) => { if (extras.controller!.dispatch(item.commandValue!, { replaceRange: range })) editor.view.focus() }
          : item.command
        return { ...item, command: (props: { editor: Editor; range: Range }) => { recordSlashUse(id); run(props) } }
      })
  }
  return {
  char: '/',
  allowSpaces: false,
  shouldShow: shouldShowMarkdownSuggestion,
  // A space ends the query, unless it follows the name of an item that takes a parameter or the words still name an item
  // („/nadpis 2“, „/kontakt v textu“).
  findSuggestionMatch: config => {
    const match = findSuggestionMatch({ ...config, allowSpaces: true })
    if (match && /\s/u.test(match.query)) {
      const spaced = parameterItems()
      if ((spaced.length && parameterTarget(match.query, spaced)) || visible().some(item => (rankSuggestion(match.query, read(item)) ?? 4) < 4)) return match
    }
    return findSuggestionMatch(config)
  },

  items,

  render: createMarkdownSuggestionRenderer<SlashCommandItem>({
    pluginKey: SlashCommandsKey,
    label: slashStrings.menuLabel,
    heading: slashStrings.menuHeading,
    testId: 'slash-menu',
    mapItem: item => ({ id: item.id ?? item.title, title: item.title, description: item.description, icon: item.icon, disabled: !(item.available?.() ?? true),
      ...item.id && itemKeys[item.id] ? { shortcut: formatMarkdownShortcut(itemKeys[item.id]!.split(' ')) } : {} }),
    itemsNow: (query, editor) => items({ query, editor }),
    command: (item, { editor, range }) => item.command({ editor, range }),
  }),
  }
}

/** Dismiss a mounted menu when its runtime surface configuration changes. */
export function closeSlashSuggestion(editor: Editor): void {
  closeMarkdownSuggestion(editor, SlashCommandsKey)
}

export const SlashCommands = Extension.create<{ suggestion: Omit<SuggestionOptions, 'editor'> }>({
  name: 'slashCommands',

  addOptions() {
    return { suggestion: slashSuggestion() }
  },

  addProseMirrorPlugins() {
    const editor = this.editor, items = this.options.suggestion.items
    return [
      Suggestion({
        editor: this.editor,
        pluginKey: SlashCommandsKey,
        ...this.options.suggestion,
        command: ({ editor, range, props }) => {
          (props as SlashCommandItem).command({ editor, range })
        },
      }),
      // The first menu after load asked its rows cold (~11 ms of dry runs, J12.3): ask them once while the page is idle after the editor
      // first takes the focus (no slash comes before it), so a page only read runs no dry run (one fetches: the requirement catalog's).
      new Plugin({ view: () => {
        const warm = () => {
          editor.off('focus', warm)
          whenMarkdownIdle(() => { if (!editor.isDestroyed && editor.isEditable) void Promise.resolve(items?.({ query: '', editor, signal: new AbortController().signal })).catch(() => undefined) })
        }
        editor.on('focus', warm)
        return { destroy: () => { editor.off('focus', warm) } }
      } }),
    ]
  },
})
