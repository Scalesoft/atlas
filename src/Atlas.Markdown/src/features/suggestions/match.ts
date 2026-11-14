import { foldMarkdownText } from '../../core/recognition/text'

/** Lower-case, accent-insensitive form used to compare typed search text: "Zmínka" and "zminka" fold the same (the engine's one fold). */
export const foldSearchText = foldMarkdownText

/**
 * The value of the first choice with a name that starts with the typed text, ignoring case and diacritics („kolac“ →
 * Koláčový), or that the text starts with up to five letters („sloupce“ → Sloupcový). For slash arguments (`/graf koláč`).
 */
export function pickNamedChoice<T>(text: string, choices: readonly (readonly [T, ...string[]])[]): T | undefined {
  const typed = foldSearchText(text).trim()
  if (!typed) return undefined
  return choices.find(([, ...names]) => names.some(name => { const folded = foldSearchText(name); return folded.startsWith(typed) || typed.startsWith(folded.slice(0, 5)) }))?.[0]
}

export interface SuggestionRankInput {
  readonly title: string
  readonly keywords?: readonly string[]
}

/**
 * How well `query` matches a suggestion; lower is better, `undefined` is no match. Comparison ignores case and
 * diacritics. Tiers: 0 the title starts with the query (an empty query is tier 0), 1 a word of the title starts
 * with it, 2 a keyword starts with it, 3 the query is inside the title or a keyword, 4 the letters of the query appear in the
 * title in order (an abbreviation: „tbl“ finds „Tabulka“; at least two letters; only with `abbreviations`).
 */
export function rankSuggestion(query: string, item: SuggestionRankInput, abbreviations = false): number | undefined {
  const needle = foldSearchText(query).trim()
  if (!needle) return 0
  const title = foldSearchText(item.title)
  if (title.startsWith(needle)) return 0
  const words = title.split(/[\s\-–—_/.,:;()]+/u)
  if (words.some(word => word.startsWith(needle)) || wordStarts(needle.split(/\s+/u), words)) return 1
  const keywords = (item.keywords ?? []).map(foldSearchText)
  if (keywords.some(keyword => keyword.startsWith(needle) || keyword.split(/[\s\-–—_/.,:;()]+/u).some(word => word.startsWith(needle)))) return 2
  if (title.includes(needle) || keywords.some(keyword => keyword.includes(needle))) return 3
  if (abbreviations && needle.length >= 2 && inOrder(needle.replace(/\s+/gu, ''), title)) return 4
  return undefined
}
/** Several typed words, each the start of a later word of the title than the one before. */
function wordStarts(typed: readonly string[], words: readonly string[]): boolean {
  if (typed.length < 2) return false
  let at = 0
  for (const part of typed) {
    while (at < words.length && !words[at]!.startsWith(part)) at++
    if (at++ >= words.length) return false
  }
  return true
}
const inOrder = (letters: string, text: string) => { let at = 0; for (const letter of text) if (letter === letters[at]) at++; return at === letters.length }

export interface SuggestionRankOptions<T> {
  /** How often the item was picked; inside one tier the more used comes first (recent and frequent commands). */
  usage?: (item: T) => number
  /** Also tier 4, the letters of the query in order („tbl“): a command menu, not a search of names. */
  abbreviations?: boolean
}

/**
 * The matching items: a whole title or keyword equal to the query first („Graf“ before „Grafy“), then by tier; inside a tier
 * the more used item, then the input order.
 */
export function rankSuggestions<T>(query: string, items: readonly T[], read: (item: T) => SuggestionRankInput, options: SuggestionRankOptions<T> = {}): T[] {
  const needle = foldSearchText(query).trim()
  // The name typed exactly first, then a keyword typed exactly („/Požadavek“ is „Požadavek“, before „Požadavek z nasazení“ with that keyword).
  const exact = (input: SuggestionRankInput) => !needle ? 0 : foldSearchText(input.title).trim() === needle ? 2 : (input.keywords ?? []).some(name => foldSearchText(name).trim() === needle) ? 1 : 0
  return items.map((item, index) => { const input = read(item); return { item, index, rank: rankSuggestion(query, input, options.abbreviations), exact: exact(input), used: options.usage?.(item) ?? 0 } })
    .filter((entry): entry is { item: T; index: number; rank: number; exact: number; used: number } => entry.rank !== undefined)
    .sort((a, b) => b.exact - a.exact || a.rank - b.rank || b.used - a.used || a.index - b.index).map(entry => entry.item)
}
