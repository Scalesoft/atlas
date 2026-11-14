import { formatShortDate } from "../../../formatDate";
import { locale } from "../../../i18n";
import type { LogEntry, LogLevel } from "../../../logs/logApi";

export const levels: LogLevel[] = ["trace", "debug", "information", "warning", "error", "critical"];
const labels: Record<LogLevel, string> = { trace: "TRACE", debug: "DEBUG", information: "INFO", warning: "WARN", error: "ERROR", critical: "CRIT" };
export const levelLabel = (level: LogLevel) => labels[level];

/** Local time with milliseconds, with the date when it is not today. */
export function logTime(at: string, now = new Date()) {
  const date = new Date(at);
  const time = date.toLocaleTimeString(locale, { hourCycle: "h23" }) + "." + String(date.getMilliseconds()).padStart(3, "0");
  return date.toDateString() === now.toDateString() ? time : `${formatShortDate(date, now)} ${time}`;
}

/** The last two segments of a category: `Atlas.Server.Features.Backups.ZipBackupJob` → `Backups.ZipBackupJob`. */
export const shortCategory = (category: string) => category.split(".").slice(-2).join(".");

/** The lines of an exception's text, their own indentation dropped: a frame („at …“) is indented by the row, so a wrapped frame keeps its
 * indent and the next frame still starts a line. */
export const exceptionLines = (exception: string) =>
  exception.replace(/\r/g, "").split("\n").filter((line) => line.trim())
    // „at“ holds to its frame: a long frame wraps inside itself, not after the word.
    .map((line) => ({ text: line.trim().replace(/^at /, "at\u00a0"), frame: /^\s*at /.test(line) }));

/** The time window of the filter `cas`: since how long ago, in ms. */
export const timeRanges: Record<string, number> = { "15m": 15 * 60_000, "1h": 3_600_000, "24h": 86_400_000, "7d": 7 * 86_400_000 };

/** One line as a log file has it (the exception's lines under it). */
export const entryText = (entry: LogEntry) =>
  `${entry.at} [${entry.level}] ${entry.category}${entry.traceId ? ` (${entry.traceId})` : ""}: ${entry.message}${entry.exception ? `\n${entry.exception}` : ""}`;

/** Several entries, oldest first, as a log file reads. */
export const entriesText = (entries: LogEntry[]) => entries.map(entryText).reverse().join("\n");
