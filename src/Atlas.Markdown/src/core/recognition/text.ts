/*
 * The one fold and the one tokenizer of the linking engine: every
 * recognizer, the suggestion lists and the server port compare text through these, so „Nováková“, „NOVAKOVA“ and „nováková“ are one.
 */

/** NFD, no combining marks, invariant lower case (never a locale's): „Jana Nováková“ → „jana novakova“. */
export const foldMarkdownText = (value: string): string => value.normalize('NFD').replace(/\p{M}+/gu, '').toLowerCase()

export interface MarkdownToken { readonly start: number; readonly end: number; readonly key: string }
/** A word: letters and digits, joined by an apostrophe or a hyphen („O’Brien“, „Nováková-Svobodová“, „PX2-1235“). */
const WORD = /[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['’-][\p{L}\p{M}\p{N}]+)*/gu
/** The words of `text` with their offsets and folded keys. */
export const tokenize = (text: string): MarkdownToken[] => [...text.matchAll(WORD)].map(match => ({ start: match.index, end: match.index + match[0].length, key: foldMarkdownText(match[0]) }))
/** The folded words of a name joined by one space: „Jana  Nováková“ → „jana novakova“. */
export const nameKey = (name: string): string => tokenize(name).map(token => token.key).join(' ')

/**
 * The stem of a folded word, for Czech inflection: one ending off („jany“, „janě“, „janou“ → „jan“; „novákové“, „novákovou“ → „novakov“;
 * „petrovi“, „petrem“ → „petr“). Only ever an offer (D4), so a stem two names share („Jan“, „Jana“) costs an underline, never a chip.
 */
export const stemOf = (key: string): string => {
  if (key.length <= 3) return key
  const stem = key.replace(/(?:ovi|ou|em|[aeiouy])$/u, '')
  return stem.length >= 3 ? stem : key
}
