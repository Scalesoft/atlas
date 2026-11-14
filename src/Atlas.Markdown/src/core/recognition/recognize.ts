import { stemOf, tokenize, type MarkdownToken } from './text'
import { ISSUE_KEY_DENY, ISSUE_KEY_PATTERN } from './patterns'
import type { MarkdownLexicon } from './lexicon'
import { RECOGNITION_ENDS } from './gate'

/*
 * The pure part of the linking engine (audit-engine §4): an index of what is known by name (a trie of folded words, read backwards
 * from the caret), the named patterns, and the decision for what a text ends with or holds. No editor, no network: the same contract
 * as the server port, held together by tests/fixtures/linking/corpus.json.
 */

/** `chip`: convert at once; `unresolved`: several people, an unresolved chip waits for the pick; `suggest`: only underlined and
 *  offered (aliases, initials, inflected forms, page titles, a truncated lexicon); `pending`: a key the network confirms first. */
export type MarkdownRecognitionDecision = 'chip' | 'unresolved' | 'suggest' | 'pending' | 'none'

export interface MarkdownRecognitionEntry {
  /** `person`, `page`, `issue`… (MarkdownEntityKind.kind). */
  readonly kind: string
  /** The entity's identity within its kind: one candidate however many of its names match. */
  readonly id: string
  /** The words as known („Jana Nováková“, „MMu“, „PX2-1235“). */
  readonly name: string
  /** `full`: the whole name; `alias`: an alias, nickname or initials, only ever offered (D4). */
  readonly form: 'full' | 'alias'
  /** Whether a lone whole match becomes a chip by itself (false: a page title, anyone of a truncated lexicon). */
  readonly auto: boolean
  /** Its inflected forms („Jany Novákové“) are offered too. */
  readonly inflects?: boolean
  /** Counts only spelled exactly as `name` (a short alias „MMu“, an issue key): „MMU“ is something else. */
  readonly cased?: boolean
  /** What the kind's recognizer makes the chip of. */
  readonly target: unknown
}

export interface MarkdownRecognitionHit {
  readonly kind: string
  /** Offsets in the text that was read. */
  readonly from: number
  readonly to: number
  readonly text: string
  readonly form: 'full' | 'alias' | 'stem' | 'pattern'
  readonly decision: MarkdownRecognitionDecision
  /** The entries' targets, one per entity. */
  readonly candidates: readonly unknown[]
}

/** More words than this are no name nor title. */
const MAX_WORDS = 8
/** Only spaces between the words of a name; after an initial also its dot („J. Bureš“). */
const GAP = /^[  ]+$/u
const INITIAL_GAP = /^\.[  ]+$/u
const WORD_CHAR = /[\p{L}\p{N}]/u

interface Trie { next?: Map<string, Trie>; entries?: MarkdownRecognitionEntry[] }
export interface MarkdownRecognitionIndex {
  readonly exact: Trie
  readonly stems: Trie
  readonly longest: number
  /** Kinds whose several candidates make one unresolved chip (people); several of any other kind are only offered. */
  readonly unresolved: ReadonlySet<string>
}

/** The index of `entries`, built when the lexicon or the known entries change, never per keystroke. */
export function buildRecognitionIndex(entries: readonly MarkdownRecognitionEntry[], unresolved: ReadonlySet<string> = new Set()): MarkdownRecognitionIndex {
  const exact: Trie = {}, stems: Trie = {}
  let longest = 0
  const add = (trie: Trie, words: readonly string[], entry: MarkdownRecognitionEntry) => {
    let node = trie
    // Backwards: a match is read from the caret to the left.
    for (let i = words.length - 1; i >= 0; i--) {
      const next = node.next ??= new Map()
      let child = next.get(words[i]!)
      if (!child) next.set(words[i]!, child = {})
      node = child
    }
    if (!node.entries?.some(known => known.kind === entry.kind && known.id === entry.id)) (node.entries ??= []).push(entry)
  }
  for (const entry of entries) {
    const words = tokenize(entry.name).map(token => token.key)
    // One word is a name only spelled exactly or as an alias: „Eva“ or „Projekt“ alone is everyday text.
    if (!words.length || words.length > MAX_WORDS || (words.length === 1 && entry.form === 'full' && !entry.cased)) continue
    longest = Math.max(longest, words.length)
    add(exact, words, entry)
    if (entry.inflects && entry.form === 'full') add(stems, words.map(stemOf), { ...entry, form: 'alias', auto: false })
  }
  return { exact, stems, longest, unresolved }
}

