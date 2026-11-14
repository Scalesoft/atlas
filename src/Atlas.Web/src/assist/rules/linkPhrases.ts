import { detectSecrets } from "./secretDetector";
import { spanOf, spansOverlap, type TextSpan } from "./span";

/**
 * E2 (plan §19): the phrases of a finished paragraph that may name a wiki page, for POST /api/assist/link-candidates.
 * Pure TypeScript, no editor. The server decides (LinkPhrasePolicy: exact title or alias, meaning only for 2–5 words);
 * this side only spends the request's budget on phrases the server would consider, so the two policies agree:
 *
 * - a phrase is 1–5 words inside one clause: punctuation, a line break or an inline component (U+FFFC) ends it;
 * - a single word counts with at least 4 characters and not a stop word, or as an acronym in capitals („VPN“, „K8S“);
 * - a longer phrase neither starts nor ends with a stop word or a short lower-case word („k VPN“, „VPN podle“) and has
 *   at least one significant word;
 * - a paragraph with a detected password is never sent (design §7.4.3: nothing near a secret leaves the browser);
 * - spans that already link (or are code) are skipped, every text is sent once, single words first, at most 40.
 */
export const linkPhraseLimits = Object.freeze({ maxPhrases: 40, maxLength: 80, maxWords: 5 });

export interface LinkPhrase {
  /** Exactly the text of `span`: the server echoes it, so a candidate maps back to its words. */
  readonly phrase: string;
  readonly span: TextSpan;
  readonly words: number;
}

export interface LinkPhraseOptions {
  /** Spans that must not be part of a phrase (an existing link, inline code). */
  readonly skip?: readonly TextSpan[];
}

/** Normalized as the server's LinkPhrasePolicy (no diacritics, lower case); keep the two lists in step. */
const stopWords = new Set([
  // Words of every page: what the text is about, never which page.
  "navod", "navodu", "navody", "postup", "postupu", "postupy", "stranka", "stranky", "strance", "stranku", "dokument",
  "dokumentu", "dokumenty", "informace", "informaci", "nastaveni", "system", "systemu", "prehled", "poznamka", "poznamky",
  "clanek", "clanku", "kapitola", "kapitole", "sekce", "sekci", "text", "textu", "obrazek", "tabulka", "tabulce", "seznam",
  "seznamu", "priklad", "prikladu", "odkaz", "odkazu", "popis", "popisu", "reseni", "problem", "problemu", "verze", "verzi",
  "soubor", "souboru", "data", "udaje", "pozor", "upozorneni", "dalsi", "nove", "novy", "nova",
  // Czech function words of four letters and more.
  "jako", "nebo", "take", "tedy", "ktery", "ktera", "ktere", "kterou", "jsou", "neni", "bude", "budou", "byla", "bylo",
  "podle", "mezi", "tento", "tato", "toto", "tyto", "jeho", "jeji", "jejich", "kdyz", "proto", "vsak", "pouze", "vice",
  "pred", "pres", "potom", "pak", "zde", "tady", "jinak", "jeste", "uz", "muze", "mohou", "musi", "lze", "nutne", "treba",
  "prosim", "dekuji",
  // English.
  "this", "that", "with", "from", "into", "have", "will", "your", "page", "pages", "guide", "note", "notes", "info",
]);

const wordPattern = /[\p{L}\p{N}]+(?:[-'’][\p{L}\p{N}]+)*/gu;
/** Only spaces may separate the words of one phrase; anything else ends the clause. */
const joiner = /^[ \t ]+$/u;

const normalize = (word: string) => word.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase();
const isAcronym = (word: string) => /^(?=.*\p{L})[\p{Lu}\p{N}]{2,3}$/u.test(word);
const isNumber = (word: string) => /^\p{N}+$/u.test(word);
/** A word that may stand alone as a page name. */
const significant = (word: string) => isAcronym(word) || (!isNumber(word) && normalize(word).length >= 4 && !stopWords.has(normalize(word)));
/** A word a phrase may start or end with. */
const edge = (word: string) => isAcronym(word) || (!isNumber(word) && !stopWords.has(normalize(word)) && (word.length >= 3 || /^\p{Lu}/u.test(word)));

interface Word { readonly text: string; readonly start: number; readonly end: number }

function clauses(text: string): Word[][] {
  const result: Word[][] = [];
  let current: Word[] = [];
  let previousEnd = -1;
  for (const match of text.matchAll(wordPattern)) {
    const word = { text: match[0], start: match.index, end: match.index + match[0].length };
    if (current.length && !joiner.test(text.slice(previousEnd, word.start))) {
      result.push(current);
      current = [];
    }
    current.push(word);
    previousEnd = word.end;
  }
  if (current.length) result.push(current);
  return result;
}

export function linkPhrases(text: string, options: LinkPhraseOptions = {}): LinkPhrase[] {
  if (!text.trim() || detectSecrets(text).length) return [];
  const skip = options.skip ?? [];
  const found: LinkPhrase[] = [];
  for (const words of clauses(text)) {
    for (let size = 1; size <= linkPhraseLimits.maxWords; size++) {
      for (let first = 0; first + size <= words.length; first++) {
        const part = words.slice(first, first + size);
        const head = part[0]!;
        const tail = part.at(-1)!;
        if (size === 1 ? !significant(head.text) : !edge(head.text) || !edge(tail.text) || !part.some(word => significant(word.text) && !isAcronym(word.text)))
          continue;
        const span = spanOf(text, head.start, tail.end);
        const phrase = text.slice(span.start, span.end);
        if (phrase.length > linkPhraseLimits.maxLength || skip.some(other => spansOverlap(other, span))) continue;
        found.push({ phrase, span, words: size });
      }
    }
  }
  // Single words first, then longer phrases, each in reading order; every text once.
  const seen = new Set<string>();
  return found
    .sort((a, b) => a.words - b.words || a.span.start - b.span.start)
    .filter(item => !seen.has(item.phrase) && !!seen.add(item.phrase))
    .slice(0, linkPhraseLimits.maxPhrases);
}
