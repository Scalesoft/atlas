import { geometryOf } from "./geometry";
import { defaultBoardSettings, isObjectEdit, type BoardAck, type BoardBatch, type BoardChange, type BoardColor, type BoardDto, type BoardEdit, type BoardItemKind, type BoardProps, type BoardSettings, type Geometry, type ObjectEdit } from "./boardApi";

/**
 * The client side of a board (spec §5, §8): the confirmed state the server ordered, the local batches still pending, and the
 * optimistic view = confirmed + pending replayed in order. No Vue, no I/O: the session (boardSession.ts) sends the batches,
 * feeds the answers and the hub's batches back and decides when to catch up or reload.
 */
export interface BoardItem {
  id: string; kind: BoardItemKind; x: number; y: number; width: number; height: number; z: number;
  text?: string; color?: BoardColor; targetResourceId?: string; from?: string; to?: string;
  /** Typed by kind (interaction spec §4); keys the client does not know are kept as they came. */
  props?: BoardProps;
  /** The group or frame it belongs to; one that is not on the board (deleted) holds nothing (boardGroups.ts). */
  parent?: string;
  /** Soft-deleted: kept so that a restore (or an undo of the delete) brings it back. */
  deleted?: boolean;
}
export interface BoardSnapshot { title: string; sequence: number; items: readonly BoardItem[]; settings?: BoardSettings }
export interface PendingBatch {
  readonly operationId: string;
  readonly changes: readonly BoardChange[];
  /** The sequence the server answered with while batches before it were still missing here. */
  readonly acknowledged?: number;
}
/** `gap`: batches before this one are missing (catch up); `reload`: the batch refers to something this state does not know. */
export type ReceiveResult = "applied" | "stale" | "gap" | "reload";
export type AckResult = "confirmed" | "gap" | "reload";

interface State { title: string; items: Map<string, BoardItem>; settings: BoardSettings }
/** `prop:<key>` is one key of `props`, undone on its own (spec §4: `set` changes props key by key, null removes a key); `object:<key>`
 * a field of a card's object (an ObjectEdit). */
type Property = "alive" | "geometry" | "text" | "color" | "z" | "title" | "parent" | `prop:${string}` | `setting:${string}` | `object:${string}`;
/** One property a gesture changed, with its value before and after. Undo and redo write it back only where it still holds. */
interface Step { property: Property; itemId: string; before: unknown; after: unknown; /** The object of an `object:` step. */ objectId?: string }
/** `coalesce`: the editing it belongs to; the saves of one editing (each pause in typing) are one step (W4-4). */
interface Entry { steps: Step[]; operationId: string; coalesce?: string }

export function fromBoardDto(dto: BoardDto): BoardSnapshot {
  return {
    title: dto.title,
    sequence: dto.sequence,
    settings: { ...defaultBoardSettings, ...dto.settings },
    items: dto.items.map(({ id, kind, x, y, width, height, z, text, color, from, to, target, props, parent }) => ({
      id, kind, x, y, width, height, z,
      ...(text != null && { text }), ...(color != null && { color }), ...(from != null && { from }), ...(to != null && { to }),
      ...(parent != null && { parent }),
      ...(target && (target.type === "atlas.page" || kind === "object") && { targetResourceId: target.resourceId }),
      ...(props && { props: { ...props } }),
    })),
  };
}

const stateOf = (snapshot: BoardSnapshot): State => ({ title: snapshot.title, items: new Map(snapshot.items.map(item => [item.id, { ...item }])), settings: { ...defaultBoardSettings, ...snapshot.settings } });
const copyState = (state: State): State => ({ title: state.title, items: new Map(state.items), settings: state.settings });
const alive = (state: State, id: string | undefined) => { const item = id ? state.items.get(id) : undefined; return item && !item.deleted ? item : undefined; };
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** The connector rule of the server's read: a connector shows only while both its ends exist and are not deleted. */
function visible(state: State, item: BoardItem) {
  return !item.deleted && (item.kind !== "connector" || (!!alive(state, item.from) && !!alive(state, item.to)));
}

/**
 * Applies one change. Returns false when it does not fit the state: an item that is unknown, or (except for restore) deleted.
 * The confirmed state then needs a reload; the optimistic replay just skips it (the server will reject it).
 */
