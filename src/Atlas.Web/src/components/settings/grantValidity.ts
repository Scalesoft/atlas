import t from "../../i18n/settings-flow";
import { locale } from "../../i18n";
/**
 * The validity of a grant (owner decision 27. 9.): "Od" and "Do" are both optional
 * (`datetime-local` strings in the browser's zone; an empty Od applies at once, an empty Do never
 * ends) and the presets end it 7, 30 or 90 days after its start, or never. One set of rules for a
 * new grant (GrantFlow) and for editing one in place (GrantValidityFields in the row editors).
 */
export interface ValidityPreset {
  value: string;
  label: string;
  /** Days after Od (or now); null: no end. */
  days: number | null;
}

export const validityPresets: readonly ValidityPreset[] = [
  { value: "7", get label() { return t.validity.days(7); }, days: 7 },
  { value: "30", get label() { return t.validity.days(30); }, days: 30 },
  { value: "90", get label() { return t.validity.days(90); }, days: 90 },
  { value: "none", get label() { return t.validity.none; }, days: null },
];

/** A date as the value of `<input type="datetime-local">` in the browser's zone, to the minute. */
export function localInput(date: Date) {
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

/** The Do of a preset: `days` after Od, or after now when Od is empty; empty for no end. */
export function presetEnd(preset: ValidityPreset, startAt: string, now = new Date()) {
  if (preset.days === null) return "";
  const base = startAt ? new Date(startAt) : now;
  return localInput(new Date(base.getTime() + preset.days * 86_400_000));
}

/** The stored bounds of a grant being edited, as the API returned them. */
export interface StoredValidity {
  startAt?: string | null;
  expiresAt?: string | null;
}

/**
 * Why the bounds cannot be saved, or "" when they can: the end comes after the start and, for a new
 * grant (no `stored`) or changed bounds, lies in the future. Stored bounds left as they are pass even
 * when the grant has lapsed, so a role change still saves; the server has the same rule
 * (Rules.RequireFutureEnd, applied to new or changed bounds only).
 */
export function validityError(startAt: string, expiresAt: string, stored?: StoredValidity, now = new Date()) {
  if (startAt && expiresAt && new Date(startAt) >= new Date(expiresAt)) return t.validity.endAfterStart;
  const untouched = !!stored && startAt === localDateTime(stored.startAt) && expiresAt === localDateTime(stored.expiresAt);
  if (expiresAt && !untouched && new Date(expiresAt) <= now) return t.validity.endPast;
  return "";
}

/** The earliest Do the picker offers (its `min`): the next minute, or a minute after a later Od. */
export function minimumEnd(startAt: string, now = new Date()) {
  const next = Math.floor(now.getTime() / 60_000) * 60_000 + 60_000;
  const start = startAt ? new Date(startAt).getTime() + 60_000 : Number.NaN;
  return localInput(new Date(start > next ? start : next));
}

/** A stored UTC instant as the value of `<input type="datetime-local">` in the browser's zone (editing a grant). */
export function localDateTime(value?: string | null) {
  if (!value) return "";
  const date = new Date(value);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
}

/**
 * The local input back as a UTC instant. An unchanged input returns the server's own value, so a
 * role change keeps the stored bounds exactly (PostgreSQL keeps microseconds the input cannot show).
 */
export function utcDateTime(value: string, original?: string | null) {
  if (original && value === localDateTime(original)) return original;
  return value ? new Date(value).toISOString() : null;
}

/** One line for a summary: "Bez časového omezení", "Do …", "Od …" or "… – …". */
export function validitySummary(startAt: string, expiresAt: string) {
  const format = (value: string) => new Date(value).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  if (!startAt && !expiresAt) return t.validity.summaryNone;
  if (!startAt) return t.validity.summaryUntil(format(expiresAt));
  if (!expiresAt) return t.validity.summaryFrom(format(startAt));
  return t.validity.summaryRange(format(startAt), format(expiresAt));
}
