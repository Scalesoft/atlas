import { locale } from "./i18n";
import t from "./i18n/shell";

// One formatter for the whole wiki, in the language of the tool (src/i18n), never the browser locale.
const dateTimeFormat = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
const dateFormat = new Intl.DateTimeFormat(locale, { dateStyle: "medium" });

function parse(value: string | Date | null | undefined) {
  if (!value) return undefined;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date;
}

/** Day and time, e.g. „7. 9. 2026 16:33“; an empty string when there is nothing to show. */
export function formatDateTime(value: string | Date | null | undefined): string {
  const date = parse(value);
  return date ? dateTimeFormat.format(date) : "";
}

const dateTimeSecondsFormat = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "medium" });

/** Day and time with seconds, where moments within one minute have to be told apart (the space trash). */
export function formatDateTimeSeconds(value: string | Date | null | undefined): string {
  const date = parse(value);
  return date ? dateTimeSecondsFormat.format(date) : "";
}

/** The day alone, for places where the time carries no meaning. */
export function formatDate(value: string | Date | null | undefined): string {
  const date = parse(value);
  return date ? dateFormat.format(date) : "";
}

const dayLongFormat = new Intl.DateTimeFormat(locale, { dateStyle: "long", timeZone: "UTC" });

/** A calendar day sent as „2026-10-09“, spelled out („9. října 2026“). Read and shown in UTC, so it never moves with the browser's time zone. */
export function formatDay(day: string): string {
  return dayLongFormat.format(new Date(day));
}

const dayMonthFormat = new Intl.DateTimeFormat(locale, { day: "numeric", month: "numeric" });
const dayMonthYearFormat = new Intl.DateTimeFormat(locale, { day: "numeric", month: "numeric", year: "numeric" });

/** „9. 10.“ within the current year, „9. 10. 2025“ otherwise; an empty string when there is nothing to show. */
export function formatShortDate(value: string | Date | null | undefined, now = new Date()): string {
  const date = parse(value);
  if (!date) return "";
  return (date.getFullYear() === now.getFullYear() ? dayMonthFormat : dayMonthYearFormat).format(date);
}

const timeFormat = new Intl.DateTimeFormat(locale, { timeStyle: "short" });

/** The time alone, for a moment within the next minutes (e.g. „16:43“). */
export function formatTime(value: string | Date | number | null | undefined): string {
  const date = parse(typeof value === "number" ? new Date(value) : value);
  return date ? timeFormat.format(date) : "";
}

const relativeFormat = new Intl.RelativeTimeFormat(locale, { numeric: "auto" });
const MINUTE = 60_000, HOUR = 60 * MINUTE, DAY = 24 * HOUR;

/** How long ago, e.g. „před 5 minutami“, „včera“, „před 3 dny“. Past a month the day itself („7. 9. 2026“) reads better. */
export function formatAgo(value: string | Date | null | undefined, now = Date.now()): string {
  const date = parse(value);
  if (!date) return "";
  const elapsed = now - date.getTime();
  if (elapsed < MINUTE) return t.misc.justNow;
  if (elapsed < HOUR) return relativeFormat.format(-Math.floor(elapsed / MINUTE), "minute");
  if (elapsed < DAY) return relativeFormat.format(-Math.floor(elapsed / HOUR), "hour");
  if (elapsed < 31 * DAY) return relativeFormat.format(-Math.floor(elapsed / DAY), "day");
  return dateFormat.format(date);
}

const pragueDateTimeFormat = new Intl.DateTimeFormat(locale, { timeZone: "Europe/Prague", dateStyle: "medium", timeStyle: "short" });

/** Day and time in Europe/Prague, the zone the activity calendar counts its days in (a UTC evening is the next Prague day). */
export function formatActivityTime(value: string): string {
  return pragueDateTimeFormat.format(new Date(value));
}

const pragueDayFormat = new Intl.DateTimeFormat("en", { timeZone: "Europe/Prague", year: "numeric", month: "2-digit", day: "2-digit" });
/** The Europe/Prague calendar day (YYYY-MM-DD) of a moment, `days` later: the day the server reads a bare date as (a UTC date is a day early after Prague midnight). */
export function pragueDate(value: string | number | Date, days = 0): string {
  const parts = Object.fromEntries(pragueDayFormat.formatToParts(new Date(value)).map((part) => [part.type, part.value]));
  return new Date(Date.UTC(Number(parts.year), Number(parts.month) - 1, Number(parts.day) + days)).toISOString().slice(0, 10);
}