function applyChange(state: State, change: BoardChange): boolean {
  if (change.type === "rename") { state.title = change.title; return true; }
  if (change.type === "settings") { state.settings = { ...state.settings, ...change.settings }; return true; }
  if (change.type === "create") {
    state.items.set(change.item.id, { ...change.item, ...(change.item.props && { props: { ...change.item.props } }) });
    return true;
  }
  const item = state.items.get(change.itemId);
  if (!item || (item.deleted && change.type !== "restore")) return false;
  const next: BoardItem = { ...item };
  switch (change.type) {
    case "move": Object.assign(next, geometryOf(change)); break;
    case "set":
      if (change.text !== undefined) next.text = change.text;
      // An empty colour removes it (the server takes "" so).
      if (change.color) next.color = change.color; else if (change.color === "") delete next.color;
      if (change.props) {
        const props: Record<string, unknown> = { ...next.props };
        for (const [key, value] of Object.entries(change.props)) if (value === null) delete props[key]; else if (value !== undefined) props[key] = value;
        next.props = props as BoardProps;
        // Only the server sets a card's object (a contact merge, BoardBatchWriter.RetargetAsync): the card follows it.
        if (next.kind === "object" && typeof change.props.objectId === "string") next.targetResourceId = change.props.objectId;
      }
      break;
    case "reorder": next.z = change.z; break;
    case "parent": if (change.parentId) next.parent = change.parentId; else delete next.parent; break;
    case "delete": next.deleted = true; break;
    case "restore": delete next.deleted; break;
  }
  state.items.set(item.id, next);
  return true;
}

function read(state: State, step: Step): unknown {
  if (step.property === "title") return state.title;
  if (step.property.startsWith("setting:")) return (state.settings as unknown as Record<string, unknown>)[step.property.slice(8)] ?? null;
  if (step.property === "alive") { const item = state.items.get(step.itemId); return item && !item.deleted; }
  const item = alive(state, step.itemId);
  if (!item) return undefined;
  if (step.property.startsWith("prop:")) return (item.props as Record<string, unknown> | undefined)?.[step.property.slice(5)] ?? null;
  switch (step.property) {
    case "geometry": return geometryOf(item);
    case "text": return item.text ?? "";
    case "color": return item.color ?? null;
    case "z": return item.z;
    case "parent": return item.parent ?? null;
    default: return undefined;
  }
}

function write(step: Step, value: unknown): BoardChange {
  const itemId = step.itemId;
  switch (step.property) {
    case "title": return { type: "rename", title: value as string };
    case "alive": return value ? { type: "restore", itemId } : { type: "delete", itemId };
    case "geometry": return { type: "move", itemId, ...(value as Geometry) };
    case "text": return { type: "set", itemId, text: value as string };
    case "color": return { type: "set", itemId, color: (value as BoardColor | null) ?? "" };
    case "z": return { type: "reorder", itemId, z: value as number };
    case "parent": return value ? { type: "parent", itemId, parentId: value as string } : { type: "parent", itemId };
    default: return step.property.startsWith("setting:")
      ? { type: "settings", settings: { [step.property.slice(8)]: value } }
      : { type: "set", itemId, props: { [step.property.slice(5)]: value } };
  }
}

/** The properties a batch changes, read before and after each change on a scratch copy of the view; an object's fields as its edit says. */
function stepsOf(view: State, changes: readonly BoardEdit[]): Step[] {
  const scratch = copyState(view);
  const steps: Step[] = [];
  for (const change of changes) {
    if (isObjectEdit(change)) {
      for (const key of Object.keys(change.after)) {
        const before = change.before[key] ?? null, after = change.after[key] ?? null;
        if (!same(before, after)) steps.push({ property: `object:${key}`, itemId: change.itemId, objectId: change.objectId, before, after });
      }
      continue;
    }
    const itemId = change.type === "rename" || change.type === "settings" ? "" : change.type === "create" ? change.item.id : change.itemId;
    const properties: Property[] =
      change.type === "rename" ? ["title"]
      : change.type === "settings" ? Object.keys(change.settings).map(key => `setting:${key}` as const)
      : change.type === "create" || change.type === "delete" || change.type === "restore" ? ["alive"]
      : change.type === "move" ? ["geometry"]
      : change.type === "reorder" ? ["z"]
      : change.type === "parent" ? ["parent"]
      : [
        ...(change.text !== undefined ? ["text" as const] : []), ...(change.color !== undefined ? ["color" as const] : []),
        ...Object.keys(change.props ?? {}).map(key => `prop:${key}` as const),
      ];
    const before = properties.map(property => read(scratch, { property, itemId, before: null, after: null }));
    if (!applyChange(scratch, change)) continue;
    properties.forEach((property, index) => {
      const after = read(scratch, { property, itemId, before: null, after: null });
      // No step for a property of an item the same batch creates: its alive step undoes it. A colour the item did not have is
      // undone by an empty one (write).
      if (same(before[index], after) || (property !== "alive" && before[index] === undefined)
        || (property.startsWith("setting:") && before[index] === null)) return;
      steps.push({ property, itemId, before: property === "alive" ? before[index] ?? false : before[index], after });
    });
  }
  return steps;
}

