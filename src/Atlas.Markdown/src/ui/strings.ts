import type { AttachmentsStrings } from './strings/attachments'
import type { BlocksStrings } from './strings/blocks'
import type { ChartsStrings } from './strings/charts'
import type { CollaborationStrings } from './strings/collaboration'
import type { ConcealedStrings } from './strings/concealed'
import type { ContactsStrings } from './strings/contacts'
import type { DiagramsStrings } from './strings/diagrams'
import type { DragStrings } from './strings/drag'
import type { EditorToolsStrings } from './strings/editorTools'
import type { EmojiStrings } from './strings/emoji'
import type { HeadingAnchorsStrings } from './strings/headingAnchors'
import type { HistoryStrings } from './strings/history'
import type { ImagesStrings } from './strings/images'
import type { InputStrings } from './strings/input'
import type { MathStrings } from './strings/math'
import type { ModulesStrings } from './strings/modules'
import type { OpenApiStrings } from './strings/openapi'
import type { OutlineStrings } from './strings/outline'
import type { PagesStrings } from './strings/pages'
import type { PanelsStrings } from './strings/panels'
import type { ReferencesStrings } from './strings/references'
import type { ReviewStrings } from './strings/review'
import type { SecretsStrings } from './strings/secrets'
import type { SlashStrings } from './strings/slash'
import type { SourceStrings } from './strings/source'
import type { SuggestionsStrings } from './strings/suggestions'
import type { IntentionsStrings } from './strings/intentions'
import type { SurfaceStrings } from './strings/surface'
import type { TableStrings } from './strings/table'
import type { TocStrings } from './strings/toc'
import type { ToolbarStrings } from './strings/toolbar'
import type { YouTrackStrings } from './strings/youtrack'

/**
 * One dictionary for every user-visible word the editor and its first-party plugins render (D5).
 * The handful of words that repeat across every popover, dialog and picker sit at the top level;
 * everything else is grouped in one flat section per area (`strings.table.*`, `strings.charts.*`).
 * A host embedding the editor in a different language calls `setMarkdownStrings` once at startup,
 * before it creates editors or plugins. Defaults are Czech. A value that needs a number or a name
 * is a small function returning the whole sentence. Nothing here is a translation *framework* —
 * no locale loading, plural rules or message syntax; that is an explicit non-goal until a second
 * language is actually needed (see the plan's Non-Goals).
 *
 * Only the top-level words live here. Each section's defaults live next to the code that renders
 * them (`ui/strings/<section>.ts` for core, `plugins/<name>/strings.ts` and `runtimeStrings.ts`
 * for plugins) and join this object through `registerMarkdownStrings` when that module is loaded,
 * so a page carries only the text of what it loads and lazy runtimes bring their own. Overrides
 * win regardless of order: `setMarkdownStrings` before a section loads keeps its keys, and
 * registration only fills the keys that are still missing.
 */
export interface MarkdownStrings {
  /** BCP 47 tag of the language of these words; collation, number and date formatting the editor shows follow it (D5). */
  locale: string
  apply: string
  cancel: string
  close: string
  /** P12: the word for deleting an element of the document (a block). */
  delete: string
  remove: string
  loading: string
  retry: string
  fullscreen: string
  exitFullscreen: string
  readOnly: string
  insert: string
  /** A dropped Markdown file contains structure the document cannot hold. */
  markdownImportFailed: string
  /** Slash menu item and command: insert the clipboard text as formatted Markdown. */
  pasteAsMarkdown: string
  /** Slash menu description of "paste as Markdown"; the platform's shortcut is appended in parentheses. */
  pasteAsMarkdownDescription: string
  /** The browser refused or does not offer clipboard text for "paste as Markdown". */
  clipboardReadFailed: string

  // Core editor areas.
  slash: SlashStrings
  blocks: BlocksStrings
  images: ImagesStrings
  drag: DragStrings
  suggestions: SuggestionsStrings
  intentions: IntentionsStrings
  table: TableStrings
  toolbar: ToolbarStrings
  surface: SurfaceStrings
  outline: OutlineStrings
  modules: ModulesStrings
  input: InputStrings
  source: SourceStrings
  collaboration: CollaborationStrings
  review: ReviewStrings
  history: HistoryStrings
  references: ReferencesStrings
  panels: PanelsStrings

