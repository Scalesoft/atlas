import { watch } from "vue";
import t from "./i18n/shell";
import { ApiError, put } from "./api";

/**
 * The stored page limits in characters (JavaScript `length`): Rules.MaxDocumentJsonLength and
 * Rules.MaxDocumentMarkdownLength in src/Atlas.Server/Platform/Validation/Rules.cs (savePage.test.ts
 * keeps them equal). Large tables make the editor JSON many times the Markdown, so it usually hits first.
 */
export const documentLimits = { json: 2_000_000, markdown: 1_000_000 } as const;

/** "2,3 MB", rounded up like the server's message, so a size over a limit never reads as the limit. */
function megabytes(characters: number) {
  const tenths = Math.ceil(characters / 100_000);
  return tenths % 10 === 0 ? `${tenths / 10} MB` : `${Math.floor(tenths / 10)},${tenths % 10} MB`;
}

/** Which limit a page exceeds and what helps, in the server's words (Rules.ValidateDocument); "" when it fits. */
export function documentSizeError(contentJson: string, markdown: string) {
  if (markdown.length > documentLimits.markdown)
    return t.save.tooLargeText(megabytes(markdown.length), megabytes(documentLimits.markdown));
  if (contentJson.length > documentLimits.json)
    return t.save.tooLargeContent(megabytes(contentJson.length), megabytes(documentLimits.json));
  return "";
}

/**
 * Before an explicit save of a live page: the server takes it only once its projection of the shared log is current, and answers
 * `collaboration_pending` until then (U4-11: Hotovo just after a few changes was refused four times in a second). The editor reports
 * that projection; the save waits for it, at most `timeoutMs`, and the retry below still covers what the report does not.
 */
export function untilProjected(status: () => { enabled: boolean; projectionSave: string } | null | undefined, timeoutMs = 3000): Promise<void> {
  const waiting = () => { const current = status(); return !!current?.enabled && current.projectionSave === "pending"; };
  if (!waiting()) return Promise.resolve();
  return new Promise(resolve => {
    const done = () => { clearTimeout(timer); stop(); resolve(); };
    const timer = setTimeout(done, timeoutMs);
    const stop = watch(waiting, still => { if (!still) done(); });
  });
}

/** The server's projection of the shared log is not current yet: the same save goes through shortly (CollaborationCheckpoints). */
export const isCollaborationPending = (error: unknown) =>
  error instanceof ApiError && error.status === 409 && error.code === "collaboration_pending";

/** The checkpoint was of a generation the page left (another writer restored a version): the editor joins the new one by itself
 * (DocumentEditor `reopened`) and the next checkpoint goes from there. */
export const isCollaborationSuperseded = (error: unknown) =>
  error instanceof ApiError && error.status === 409 && error.code === "collaboration_superseded";

/**
 * Saves a page. Content over a stored limit is refused here with the server's message, before the
 * upload (a body over the transport limit would otherwise come back as a 413 that names no limit).
 * Retry only the explicit server wait condition, never content/version conflicts.
 */
export async function putWithCollaborationRetry<T>(
  path: string,
  body: unknown,
  onRetry?: () => void,
  maxRetries = 37,
): Promise<T> {
  if (body && typeof body === "object" && "contentJson" in body && "markdown" in body
    && typeof body.contentJson === "string" && typeof body.markdown === "string") {
    const tooLarge = documentSizeError(body.contentJson, body.markdown);
    if (tooLarge) throw new Error(tooLarge);
  }
  const generation =
    body && typeof body === "object" && "collaborationGeneration" in body
      ? body.collaborationGeneration
      : undefined;
  for (let retries = 0; ; retries++) {
    try {
      return await put<T>(path, body);
    } catch (error) {
      if (
        !isCollaborationPending(error) ||
        typeof generation !== "string" ||
        !generation ||
        retries >= maxRetries
      )
        throw error;
      onRetry?.();
      await new Promise<void>((resolve) => setTimeout(resolve, 80));
    }
  }
}