/** What an undo or redo does: the board's changes it queued (under `operationId`) and the object edits the caller writes. */
export interface UndoStep { readonly operationId: string; readonly changes: readonly BoardEdit[] }

/** `readObject`: a field of an object as it is now (null when it has none), so an undo of an object edit skips one changed since. */
export function createBoardModel(snapshot: BoardSnapshot, options: { newId?: () => string; readObject?: (objectId: string, key: string) => unknown } = {}) {
  const newId = options.newId ?? (() => crypto.randomUUID());
  let confirmed = stateOf(snapshot);
  let sequence = snapshot.sequence;
  let pending: PendingBatch[] = [];
  let view: State | undefined;
  let shown: BoardItem[] | undefined;
  const undoStack: Entry[] = [];
  const redoStack: Entry[] = [];

  function current(): State {
    if (!view) {
      view = copyState(confirmed);
      for (const batch of pending) for (const change of batch.changes) applyChange(view, change);
    }
    return view;
  }
  function changed() { view = undefined; shown = undefined; }
  function enqueue(changes: readonly BoardChange[]): PendingBatch {
    const batch: PendingBatch = { operationId: newId(), changes };
    pending.push(batch);
    changed();
    return batch;
  }
  /** Applies a batch the server ordered as `sequence` to the confirmed state; false when it referred to an unknown item. */
  function confirm(changes: readonly BoardChange[], next: number) {
    let fits = true;
    for (const change of changes) fits = applyChange(confirmed, change) && fits;
    sequence = next;
    changed();
    return fits;
  }
  /** Writes the steps back from one side to the other, against the current view, skipping every step that no longer holds. */
  function revert(entry: Entry, from: "before" | "after", to: "before" | "after") {
    const scratch = copyState(current());
    const changes: BoardEdit[] = [];
    for (const step of from === "after" ? [...entry.steps].reverse() : entry.steps) {
      if (step.objectId) {
        const key = step.property.slice("object:".length);
        if (options.readObject && !same(options.readObject(step.objectId, key) ?? null, step[from])) continue;
        // One edit per object, all its fields together.
        const edit = changes.find((change): change is ObjectEdit => isObjectEdit(change) && change.objectId === step.objectId);
        if (edit) { edit.before[key] = step[from]; edit.after[key] = step[to]; }
        else changes.push({ type: "object", itemId: step.itemId, objectId: step.objectId, before: { [key]: step[from] }, after: { [key]: step[to] } });
        continue;
      }
      const now = read(scratch, step);
      if (now === undefined || !same(now, step[from])) continue;
      const change = write(step, step[to]);
      applyChange(scratch, change);
      changes.push(change);
    }
    return changes;
  }
  function travel(source: Entry[], target: Entry[], from: "before" | "after", to: "before" | "after"): UndoStep | null {
    while (source.length) {
      const entry = source.pop()!;
      const changes = revert(entry, from, to);
      // Nothing of the gesture is still as it left it (someone else deleted or changed it): skip to the one before.
      if (!changes.length) continue;
      const board = changes.filter((change): change is BoardChange => !isObjectEdit(change));
      if (board.length) entry.operationId = enqueue(board).operationId;
      target.push(entry);
      return { operationId: entry.operationId, changes };
    }
    return null;
  }

  return {
    get title() { return current().title; },
    get settings(): BoardSettings { return current().settings; },
    /** The last confirmed sequence. */
    get sequence() { return sequence; },
    get pending(): readonly PendingBatch[] { return pending; },
    /** The visible items of the optimistic view, bottom to top. */
    get items(): readonly BoardItem[] {
      const state = current();
      shown ??= [...state.items.values()].filter(item => visible(state, item)).sort((a, b) => a.z - b.z || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
      return shown;
    },
    get canUndo() { return undoStack.length > 0; },
    get canRedo() { return redoStack.length > 0; },

    /**
     * One local gesture: one batch, one undo step; a gesture of the same `coalesce` as the last step joins that step. Its object
     * edits are only steps of the undo (the object path wrote them): the batch, null without board changes, carries the rest.
     */
    apply(changes: readonly BoardEdit[], coalesce?: string): PendingBatch | null {
      if (!changes.length) return null;
      const steps = stepsOf(current(), changes);
      const board = changes.filter((change): change is BoardChange => !isObjectEdit(change));
      const batch = board.length ? enqueue(board) : null;
      const last = undoStack.at(-1);
      if (coalesce && steps.length && last?.coalesce === coalesce && !redoStack.length) {
        for (const step of steps) {
          const known = last.steps.find(entry => entry.itemId === step.itemId && entry.property === step.property);
          if (known) known.after = step.after; else last.steps.push(step);
        }
        if (batch) last.operationId = batch.operationId;
      } else if (steps.length) undoStack.push({ steps, operationId: batch?.operationId ?? newId(), ...(coalesce && { coalesce }) });
      redoStack.length = 0;
      return batch;
    },
    /**
     * Merges these unsent batches, each run of them in the queue, into as few batches as `limit` changes allow, under the first
     * one's operation id (the view stays the same: the changes keep their order). The merged batches, for one request each.
     */
    coalesce(operationIds: readonly string[], limit: number): PendingBatch[] {
      const merging = new Set(operationIds), renamed = new Map<string, string>(), next: PendingBatch[] = [];
      let open: { operationId: string; changes: BoardChange[] } | undefined;
      for (const batch of pending) {
        if (!merging.has(batch.operationId) || batch.acknowledged !== undefined) { open = undefined; next.push(batch); continue; }
        if (open && open.changes.length + batch.changes.length <= limit) { open.changes.push(...batch.changes); renamed.set(batch.operationId, open.operationId); continue; }
        next.push(open = { operationId: batch.operationId, changes: [...batch.changes] });
      }
      pending = next;
      for (const entry of [...undoStack, ...redoStack]) entry.operationId = renamed.get(entry.operationId) ?? entry.operationId;
      return next.filter(batch => merging.has(batch.operationId));
    },
    acknowledge(operationId: string, ack: BoardAck): AckResult {
      const index = pending.findIndex(batch => batch.operationId === operationId);
      // Already confirmed: echoed by the hub or contained in a reloaded snapshot (also a duplicate of an earlier send).
      if (index < 0) return "confirmed";
      if (ack.sequence <= sequence) { pending.splice(index, 1); changed(); return "confirmed"; }
      if (ack.sequence === sequence + 1) {
        const [batch] = pending.splice(index, 1);
        return confirm(batch!.changes, ack.sequence) ? "confirmed" : "reload";
      }
      pending[index] = { ...pending[index]!, acknowledged: ack.sequence };
      return "gap";
    },
    /** The server refused the batch (409, 400): it leaves the view and the history. The session reloads after it. */
    reject(operationId: string) {
      pending = pending.filter(batch => batch.operationId !== operationId);
      for (const stack of [undoStack, redoStack]) for (let index = stack.length - 1; index >= 0; index--) if (stack[index]!.operationId === operationId) stack.splice(index, 1);
      changed();
    },
    receive(batch: BoardBatch): ReceiveResult {
      if (batch.sequence <= sequence) return "stale";
      if (batch.sequence > sequence + 1) return "gap";
      // Our own batch echoed by the hub: it becomes confirmed here and is not replayed on top a second time.
      pending = pending.filter(item => item.operationId !== batch.operationId);
      return confirm(batch.changes, batch.sequence) ? "applied" : "reload";
    },
    /**
     * A full reload (resync, rejection). Unanswered batches stay pending on top: one the server already applied replays as the
     * same values. Batches the snapshot already contains go.
     * shortcut: items deleted before the snapshot are not in it, so an undo of their deletion is skipped; keep deleted records across reloads if users miss it.
     */
    reset(next: BoardSnapshot) {
      confirmed = stateOf(next);
      sequence = next.sequence;
      pending = pending.filter(batch => batch.acknowledged === undefined || batch.acknowledged > next.sequence);
      changed();
    },
    undo: () => travel(undoStack, redoStack, "after", "before"),
    redo: () => travel(redoStack, undoStack, "before", "after"),
  };
}
export type BoardModel = ReturnType<typeof createBoardModel>;
