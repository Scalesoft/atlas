// What a requirement's title takes from what is typed into it (plan 3.1 §2.3a, owner R1): very lenient Czech phrases for the
// deadline, the party and a dependency, and YouTrack issue keys. Pure, so the editor's plugin (requirementInline.ts) and the tests share it.
import { shiftDay } from "../activity";
import { foldText } from "../lib/foldText";
import { parseDate } from "./requirementModel";

const weekday = (day: string) => new Date(day + "T12:00:00Z").getUTCDay();
/** The next such weekday after `today` (0 = Sunday): „do pátku“ on a Friday is the next one. */
const nextWeekday = (today: string, target: number) => shiftDay(today, ((target - weekday(today) + 6) % 7) + 1);
const endOfMonth = (today: string) => {
  const [year, month] = today.split("-").map(Number) as [number, number];
  return new Date(Date.UTC(year, month, 0)).toISOString().slice(0, 10);
};
// Folded (no diacritics), each in the forms „do …“ takes: „do pátku“, „do středy“, „do neděle“.
const weekdays: [RegExp, number][] = [[/^(pondeli|pondelka)$/, 1], [/^(utery|uterka)$/, 2], [/^(streda|stredy)$/, 3], [/^(ctvrtek|ctvrtka)$/, 4],
  [/^(patek|patku)$/, 5], [/^(sobota|soboty)$/, 6], [/^(nedele)$/, 0]];

/**
 * A deadline as typed after „do “: „15. 11.“, „15. 11“, „15.11.“, „15. 11. 2026“, „2026-11-15“, „zítra“/„zítřka“, „pozítří“, „dneška“, a weekday
 * („pátku“, „příštího pondělí“), „konce týdne“ (Friday), „konce měsíce“, „konce roku“, „týdne“ or „N dnů“/„N týdnů“. A day and month
 * alone need a space after the first dot or a closing dot („3.1“ is a version, not a day), and are this year's, or next year's once they are more than 60 days past (nobody writes a deadline long gone). Null for anything else.
 */
export function parseDeadline(typed: string, today: string): string | null {
  const text = foldText(typed).replace(/\s+/g, " ").replace(/[,;!?]+$/, "").trim();
  const short = /^(\d{1,2})\.(?: (\d{1,2})\.?|(\d{1,2})\.)$/.exec(text);
  if (short) {
    const month = short[2] ?? short[3], thisYear = parseDate(`${short[1]}. ${month}. ${today.slice(0, 4)}`);
    if (!thisYear) return null;
    return thisYear < shiftDay(today, -60) ? parseDate(`${short[1]}. ${month}. ${Number(today.slice(0, 4)) + 1}`) : thisYear;
  }
  const full = /^(\d{1,2})\.\s?(\d{1,2})\.\s?(\d{4})$/.exec(text);
  if (full || /^\d{4}-\d{2}-\d{2}$/.test(text)) return parseDate(full ? `${full[1]}. ${full[2]}. ${full[3]}` : text) || null;
  if (/^(dnes|dneska)$/.test(text)) return today;
  if (/^(zitra|zitrka)$/.test(text)) return shiftDay(today, 1);
  if (/^(pozitri|pozitrka)$/.test(text)) return shiftDay(today, 2);
  if (/^konce? tydne$/.test(text)) return weekday(today) === 5 ? today : nextWeekday(today, 5);
  if (/^konce? mesice$/.test(text)) return endOfMonth(today);
  if (/^konce? roku$/.test(text)) return `${today.slice(0, 4)}-12-31`;
  if (text === "tydne") return shiftDay(today, 7);
  const span = /^(\d{1,3}) (dnu|dni|dne|tydnu|tydny|tydne)$/.exec(text);
  if (span) return shiftDay(today, Number(span[1]) * (span[2].startsWith("t") ? 7 : 1));
  const next = /^(?:pristiho |pristi )?(.+)$/.exec(text)?.[1] ?? "", day = weekdays.find(([pattern]) => pattern.test(next));
  if (day) return shiftDay(nextWeekday(today, day[1]), text.startsWith("prist") ? 7 : 0);
  return null;
}

/** A typed match ending at the caret: where it starts in the text before the caret, what it is, and its value. */
export interface PhraseMatch { index: number; text: string; value: string }

const deadlineStart = /(^|[\s(])(do|termín|termin|deadline)\s/giu;
/**
 * „do pátku “ at the end of `before` (the title up to the caret, the space just typed included): the phrase from „do“ to that space
 * and its day. The last „do“ that reads as a deadline wins (at most four words after it), so „připojit do sítě do pátku “ takes „do pátku“.
 */
export function findDeadline(before: string, today: string): PhraseMatch | null {
  if (!/\S\s$/.test(before)) return null;
  const starts = [...before.matchAll(deadlineStart)].map(match => match.index + match[1]!.length).reverse();
  for (const index of starts) {
    const text = before.slice(index), rest = text.replace(/^\S+\s+/, "").trim();
    if (rest.split(/\s+/).length > 4) break;
    const value = parseDeadline(rest, today);
    if (value) return { index, text, value };
  }
  return null;
}

/** „čekáme na “, „čeká na “, „waiting “ and the name typed after it (the query): the party picker's trigger. */
const partyTrigger = /(^|[\s(„"–-])((?:čekáme|čeká|čekám|čekají|cekame|ceka|cekam)\s+na|waiting(?:\s+for|\s+on)?)\s(?!\s)((?:(?!\s\s).){0,40})$/iu;
/** „závisí na #“, „po #“, „after #“ and what is typed after the sign: the dependency picker's trigger (not @: that is the contacts' picker). */
const dependencyTrigger = /(^|\s)((?:závisí|zavisi)\s+na|po|after|depends\s+on)\s?#((?:(?!\s\s)[^@#]){0,40})$/iu;

/** A trigger phrase at the end of `before`: where the phrase starts and the query typed after it. */
export interface TriggerMatch { index: number; query: string }
const trigger = (pattern: RegExp) => (before: string): TriggerMatch | null => {
  const match = pattern.exec(before);
  return match ? { index: match.index + match[1]!.length, query: match[3] ?? "" } : null;
};
export const findPartyTrigger = trigger(partyTrigger);
export const findDependencyTrigger = trigger(dependencyTrigger);

/**
 * A YouTrack issue key just finished by a space or a punctuation mark: „#PX2-1235 “ or „PX2-1235, “. `text` is the key as typed (with
 * its #) followed by that mark, `value` the key. Not a key glued to a word or an address („a-PX2-1“, „x@PX2-1“).
 */
export function findIssueKey(before: string): PhraseMatch | null {
  const match = /(^|[\s(„"])(#?([A-Z][A-Z0-9]+-[1-9]\d{0,6}))[\s.,;:!?)“"]$/u.exec(before);
  return match ? { index: match.index + match[1]!.length, text: before.slice(match.index + match[1]!.length), value: match[3]! } : null;
}
