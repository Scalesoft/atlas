export interface ActivitySummary {
  revisions: number; pages: number; days: { day: string; count: number }[];
  authors: { userId: string; userName: string; revisions: number; pages: number }[];
}
export interface ActivityChange {
  pageId: string; title: string; authorId: string; authorName: string; at: string; version: number;
}
export interface ActivityChanges { items: ActivityChange[]; hasMore: boolean }
export interface PopularPage { pageId: string; title: string; views: number; readers: number }
/** A view of the page, or (managers of the vault only) one release of secret fields on it: `fields` in registry order. */
export type ReadershipItem =
  | { kind: "view"; userId: string; userName: string; at: string }
  | { kind: "secret"; userId: string; userName: string; at: string; secretTitle: string; fields: string[]; operation: "reveal" | "copy" };
export interface Readership {
  views: number; readers: number; secrets: number;
  days: { day: string; views: number; secrets: number }[];
  items: ReadershipItem[]; hasMore: boolean;
}
export function shiftDay(day: string, offset: number) {
  const date = new Date(day + "T12:00:00Z");
  date.setUTCDate(date.getUTCDate() + offset);
  return date.toISOString().slice(0, 10);
}
export function periodQuery(from: string, to: string) { return new URLSearchParams({ from, to }).toString(); }
export function validPeriod(from: string, to: string) {
  const days = (Date.parse(to) - Date.parse(from)) / 86_400_000;
  return from.length === 10 && to.length === 10 && Number.isFinite(days) && days >= 0 && days <= 365;
}