interface Found { start: number; entries: MarkdownRecognitionEntry[]; stem: boolean }
/** The longest entry of `trie` that ends with word `end`, its words joined by spaces only (an initial by its dot and a space). */
function ending(trie: Trie, tokens: readonly MarkdownToken[], end: number, text: string, longest: number, stem: boolean): Found | null {
  let node: Trie | undefined = trie, found: Found | null = null
  for (let i = end; i >= 0 && end - i < longest; i--) {
    if (i < end) {
      const gap = text.slice(tokens[i]!.end, tokens[i + 1]!.start)
      if (!GAP.test(gap) && !(INITIAL_GAP.test(gap) && tokens[i]!.end - tokens[i]!.start === 1)) break
    }
    node = node.next?.get(stem ? stemOf(tokens[i]!.key) : tokens[i]!.key)
    if (!node) break
    const written = text.slice(tokens[i]!.start, tokens[end]!.end)
    const entries = node.entries?.filter(entry => !entry.cased || entry.name === written)
    if (entries?.length) found = { start: i, entries, stem }
  }
  return found
}

/** The decision for what matched (§4.5); a lexicon with `truncated` never gives `chip` (its entries are not `auto`). */
function decide(index: MarkdownRecognitionIndex, found: Found): { decision: MarkdownRecognitionDecision; form: MarkdownRecognitionHit['form']; kind: string; candidates: unknown[] } {
  const kind = found.entries[0]!.kind, entries = found.entries.filter(entry => entry.kind === kind)
  const whole = !found.stem && entries.every(entry => entry.form === 'full'), auto = whole && entries.every(entry => entry.auto)
  const decision = !auto ? 'suggest' : entries.length === 1 ? 'chip' : index.unresolved.has(kind) ? 'unresolved' : 'suggest'
  return { decision, form: found.stem ? 'stem' : whole ? 'full' : 'alias', kind, candidates: entries.map(entry => entry.target) }
}

/** The best match ending with word `end`: the longer of the exact and the inflected one, the exact one when as long. */
function matchEnding(index: MarkdownRecognitionIndex, tokens: readonly MarkdownToken[], end: number, text: string): MarkdownRecognitionHit | null {
  const exact = ending(index.exact, tokens, end, text, index.longest, false)
  const stem = ending(index.stems, tokens, end, text, index.longest, true)
  const found = exact && (!stem || exact.start <= stem.start) ? exact : stem
  if (!found) return null
  const from = tokens[found.start]!.start, to = tokens[end]!.end
  // No piece of an e-mail or a domain („mmu@firma.example.test“, „jana.novakova“).
  const before = text[from - 1], after = text[to]
  if (before === '@' || before === '.' || after === '@' || (after === '.' && WORD_CHAR.test(text[to + 1] ?? ''))) return null
  return { from, to, text: text.slice(from, to), ...decide(index, found) }
}

/** Every known name in `text` (a block's prose), left to right, the longest where they overlap, at most `limit`. Pure. */
export function recognizeText(text: string, index: MarkdownRecognitionIndex, limit = Infinity): MarkdownRecognitionHit[] {
  if (!index.longest) return []
  const tokens = tokenize(text), hits: MarkdownRecognitionHit[] = []
  for (let end = 0; end < tokens.length; end++) {
    const hit = matchEnding(index, tokens, end, text)
    if (!hit) continue
    // A longer name over the last one replaces it; one that starts inside it is a piece of it.
    while (hits.length && hit.from < hits[hits.length - 1]!.to) {
      if (hit.from > hits[hits.length - 1]!.from) break
      hits.pop()
    }
    if (hits.length && hit.from < hits[hits.length - 1]!.to) continue
    hits.push(hit)
    if (hits.length >= limit) break
  }
  return hits
}

const KEY_AT_END = new RegExp(`(?:^|[^\\p{L}\\p{N}#-])(#?)(${ISSUE_KEY_PATTERN})$`, 'u')
/** The issue key `body` ends with („… viz #PX2-1235“ → `#PX2-1235`), if its project is one of `projects` and no standard's. Pure. */
export function issueKeyAtEnd(body: string, projects: readonly string[]): { from: number; to: number; key: string } | null {
  const match = KEY_AT_END.exec(body)
  if (!match) return null
  const key = match[2]!, project = key.slice(0, key.lastIndexOf('-'))
  if (ISSUE_KEY_DENY.has(project) || !projects.includes(project)) return null
  return { from: body.length - key.length - match[1]!.length, to: body.length, key }
}

/**
 * What `before` (the window, its last character just typed) ends with, before that character, when it is a space or a punctuation
 * mark: a known name or key (index), else a key of a YouTrack project of the lexicon to confirm (`pending`). Pure.
 */
export function recognizeTail(before: string, index: MarkdownRecognitionIndex | undefined, lexicon?: MarkdownLexicon): MarkdownRecognitionHit | null {
  if (!RECOGNITION_ENDS.test(before.slice(-1))) return null
  const body = before.slice(0, -1), tokens = tokenize(body), last = tokens.length - 1
  if (last < 0 || tokens[last]!.end !== body.length) return null
  const known = index?.longest ? matchEnding(index, tokens, last, body) : null
  if (known && known.decision !== 'suggest') return known
  const key = issueKeyAtEnd(body, lexicon?.youTrackProjects ?? [])
  return key ? { kind: 'issue', from: key.from, to: key.to, text: body.slice(key.from), form: 'pattern', decision: 'pending', candidates: [] } : known
}
