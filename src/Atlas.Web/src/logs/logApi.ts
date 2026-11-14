import { api } from "../api";

/**
 * The server's own log (ILogger output stored in the database): the cookie API of Správa wiki → Systémový log, wiki admins only.
 * JSON camelCase; a refusal is an ApiError (`forbidden`).
 */
export type LogLevel = "trace" | "debug" | "information" | "warning" | "error" | "critical";
export interface LogEntry {
  id: number; at: string; level: LogLevel; category: string; eventId: number | null; message: string;
  exceptionType: string | null; exceptionMessage: string | null; exception: string | null; traceId: string | null; userId: string | null;
  /** The background job run the line was logged in; `jobId`/`jobTitle` are null when the run row is gone. */
  jobRunId: number | null; jobId: string | null; jobTitle: string | null;
}
/** `dropped`: entries dropped since the server started because its queue was full. */
export interface LogSummary { errorsHour: number; warningsHour: number; errorsDay: number; warningsDay: number; dropped: number }
/** Newest first. With `before`, `more` = older entries exist; with `after`, `more` = more than `take` new ones arrived (a gap: replace the list). `summary` only without a cursor. */
export interface LogPage { entries: LogEntry[]; more: boolean; summary: LogSummary | null }
/** `level` is a minimum, `category` a prefix, `q` a text in the message or exception. */
export interface LogFilter { level?: LogLevel; category?: string; q?: string; traceId?: string; jobRunId?: number; exception?: boolean; from?: string; to?: string }

export interface LogApi {
  list(filter: LogFilter, cursor?: { before?: number; after?: number }, take?: number): Promise<LogPage>;
  /** A link the browser downloads (NDJSON, newest first): no fetch. */
  exportUrl(filter: LogFilter): string;
}

function query(filter: LogFilter, extra: Record<string, string | number | undefined> = {}) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries({ ...filter, exception: filter.exception ? "true" : undefined, ...extra })) if (value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const logApi: LogApi = {
  list: (filter, cursor = {}, take = 100) => api<LogPage>(`/admin/logs${query(filter, { ...cursor, take })}`),
  exportUrl: (filter) => `/api/admin/logs/export${query(filter)}`,
};