  // First-party plugins (`@atlas/markdown/plugins/*`).
  attachments: AttachmentsStrings
  charts: ChartsStrings
  concealed: ConcealedStrings
  contacts: ContactsStrings
  diagrams: DiagramsStrings
  editorTools: EditorToolsStrings
  emoji: EmojiStrings
  headingAnchors: HeadingAnchorsStrings
  math: MathStrings
  openapi: OpenApiStrings
  pages: PagesStrings
  secrets: SecretsStrings
  toc: TocStrings
  youtrack: YouTrackStrings
}

/** What `setMarkdownStrings` accepts: any top-level word, and any subset of any section. */
export type MarkdownStringsOverrides = {
  [K in keyof MarkdownStrings]?: MarkdownStrings[K] extends string ? string : Partial<MarkdownStrings[K]>
}

/** A key of `MarkdownStrings` that holds a section rather than a single word. */
export type MarkdownStringsSection = { [K in keyof MarkdownStrings]: MarkdownStrings[K] extends string ? never : K }[keyof MarkdownStrings]

type TopLevelWords = { [K in keyof MarkdownStrings as MarkdownStrings[K] extends string ? K : never]: string }
const words: TopLevelWords = {
  locale: 'cs-CZ',
  apply: 'Použít',
  cancel: 'Zrušit',
  close: 'Zavřít',
  delete: 'Smazat',
  remove: 'Odebrat',
  loading: 'Načítání…',
  retry: 'Zkusit znovu',
  fullscreen: 'Celá obrazovka',
  exitFullscreen: 'Ukončit celou obrazovku',
  readOnly: 'Pouze pro čtení',
  insert: 'Vložit',
  markdownImportFailed: 'Markdown obsahuje strukturu, kterou dokument nepodporuje, a nelze ho vložit.',
  pasteAsMarkdown: 'Vložit jako Markdown',
  pasteAsMarkdownDescription: 'Text ze schránky jako formátovaný obsah',
  clipboardReadFailed: 'Schránku se nepodařilo přečíst. Vložte text pomocí Ctrl+V.',
}

/**
 * The live dictionary. It starts with the top-level words; a section appears once the module that
 * registers it has loaded (or once a host overrides part of it). Section objects are never replaced,
 * so code may keep a reference to one.
 */
export const markdownStrings: MarkdownStrings = { ...words } as MarkdownStrings

/**
 * Adds a section's defaults to the dictionary and returns the live section object. Only keys that
 * are still missing are set, so a host's earlier `setMarkdownStrings` stays in force. A section may
 * be registered in parts (entry text eagerly, runtime text from its lazy chunk).
 */
export function registerMarkdownStrings<K extends MarkdownStringsSection>(section: K, defaults: Partial<MarkdownStrings[K]>): MarkdownStrings[K] {
  const root = markdownStrings as unknown as Record<string, Record<string, unknown>>
  const target = root[section] ??= {}
  for (const [key, value] of Object.entries(defaults)) if (!Object.prototype.hasOwnProperty.call(target, key)) target[key] = value
  return target as unknown as MarkdownStrings[K]
}

/**
 * Overrides one or more strings in place; every consumer reads the same shared object. Sections
 * merge key by key, so `setMarkdownStrings({ table: { deleteRowLabel: 'Delete row' } })` keeps the
 * rest of the table section, whether or not the table code has loaded yet.
 */
export function setMarkdownStrings(overrides: MarkdownStringsOverrides): void {
  const target = markdownStrings as unknown as Record<string, unknown>
  for (const [key, value] of Object.entries(overrides)) {
    if (value === undefined) continue
    const current = target[key]
    if (current && typeof current === 'object' && typeof value === 'object') Object.assign(current, value)
    else target[key] = typeof value === 'object' ? { ...value } : value
  }
}
