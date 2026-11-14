import type { Node as PMNode } from '@tiptap/pm/model'

/**
 * The eager side of the Markdown dialect (`a-` components, ./markdownDialect): parsing and serializing are synchronous,
 * but only Markdown with `a-` components needs the dialect, so a page that only renders or edits never downloads it.
 * Editor controllers start `loadMarkdownDialect()`; until it resolves, a parse or serialize that meets a component
 * throws instead of silently keeping it as code or text.
 */
export type MarkdownDialect = typeof import('./markdownDialect')

/** Identity written as `{#id}` (a table a chart links to). */
export const MARKDOWN_DIALECT_ID = /^[A-Za-z0-9._-]{1,100}$/
/** Markdown the dialect reads differently: an ```a-… fence or an `[…](a-…)` link. */
export const MARKDOWN_DIALECT_SOURCE = /(?:`{3,}|~{3,})[ \t]*a-|\]\(a-/

let dialect: MarkdownDialect | undefined
let ready = false
const forms = new Set<() => Promise<void>>()

/** Loads the dialect and the forms of every imported plugin; resolves to the dialect's helpers. */
export async function loadMarkdownDialect(): Promise<MarkdownDialect> {
  const loading = [...forms]
  const [module] = await Promise.all([import('./markdownDialect'), ...loading.map(load => load())])
  dialect = module
  if (loading.length === forms.size) ready = true
  return module
}

/** Whether the dialect and every registered form have loaded. */
export const markdownDialectReady = (): boolean => ready

/** The loaded dialect; throws while `loadMarkdownDialect()` has not resolved. */
export function markdownDialect(): MarkdownDialect {
  if (!ready) throw new Error('The Markdown dialect is not loaded yet: await loadMarkdownDialect() before reading or writing a- components.')
  return dialect!
}
export const loadedMarkdownDialect = (): MarkdownDialect | undefined => dialect

/**
 * For a plugin's Markdown reader: a construct it refuses and leaves as text (1-based line and column in the Markdown being
 * read). The page source shows it as an error on that line; other readers (paste, import) keep the text and go on.
 */
export function reportMarkdownSyntaxError(issue: import('./markdownDialect').MarkdownDialectIssue): void {
  dialect?.reportMarkdownDialectIssue('error', issue)
}

/** A plugin's lazy dialect form, registered once per plugin module; the accessor throws until the dialect is loaded. */
export function registerMarkdownDialectForm<F>(load: () => Promise<F>): () => F {
  let form: F
  forms.add(async () => { form = await load() })
  ready = false
  return () => (markdownDialect(), form)
}

// ---------------------------------------------------------------------------
// Document being serialized (charts and their linked tables need the whole document)
// ---------------------------------------------------------------------------

let serializing: PMNode | null = null
export function withMarkdownSerialization<T>(document: PMNode, run: () => T): T {
  const previous = serializing
  serializing = document
  try { return run() } finally { serializing = previous }
}
/** The document whose Markdown is being written; null outside serialization. */
export const serializedMarkdownDocument = (): PMNode | null => serializing
