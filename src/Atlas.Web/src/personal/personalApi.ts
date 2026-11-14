import { api } from "../api";
import { objectApi } from "../objects/objectApi";

/**
 * The personal space (spec 2026-10-29): Můj prostor (my private notes) and Sdíleno se mnou (others' notes shared with me now). A note
 * is an object of type `atlas.note` on the one object path (title as inline Markdown, text as Markdown). The lists carry a plain-text
 * excerpt only; `note` reads a whole note when it is opened. Sharing goes through the access kind `item` (ShareItemDialog). The owner
 * dropped personal boards on 29. 10. 2026.
 */
export const noteTypeId = "atlas.note";
export interface NoteData { title: string; text: string }
/** The server's limits of a note (NoteObjectType.MaxTitle, MaxText; a server test pins them). */
export const maxNoteTitle = 200, maxNoteText = 100_000;
export interface PersonalItem {
  id: string; typeId: string; title: string; updatedAt: string; version: number; canEdit: boolean;
  /** Můj prostor: how many shares are valid now. */
  shares: number;
  /** Sdíleno se mnou: who shares it, my level and when my access ends (null: never). */
  ownerName: string | null; level: "read" | "write" | null; expiresAt: string | null;
  /** The start of the note's text as plain text (at most 200 characters). */
  excerpt: string | null;
}
type ItemRef = Pick<PersonalItem, "id" | "typeId">;

export const personalApi = {
  mine: (signal?: AbortSignal) => api<PersonalItem[]>("/personal/items", { signal }),
  shared: (signal?: AbortSignal) => api<PersonalItem[]>("/personal/shared", { signal }),
  /** The whole note, read when it is opened (the lists send an excerpt). */
  note: async (id: string, signal?: AbortSignal) => {
    const note = await objectApi.get(id, signal);
    return { version: note.version, canEdit: note.canEdit, data: { title: String(note.data?.title ?? ""), text: String(note.data?.text ?? "") } };
  },
  createNote: (title: string) => objectApi.create(noteTypeId, "private", { title, text: "" }),
  /** `expectedVersion`: the version the change was made on; another change since answers 409 `version_conflict`. */
  changeNote: (id: string, fields: Partial<NoteData>, expectedVersion: number) => objectApi.change(id, { fields }, expectedVersion),
  trash: (item: ItemRef) => objectApi.trash(item.id),
  /** The trash of Můj prostor (the owner's only): its notes on the one object trash. */
  trashList: (signal?: AbortSignal) => objectApi.trashList({ type: noteTypeId, scope: "private" }, signal),
  restore: (id: string) => objectApi.restore(id),
  purge: (id: string) => objectApi.purge(id),
};
export type PersonalApi = typeof personalApi;
