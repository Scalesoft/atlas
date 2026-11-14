import { ApiError } from "../api";
import t from "../i18n/ai";

/** What replaces an action the user just started when the task has become unavailable (AI-25), read where it is shown. */
export const aiUnavailableText = () => t.unavailable;

export type AiActionResult<T> = { state: "done"; value: T } | { state: "unavailable"; text: string };

/**
 * Runs an action the user has just started. A `503 ai_unavailable` ends it with the neutral sentence and no other trace:
 * the server wrote nothing, the caller shows `text` where the result would have been, and the control itself disappears
 * once the capabilities refresh (api.ts has already signalled that). Any other failure is the caller's to report.
 */
export async function runAiAction<T>(action: () => Promise<T>): Promise<AiActionResult<T>> {
  try {
    return { state: "done", value: await action() };
  } catch (failure) {
    if (failure instanceof ApiError && failure.code === "ai_unavailable") return { state: "unavailable", text: t.unavailable };
    throw failure;
  }
}
