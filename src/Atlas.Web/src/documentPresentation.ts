import type { CollaborationStatus } from "./collaboration";
import t from "./i18n/shell";

/** The provider's failures by their code, in the words of the page; the provider itself speaks English. */
const collaborationErrors = (): Record<string, string> => ({
  FORBIDDEN: t.collab.forbidden,
  READ_ONLY: t.collab.readOnly,
  SUPERSEDED: t.collab.superseded,
  DOCUMENT_FULL: t.collab.documentFull,
  INVALID_CONTENT: t.collab.invalidContent,
  MIGRATION_REQUIRED: t.collab.migrationRequired,
});
/** A collaboration failure as the reader may see it: never the provider's raw text. Atlas's own messages (Czech) stay as they are. */
export function collaborationErrorText(message: string) {
  const code = /\[([A-Z_]+)\]/.exec(message)?.[1];
  if (code) return collaborationErrors()[code] ?? t.collab.failed;
  // shortcut: Atlas's messages are Czech and carry diacritics, the provider's are English; give the provider codes for all if this misses.
  return /[^\x00-\x7F]/.test(message) || [t.collab.missing, t.collab.invalidSession, t.doc.collabMissing].includes(message) ? message : t.collab.failed;
}

export interface DocumentNoticeInput {
  browserOnline: boolean;
  collaboration: CollaborationStatus | null;
  saveError: string | null;
  version: number;
  publishedVersion: number | null;
  /** The writer's session has been connecting for over a second (App.vue): the chip says so instead of the revision state. */
  slowJoin?: boolean;
  /** Nothing waits for publishing as the viewer sees it: a reader reads the publication, a live page (Živě) shows its saved state. */
  nothingToPublish?: boolean;
}

// Not „connecting“: every page opens through it, and the revision state is known from the page itself, so the chip stands from the
// first frame instead of popping in (and pushing the row) once the session is online.
const editorOwnedConnectionStates: ReadonlySet<CollaborationStatus["connection"]> =
  new Set(["offline", "reconnecting"] as const);

/**
 * The page's revision state beside its title (Publikováno / Koncept / Nepublikované změny) and the failures that stop saving.
 * Saving itself (Ukládám… / Uloženo / Offline) is the shared SaveState (3.6-B), so typing never changes this slot.
 */
export function documentNotice(input: DocumentNoticeInput): {
  tone: "neutral" | "warning" | "error"; text: string;
} {
  const collaboration = input.collaboration?.enabled ? input.collaboration : null;
  // Host-side failures stay visible regardless of transport state: the editor's notice
  // reports the connection, never a rejected page save.
  if (input.saveError)
    return { tone: "error", text: input.saveError };
  if (collaboration?.error || collaboration?.connection === "failed" || collaboration?.projectionSave === "degraded")
    return { tone: "error", text: collaboration?.error?.message ? collaborationErrorText(collaboration.error.message) : t.collab.syncFailing };
  // A live session renders its own CollaborationNotice (state and unconfirmed changes)
  // for these states; the host must not announce the same thing a second time.
  if (collaboration && editorOwnedConnectionStates.has(collaboration.connection))
    return { tone: "warning", text: "" };
  // A slow join says so in the chip's place, never above the document: a usual one shows the revision state at once.
  if (collaboration?.connection === "connecting" && input.slowJoin)
    return { tone: "neutral", text: t.collab.connecting };
  if (!input.browserOnline)
    return { tone: "warning", text: t.collab.offline };
  if (input.publishedVersion === input.version || (input.nothingToPublish && input.publishedVersion !== null))
    return { tone: "neutral", text: t.common.publishedShort };
  return { tone: "neutral", text: input.publishedVersion === null ? t.common.draftShort : t.common.unpublishedChanges };
}
