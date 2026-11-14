import { historyStampText, pragueNow, timedStamp } from "@atlas/markdown/core";

/** One record of a requirement's history: a list line of its note that starts with a date (plan 3.4 §3). */
export interface HistoryEntry { readonly date: string; readonly time?: string; readonly text: string }

/**
 * The stamp of a history line's text (without its list marker): the timed list's stamp („15. 10. 2026 14:05 — text“ or without the time,
 * the rest may be empty) on a day in the calendar, with where its parts end so the editor can mark them (requirementsPlugin.ts
 * historyStamps): `dateEnd` after the year, `timeStart`/`timeEnd` around the time, `textStart` after the dash. Null for text that is not a
 * history line or a day that is not in the calendar. RequirementTree.cs HistoryLine decides the same (tests/fixtures/content/history-stamps.json).
 */
export function historyStamp(text: string) {
  const stamp = timedStamp(text);
  if (!stamp) return null;
  const [day, month, year] = text.slice(0, stamp.dateEnd).split(".").map(Number) as [number, number, number];
  const clock = stamp.timeEnd === undefined ? undefined : text.slice(stamp.dateEnd, stamp.timeEnd).trim();
  const [hour, minute] = clock?.split(":") ?? [];
  const date = new Date(Date.UTC(year, month - 1, day));
  if (date.getUTCDate() !== day || date.getUTCMonth() !== month - 1 || (hour !== undefined && (+hour > 23 || +minute! > 59))) return null;
  const timeStart = clock === undefined ? undefined : stamp.timeEnd! - clock.length;
  return {
    date: date.toISOString().slice(0, 10),
    time: hour === undefined ? undefined : `${hour.padStart(2, "0")}:${minute}`,
    dateEnd: stamp.dateEnd,
    timeStart,
    timeEnd: stamp.timeEnd,
    textStart: stamp.textStart,
  };
}

/** The history records of a note's text, in the order written; a line without a date (or with an impossible one) is plain text, and so
 * is a fresh stamp with nothing written after it yet. */
export function historyEntries(notesText: string): HistoryEntry[] {
  return notesText.split("\n").flatMap(text => {
    const item = /^\s*[-*+]\s+/.exec(text), stamp = item && historyStamp(text.slice(item[0].length));
    const rest = stamp ? text.slice(item![0].length + stamp.textStart).trim() : "";
    return rest ? [{ date: stamp!.date, ...(stamp!.time ? { time: stamp!.time } : {}), text: rest }] : [];
  });
}

/** The newest record by its date and time; of equal ones the first in the text (new records go on top), undefined when there is none. */
export function latestEntry(entries: readonly HistoryEntry[]): HistoryEntry | undefined {
  const at = (entry: HistoryEntry) => `${entry.date} ${entry.time ?? ""}`;
  return entries.reduce<HistoryEntry | undefined>((latest, entry) => !latest || at(entry) > at(latest) ? entry : latest, undefined);
}

/** „15. 10. 2026 · 14:05 — text“: a record as the L level shows it. */
export const entryText = (entry: HistoryEntry) => `${historyStampText({ date: entry.date })}${entry.time ? ` · ${entry.time}` : ""} — ${entry.text}`;

// Now in Prague and a history line's stamp „1. 11. 2026 23:40“: the editor's timed list writes the same (features/timed/timedList.ts).
export { historyStampText, pragueNow };

/** A history line's text as a note and the PM's actions write it: „1. 11. 2026 23:40 — text“. */
export const historyText = (text: string, when: { date: string; time?: string } = pragueNow()) => `${historyStampText(when)} — ${text}`;
