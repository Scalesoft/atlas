import { api, post } from "../api";
import { invalidateLexicon } from "../linking/lexicon";

/**
 * The one object path of the server (Atlas 3 F1 plan §2.3, `Features/Objects`): read, list, create, change and trash any object of a
 * type the platform stores. A change is applied once per `changeId` (a retry answers `duplicate`); the type validates it (400
 * `invalid_object_change` with its message). Pages and boards are read here but keep their own write paths. The trash lists only what
 * the reader may restore (a wiki object for who writes the type there, a space one for its Admin, a private one for its owner); a purge
 * is permanent.
 */
export type ObjectScope = "wiki" | "space" | "private";
export type ObjectData = Record<string, unknown>;
export interface ObjectDto {
  id: string; typeId: string; scope: ObjectScope; spaceId: string | null; ownerId: string | null; version: number;
  createdAt: string; createdBy: string | null; updatedAt: string; editingModel: string; canEdit: boolean; title: string; data: ObjectData | null;
}
export interface ObjectSummary { id: string; typeId: string; scope: ObjectScope; spaceId: string | null; title: string; version: number; updatedAt: string; data: ObjectData | null }
/** A page of a library, newest change first; `canCreate` for a listing of one type in one scope (in the wiki it also means the reader
 * changes the type's wiki objects). */
export interface ObjectList { items: ObjectSummary[]; total: number; page: number; pageSize: number; canCreate: boolean }
/** `name`: exact titles (case aside), any of them, in one request. */
export interface ObjectQuery { type: string; scope?: ObjectScope; spaceId?: string; q?: string; page?: number; name?: readonly string[] }
export interface ObjectChangeAck { version: number; duplicate: boolean }
/** A trashed object with who trashed it (null: before that was recorded) and when. */
/** Its data comes without the long text (a note's Markdown); `excerpt` is its plain start (PERF3-1). */
export interface TrashedObject extends Omit<ObjectSummary, "updatedAt"> { trashedAt: string; trashedById: string | null; trashedByName: string | null; excerpt?: string | null }
export interface ObjectTrashList { items: TrashedObject[]; total: number; page: number; pageSize: number }

const objectPath = (id: string) => `/objects/${encodeURIComponent(id)}`;
const saved = <T>(result: T) => { invalidateLexicon(); return result; };
const queryOf = (query: object) => new URLSearchParams(Object.entries(query).flatMap(([key, value]): [string, string][] =>
  Array.isArray(value) ? value.map(item => [key, String(item)]) : value === undefined || value === "" ? [] : [[key, String(value)]]));
export const objectApi = {
  list: (query: ObjectQuery, signal?: AbortSignal) => api<ObjectList>(`/objects?${queryOf(query)}`, { signal }),
  get: (id: string, signal?: AbortSignal) => api<ObjectDto>(objectPath(id), { signal }),
  // A saved, trashed or restored contact (a name, a learned alias) is in the spaces' lexicons: they are asked again (mostly a 304 for other objects).
  create: (typeId: string, scope: ObjectScope, data: ObjectData, spaceId?: string) =>
    post<ObjectDto>("/objects", { typeId, scope, data, ...(spaceId && { spaceId }) }).then(saved),
  change: (id: string, payload: ObjectData, expectedVersion?: number) =>
    post<ObjectChangeAck>(`${objectPath(id)}/changes`, { changeId: crypto.randomUUID(), payload, ...(expectedVersion !== undefined && { expectedVersion }) }).then(saved),
  trash: (id: string) => api<void>(objectPath(id), { method: "DELETE" }).then(saved),
  trashList: (query: Omit<ObjectQuery, "q" | "type"> & { type?: string }, signal?: AbortSignal) => api<ObjectTrashList>(`/objects/trash?${queryOf(query)}`, { signal }),
  restore: (id: string) => post<ObjectChangeAck>(`${objectPath(id)}/restore`).then(saved),
  purge: (id: string) => post<void>(`${objectPath(id)}/purge`),
};
export type ObjectApi = typeof objectApi;
