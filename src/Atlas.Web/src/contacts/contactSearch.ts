import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { contactSubtitle } from "../boards/boardTargets";
import { foldText } from "../lib/foldText";

/**
 * How people are found wherever a person is picked (the editor's contact picker and `@`, the board's contact picker, the requirement's
 * „od koho / komu“): every typed word is the beginning of a word of the name, in any order, case and diacritics aside („jan kovarik“
 * finds „Jan Kovařík“), or of the organization, function, department, e-mail or login. A common Czech nickname stands for its name
 * both ways (Honza ↔ Jan). One person found in several places (the directory, an Atlas account, a page) shows once; different people
 * of one name never merge and say what tells them apart. Pure.
 */
export type PersonSource = "directory" | "account" | "page";
export interface PersonHit {
  /** Stable while the list changes: `obj:<id>`, `user:<id>`, `page:<…>`. */
  key: string;
  contact: MarkdownContact;
  source: PersonSource;
  /** The directory contact's object id. */
  objectId?: string;
  /** Where it was found: „Adresář“, „Účet Atlasu“, „Stránka · Prostor“. */
  origin: string;
  /** The directory contact's aliases („MMu“, „Mára“) and how often each was picked for it (by folded alias, ContactFields.Learn). */
  aliases?: readonly string[];
  aliasPicks?: Readonly<Record<string, number>>;
}

// shortcut: the most common Czech nicknames only; grow the list when people miss one.
const nicknames: Record<string, string[]> = {
  jan: ["honza", "jenda"], josef: ["pepa", "pepik"], frantisek: ["franta"], jiri: ["jirka"], antonin: ["tonda"], stanislav: ["standa"],
  jakub: ["kuba"], vaclav: ["vasek", "venca"], ladislav: ["lada"], miroslav: ["mirek"], ondrej: ["ondra"], vojtech: ["vojta"],
  tomas: ["tom"], michal: ["misa"], katerina: ["katka"], barbora: ["bara"], veronika: ["verca"], marie: ["maruska"], alexandr: ["sasa"],
};
const aliases = new Map<string, string[]>();
for (const [name, short] of Object.entries(nicknames)) {
  aliases.set(name, short);
  for (const nick of short) aliases.set(nick, [...(aliases.get(nick) ?? []), name]);
}

/** The words of a text, folded; an e-mail splits at its dots and @. */
const words = (text: string | undefined) => foldText(text ?? "").split(/[^\p{L}\p{N}]+/u).filter(Boolean);
export const queryTokens = (query: string) => words(query);

/** A typed word matches a word that starts with it, or a whole nickname of it. */
const wordMatches = (token: string, word: string) => word.startsWith(token) || (aliases.get(word)?.includes(token) ?? false);
const otherWords = (contact: MarkdownContact, aliases: readonly string[] = []) =>
  [contact.organization, contact.title, contact.department, contact.username, ...(contact.emails ?? []), ...aliases].flatMap(words);

/** 0 the name starts as typed, 1 the name has every word, 2 with the other fields (and the beginning of an alias); undefined: no match. */
function plainScore(tokens: readonly string[], contact: MarkdownContact, aliases?: readonly string[]): number | undefined {
  if (!tokens.length) return 1;
  const name = words(contact.name);
  if (tokens.every(token => name.some(word => wordMatches(token, word)))) return name[0] && wordMatches(tokens[0]!, name[0]) ? 0 : 1;
  const all = [...name, ...otherWords(contact, aliases)];
  return tokens.every(token => all.some(word => wordMatches(token, word))) ? 2 : undefined;
}

/**
 * Initials: one typed word of two letters or more whose first is the first letter of the given name and whose rest begins the
 * surname (the last word): „mm“ finds Marek Musil and Michal Macek, „mmu“ only Marek Musil, „nme“ Nikola Mertová.
 */
function initials(tokens: readonly string[], contact: MarkdownContact): boolean {
  const token = tokens.length === 1 ? tokens[0]! : "", name = words(contact.name);
  return token.length >= 2 && name.length >= 2 && name[0]!.startsWith(token[0]!) && name.at(-1)!.startsWith(token.slice(1));
}

/** -1 an alias is what was typed, 0–2 as plainScore, 3 the initials; undefined: no match. */
function score(tokens: readonly string[], contact: MarkdownContact, aliases: readonly string[] = []): number | undefined {
  const typed = tokens.join(" ");
  if (typed && aliases.some(alias => words(alias).join(" ") === typed)) return -1;
  return plainScore(tokens, contact, aliases) ?? (initials(tokens, contact) ? 3 : undefined);
}

