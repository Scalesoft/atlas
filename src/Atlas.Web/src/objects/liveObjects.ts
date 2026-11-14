import { shallowReactive } from "vue";
import { ApiError } from "../api";
import { objectApi, type ObjectApi, type ObjectData } from "./objectApi";

/**
 * The objects this tab shows, one live copy per object whatever shows it (objects spec §1: one identity, one data): a board's object
 * cards, the contact directory. Seeded by what already came (a board's targets, a library page), read when missing, kept current by
 * what the object's viewers hear (`ObjectChanged`, merged per field) and locked when it is revoked. Edits show at once and go
 * through the object path. The live connection calls `apply`, `revoke` and `refresh`; nothing here subscribes.
 */
export interface LiveObject {
  id: string; typeId: string; version: number;
  /** Plain text, for labels; a card renders `data`. */
  title: string;
  data: ObjectData | null;
  canEdit: boolean;
  /** False: gone, trashed or not readable — a locked card, nothing of its content. */
  available: boolean;
}
export interface ObjectChangedMessage { objectId: string; version: number; change: unknown }

const isRecord = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);

/** The object as a board target (`{ type, resourceId, … }`) or as the object path answers it (`{ id, typeId, … }`); null without an id. */
export function liveObjectOf(raw: unknown): LiveObject | null {
  if (!isRecord(raw)) return null;
  const id = typeof raw.resourceId === "string" ? raw.resourceId : typeof raw.id === "string" ? raw.id : null;
  const typeId = typeof raw.type === "string" ? raw.type : typeof raw.typeId === "string" ? raw.typeId : "";
  if (!id) return null;
  if (raw.available === false) return locked(id, typeId);
  return { id, typeId, version: typeof raw.version === "number" ? raw.version : 0, title: typeof raw.title === "string" ? raw.title : "",
    data: isRecord(raw.data) ? { ...raw.data } : null, canEdit: raw.canEdit === true, available: true };
}
const locked = (id: string, typeId: string): LiveObject => ({ id, typeId, version: 0, title: "", data: null, canEdit: false, available: false });

const merged = (data: ObjectData, fields: Record<string, unknown>): ObjectData => {
  const next = { ...data };
  for (const [key, value] of Object.entries(fields)) { if (value === null) delete next[key]; else next[key] = value; }
  return next;
};

/** The object after a change its viewers heard: `stale` when it has it already (its own edit's echo), `refresh` when the change
 * cannot be merged here (a version skipped, a change that is no field change). Pure. */
export function applyObjectChange(object: LiveObject, message: ObjectChangedMessage): LiveObject | "stale" | "refresh" {
  if (message.version <= object.version) return "stale";
  const fields = isRecord(message.change) && isRecord(message.change.fields) ? message.change.fields : null;
  if (message.version !== object.version + 1 || !fields || !object.data) return "refresh";
  return { ...object, version: message.version, data: merged(object.data, fields) };
}

export function createLiveObjects(source: Pick<ObjectApi, "get" | "change">) {
  const objects = shallowReactive(new Map<string, LiveObject>());
  const reading = new Map<string, Promise<void>>();

  function refresh(id: string): Promise<void> {
    const pending = reading.get(id);
    if (pending) return pending;
    const read = source.get(id)
      .then(dto => { const object = liveObjectOf(dto); if (object) objects.set(id, object); })
      .catch(failure => { if (failure instanceof ApiError && (failure.status === 404 || failure.status === 403)) objects.set(id, locked(id, objects.get(id)?.typeId ?? "")); })
      .finally(() => reading.delete(id));
    reading.set(id, read);
    return read;
  }

  return {
    get: (id: string) => objects.get(id),
    /** What already came; an older copy than the one here is ignored. */
    seed(object: LiveObject) {
      const known = objects.get(object.id);
      if (!known || !object.available || object.version >= known.version) objects.set(object.id, object);
    },
    /** Reads the object unless it is here or on its way. */
    load: (id: string) => (objects.has(id) ? reading.get(id) ?? Promise.resolve() : refresh(id)),
    refresh,
    apply(message: ObjectChangedMessage) {
      const object = objects.get(message.objectId);
      if (!object?.available) return;
      const next = applyObjectChange(object, message);
      if (next === "refresh") void refresh(message.objectId);
      else if (next !== "stale") objects.set(message.objectId, next);
    },
    revoke(id: string) { objects.set(id, locked(id, objects.get(id)?.typeId ?? "")); },
    /** Sets fields of a Fields object (`{ fields }`, null clears one), shown at once. The answered version is this edit's when it
     * follows what is here (a colleague's change heard meanwhile stays, this edit is the later one); otherwise, and after a refusal,
     * the object is read again (unreadable, offline: what was here before comes back). A refusal is thrown on. */
    async edit(id: string, fields: Record<string, unknown>) {
      const before = objects.get(id);
      if (!before?.data) return;
      const shown = { ...before, data: merged(before.data, fields) };
      objects.set(id, shown);
      try {
        const ack = await source.change(id, { fields });
        const now = objects.get(id);
        if (now?.data && ack.version === now.version + 1) objects.set(id, { ...now, version: ack.version, data: merged(now.data, fields) });
        else if (!now || ack.version > now.version) await refresh(id);
      } catch (failure) {
        await refresh(id);
        if (objects.get(id) === shown) objects.set(id, before);
        throw failure;
      }
    },
  };
}
export type LiveObjects = ReturnType<typeof createLiveObjects>;

/** The tab's one store. */
export const liveObjects = createLiveObjects(objectApi);