export const matchesPerson = (query: string, contact: MarkdownContact, aliases?: readonly string[]) => score(queryTokens(query), contact, aliases) !== undefined;

/**
 * Whether picking `hit` after searching for `query` teaches the query as its alias (ContactFields.Learn): only a directory contact,
 * and only a query that is no ordinary match of the person (no beginning of its name, e-mail or other fields; „mar“ teaches nothing,
 * „mmu“ and „mára“ do) and not an alias of it already found by being typed whole.
 */
export function teachesAlias(query: string, hit: Pick<PersonHit, "contact" | "objectId">): boolean {
  const tokens = queryTokens(query);
  return !!hit.objectId && tokens.join("").length >= 2 && query.trim().length <= 60 && plainScore(tokens, hit.contact) === undefined;
}

/**
 * The people that match, the best first; of those whose alias was typed, the more often picked for it first; then those `boost` holds
 * (assist/relatedPeople.ts, lower-case object ids: named on pages close in meaning), which never outrank a better match; equal ones keep their order.
 */
export function rankPeople<T extends PersonHit>(query: string, hits: readonly T[], boost: ReadonlySet<string> = new Set()): T[] {
  const tokens = queryTokens(query), typed = tokens.join(" ");
  const picks = (hit: T) => hit.aliasPicks?.[typed] ?? 0;
  const boosted = (hit: T) => (hit.objectId && boost.has(hit.objectId.toLowerCase()) ? 1 : 0);
  return hits.map((hit, index) => ({ hit, index, rank: score(tokens, hit.contact, hit.aliases) }))
    .filter((entry): entry is { hit: T; index: number; rank: number } => entry.rank !== undefined)
    .sort((a, b) => a.rank - b.rank || (a.rank < 0 ? picks(b.hit) - picks(a.hit) : 0) || boosted(b.hit) - boosted(a.hit) || a.index - b.index).map(entry => entry.hit);
}

/** Each person once: a hit whose directory id or e-mail an earlier one has is that one. Different people of one name stay. */
export function mergePeople<T extends PersonHit>(hits: readonly T[]): T[] {
  const seen = new Map<string, T>(), kept: T[] = [];
  for (const hit of hits) {
    const ids = [hit.objectId && `obj:${hit.objectId}`, hit.contact.directoryId && `dir:${hit.contact.directoryId}`,
      ...(hit.contact.emails ?? []).map(email => `mail:${email.trim().toLowerCase()}`)].filter((id): id is string => !!id);
    const first = ids.map(id => seen.get(id)).find(Boolean);
    // A repeat's other ids are the same person's too.
    ids.forEach(id => seen.set(id, first ?? hit));
    if (!first) kept.push(hit);
    // An account that is also in the directory is that directory contact: it takes the object id and its aliases.
    else if (hit.objectId && !first.objectId) Object.assign(first, { objectId: hit.objectId, ...(hit.aliases && { aliases: hit.aliases, aliasPicks: hit.aliasPicks }) });
  }
  return kept;
}

const nameKey = (name: string) => words(name).join(" ");

/** The people whose name is this one, case, diacritics and spacing aside. */
export const sameName = <T extends PersonHit>(name: string, hits: readonly T[]) => { const key = nameKey(name); return key ? hits.filter(hit => nameKey(hit.contact.name) === key) : []; };

/**
 * The line under each name: function, organization and department; a person whose name another one has also gets the e-mail and
 * where it was found, so two Jan Nováks read apart. Keyed by `key`.
 */
export function describePeople(hits: readonly PersonHit[]): Map<string, string> {
  const counts = new Map<string, number>();
  for (const hit of hits) counts.set(nameKey(hit.contact.name), (counts.get(nameKey(hit.contact.name)) ?? 0) + 1);
  return new Map(hits.map(hit => {
    const subtitle = contactSubtitle(hit.contact), email = hit.contact.emails?.[0];
    const line = (counts.get(nameKey(hit.contact.name)) ?? 0) > 1 ? [subtitle, email, hit.origin] : [subtitle || email || hit.origin];
    return [hit.key, line.filter(Boolean).join(" · ")];
  }));
}

/**
 * What to ask a source that matches one piece of text (the directory, the accounts): the longest typed word, folded, and the names a
 * nickname stands for; the full matching runs here on what comes back.
 */
export function serverTerms(query: string): string[] {
  const longest = queryTokens(query).sort((a, b) => b.length - a.length)[0];
  return longest ? [longest, ...(aliases.get(longest) ?? [])].slice(0, 3) : [];
}
