import type { BoardChange, Geometry } from "./boardApi";
import { boundsOf, centreOf } from "./geometry";
import type { BoardItem } from "./boardModel";

/**
 * Groups and frames with explicit membership (objects spec §5.4): an item belongs to the container its `parent` names. Members
 * move with their container; an item dropped or created over a container joins the innermost one under its centre and leaves it
 * when dragged out (its centre outside: more than half of it); a group's box always wraps its members, a frame grows to take in a
 * member that stayed but sticks out. A collapsed group stands for everything inside it. Pure functions,
 * no Vue; the server checks the same nesting rules (BoardEditing).
 */
export const MAX_DEPTH = 4;
/** The room between a group's outline and its members. */
export const GROUP_PADDING = 16;
/** The card a collapsed group shows at its top left corner; its box keeps the open size. */
export const COLLAPSED_GROUP = { width: 240, height: 64 };

const isContainer = (item: BoardItem) => item.kind === "frame" || item.kind === "group";
const isCollapsed = (item: BoardItem) => item.kind === "group" && item.props?.collapsed === true;
const contains = (box: Geometry, point: { x: number; y: number }) =>
  point.x >= box.x && point.x <= box.x + box.width && point.y >= box.y && point.y <= box.y + box.height;
const sameGeometry = (a: Geometry, b: Geometry) => a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;

/** The item's group or frame while that container is live on the board; a deleted or unknown one holds nothing. */
export function parentOf(item: BoardItem, byId: ReadonlyMap<string, BoardItem>): BoardItem | undefined {
  const parent = item.parent ? byId.get(item.parent) : undefined;
  return parent && !parent.deleted && isContainer(parent) ? parent : undefined;
}

/** The live containers above the item, nearest first; bounded, so a cycle in foreign data cannot hang it. */
function ancestorsOf(item: BoardItem, byId: ReadonlyMap<string, BoardItem>): BoardItem[] {
  const chain: BoardItem[] = [];
  for (let parent = parentOf(item, byId); parent && chain.length <= MAX_DEPTH + 1 && !chain.includes(parent); parent = parentOf(parent, byId)) chain.push(parent);
  return chain;
}

function membersByParent(items: readonly BoardItem[]): Map<string, BoardItem[]> {
  const byId = new Map(items.map(item => [item.id, item]));
  const members = new Map<string, BoardItem[]>();
  for (const item of items) {
    const parent = !item.deleted && item.kind !== "connector" ? parentOf(item, byId) : undefined;
    if (parent) members.set(parent.id, [...(members.get(parent.id) ?? []), item]);
  }
  return members;
}

/** How many levels of members lie under the item (0 when it holds nothing). */
function heightOf(id: string, members: ReadonlyMap<string, readonly BoardItem[]>, depth = 0): number {
  const below = members.get(id) ?? [];
  return below.length && depth <= MAX_DEPTH ? 1 + Math.max(...below.map(member => heightOf(member.id, members, depth + 1))) : 0;
}

/** The moving ids followed by every member of every moving container, transitively, each id once. */
export function withMembers(moving: readonly string[], items: readonly BoardItem[]): string[] {
  const members = membersByParent(items);
  const result = [...new Set(moving)];
  for (let index = 0; index < result.length; index++) {
    for (const member of members.get(result[index]!) ?? []) if (!result.includes(member.id)) result.push(member.id);
  }
  return result;
}

/** The live direct members of a container, in board order: „Vybrat obsah“ and Ctrl+A on one selected frame or group. */
export function membersOf(id: string, items: readonly BoardItem[]): string[] {
  return membersByParent(items).get(id)?.map(member => member.id) ?? [];
}

/** Esc on a selection: the container all of it shares (from a card to its frame, from a frame to the one holding it), else none. */
export function escapeSelection(ids: readonly string[], items: readonly BoardItem[]): string[] {
  const byId = new Map(items.map(item => [item.id, item]));
  const parents = new Set(ids.map(id => { const item = byId.get(id); return item ? parentOf(item, byId)?.id : undefined; }));
  const [shared] = parents;
  return ids.length && parents.size === 1 && shared ? [shared] : [];
}

/** The innermost open frame or group under a point of the board, its outline widened by `slack` (a click on its border). */
export function containerAt(items: readonly BoardItem[], point: { x: number; y: number }, slack = 0): string | undefined {
  const owners = collapsedOwners(items);
  return items
    .filter(item => isContainer(item) && !item.deleted && !isCollapsed(item) && !owners.has(item.id)
      && contains({ x: item.x - slack, y: item.y - slack, width: item.width + 2 * slack, height: item.height + 2 * slack }, point))
    .sort((p, q) => p.width * p.height - q.width * q.height || q.z - p.z)[0]?.id;
}

/** The chosen items that are not inside another chosen one: what a gesture on the selection acts on. */
function rootsOf(items: readonly BoardItem[], ids: ReadonlySet<string>, byId: ReadonlyMap<string, BoardItem>): BoardItem[] {
  return items.filter(item => ids.has(item.id) && !item.deleted && item.kind !== "connector" && !ancestorsOf(item, byId).some(parent => ids.has(parent.id)));
}

const padded = (box: Geometry): Geometry =>
  ({ x: box.x - GROUP_PADDING, y: box.y - GROUP_PADDING, width: box.width + 2 * GROUP_PADDING, height: box.height + 2 * GROUP_PADDING });

/**
 * Ctrl+G: a new group around the selection (its outermost items), inside the container they all share, if any. None for fewer than
 * two outermost items or when the group would put something deeper than {@link MAX_DEPTH} levels.
 */
export function groupChanges(items: readonly BoardItem[], ids: readonly string[], id: string, z: number): BoardChange[] {
  const byId = new Map(items.map(item => [item.id, item]));
  const roots = rootsOf(items, new Set(ids), byId);
  if (roots.length < 2) return [];
  const parents = new Set(roots.map(root => parentOf(root, byId)));
  const shared = parents.size === 1 ? [...parents][0] : undefined;
  const above = shared ? ancestorsOf(shared, byId).length + 1 : 0;
  const members = membersByParent(items);
  if (roots.some(root => above + 2 + heightOf(root.id, members) > MAX_DEPTH + 1)) return [];
  return [
    { type: "create", item: { id, kind: "group", ...padded(boundsOf(roots)), z, text: "", color: "blue", props: {} } },
    ...(shared ? [{ type: "parent", itemId: id, parentId: shared.id } as const] : []),
    ...roots.map(root => ({ type: "parent", itemId: root.id, parentId: id }) as const),
  ];
}

/** Ctrl+Shift+G: every chosen group lets its members go to its own container and is deleted. */
export function ungroupChanges(items: readonly BoardItem[], ids: readonly string[]): BoardChange[] {
  const byId = new Map(items.map(item => [item.id, item]));
  const members = membersByParent(items);
  return items.filter(item => ids.includes(item.id) && item.kind === "group" && !item.deleted).flatMap(group => {
    const parent = parentOf(group, byId);
    return [
      ...(members.get(group.id) ?? []).map(member => (parent ? { type: "parent", itemId: member.id, parentId: parent.id } : { type: "parent", itemId: member.id }) as BoardChange),
      { type: "delete", itemId: group.id } as const,
    ];
  });
}

/** The items as a batch leaves them: created, moved, deleted, moved into or out of containers, restyled. */
function project(items: readonly BoardItem[], changes: readonly BoardChange[]): Map<string, BoardItem> {
  const result = new Map(items.map(item => [item.id, item]));
  for (const change of changes) {
    if (change.type === "create") { result.set(change.item.id, { ...change.item }); continue; }
    if (change.type === "rename" || change.type === "settings") continue;
    const item = result.get(change.itemId);
    if (!item) continue;
    if (change.type === "move") result.set(item.id, { ...item, x: change.x, y: change.y, width: change.width, height: change.height });
    else if (change.type === "parent") { const { parent: _old, ...rest } = item; result.set(item.id, change.parentId ? { ...rest, parent: change.parentId } : rest); }
    else if (change.type === "delete") result.set(item.id, { ...item, deleted: true });
    else if (change.type === "restore") result.set(item.id, { ...item, deleted: false });
    else if (change.type === "set" && change.props) result.set(item.id, { ...item, props: { ...item.props, ...change.props } as BoardItem["props"] });
  }
  return result;
}

/** The innermost live container under the item's centre that is not collapsed, not in `exclude` (moving along), not inside the
 * item itself and still within {@link MAX_DEPTH} with what the item holds. */
function containerFor(item: BoardItem, after: ReadonlyMap<string, BoardItem>, exclude: ReadonlySet<string>, height: number): BoardItem | undefined {
  const centre = centreOf(item);
  return [...after.values()]
    .filter(other => isContainer(other) && !other.deleted && other.id !== item.id && !exclude.has(other.id) && !isCollapsed(other) && contains(other, centre)
      && !ancestorsOf(other, after).some(above => exclude.has(above.id) || above.id === item.id) && ancestorsOf(other, after).length + 1 + height <= MAX_DEPTH)
    .sort((p, q) => p.width * p.height - q.width * q.height || q.z - p.z)[0];
}

/**
 * A batch with what it implies for membership: every item it creates (unless it already names a container for it) and every root
 * of `moved` (a drag, a nudge) joins the innermost container under its centre ({@link containerFor}) or leaves the one it was in;
 * so does every member of a container the batch resized. Then every group the batch touched wraps its members again, and every
 * frame grows (never shrinks) to wrap a member that stayed in it but sticks out: no member sits half outside (J1).
 */
export function arrangeChanges(items: readonly BoardItem[], changes: readonly BoardChange[], moved: readonly string[] = []): BoardChange[] {
  const before = new Map(items.map(item => [item.id, item]));
  let after = project(items, changes);
  const placed = new Set(changes.flatMap(change => (change.type === "parent" ? [change.itemId] : [])));
  const created = changes.flatMap(change => (change.type === "create" && change.item.kind !== "connector" && !placed.has(change.item.id) ? [change.item.id] : []));
  const moving = new Set([...moved, ...created]);
  // A frame made smaller (or larger) re-decides its members; it does not grow back over what it was shrunk away from.
  const resized = new Set([...moving].filter(id => {
    const was = before.get(id), now = after.get(id);
    return !!was && !!now && isContainer(now) && (was.width !== now.width || was.height !== now.height);
  }));
  const members = membersByParent([...after.values()]);
  const candidates = [
    ...rootsOf([...after.values()], moving, after).map(item => ({ item, exclude: moving as ReadonlySet<string> })),
    ...[...resized].flatMap(id => (members.get(id) ?? []).filter(member => !moving.has(member.id))
      .map(member => ({ item: member, exclude: new Set([...moving].filter(other => other !== id)) as ReadonlySet<string> }))),
  ];
  const joins: BoardChange[] = [];
  for (const { item, exclude } of candidates) {
    if (placed.has(item.id)) continue;
    const current = parentOf(item, after);
    const target = containerFor(item, after, exclude, heightOf(item.id, members));
    if (target?.id === current?.id) continue;
    joins.push(target ? { type: "parent", itemId: item.id, parentId: target.id } : { type: "parent", itemId: item.id });
  }
  after = project([...after.values()], joins);
  // A new frame drawn over loose items takes them in: those whose centre it covers and that belong where it now belongs.
  const owners = collapsedOwners([...after.values()]);
  for (const frame of created.map(id => after.get(id)!).filter(item => item.kind === "frame")) {
    const home = parentOf(frame, after)?.id, above = ancestorsOf(frame, after).length;
    for (const other of after.values()) {
      if (other.deleted || other.kind === "connector" || moving.has(other.id) || owners.has(other.id) || parentOf(other, after)?.id !== home
        || other.width * other.height >= frame.width * frame.height || !contains(frame, centreOf(other))
        || above + 1 + heightOf(other.id, members) > MAX_DEPTH) continue;
      joins.push({ type: "parent", itemId: other.id, parentId: frame.id });
    }
  }
  after = project([...after.values()], joins);
  return [...changes, ...joins, ...fitChanges(before, after, [...changes, ...joins], resized)];
}

/**
 * The containers the dragged items (their geometry under the gesture) would belong to if dropped now, as arrangeChanges decides
 * it: what the canvas highlights as the drop target. Empty when everything would go loose onto the board.
 */
export function dropTargets(items: readonly BoardItem[], local: ReadonlyMap<string, Geometry>): Set<string> {
  return dropTargetsOf(items, [...local.keys()])(local);
}

/**
 * {@link dropTargets} for one gesture: what can hold the moving items (`moving`, their members included) is worked out once, each frame
 * of the drag only finds the innermost one under each moved root's centre, as {@link containerFor} would (arranging the whole board on
 * every frame cost ~63 ms per drag of 50 cards on a large board). Moving changes no membership, so the candidates hold for the gesture.
 */
export function dropTargetsOf(items: readonly BoardItem[], moving: readonly string[]): (local: ReadonlyMap<string, Geometry>) => Set<string> {
  const byId = new Map(items.map(item => [item.id, item]));
  const ids = new Set(moving), members = membersByParent(items);
  const roots = rootsOf(items, ids, byId).map(root => ({ root, height: heightOf(root.id, members) }));
  const candidates = items
    .filter(other => isContainer(other) && !other.deleted && !ids.has(other.id) && !isCollapsed(other))
    .map(other => ({ other, above: ancestorsOf(other, byId) }))
    .filter(({ above }) => !above.some(parent => ids.has(parent.id)))
    .sort((p, q) => p.other.width * p.other.height - q.other.width * q.other.height || q.other.z - p.other.z);
  return local => {
    const targets = new Set<string>();
    for (const { root, height } of roots) {
      const centre = centreOf(local.get(root.id) ?? root);
      const target = candidates.find(({ other, above }) => above.length + 1 + height <= MAX_DEPTH && contains(other, centre));
      if (target) targets.add(target.other.id);
    }
    return targets;
  };
}

/**
 * Moves that make every group the changes touched (an item's old and new containers, upwards) wrap its members and every such
 * frame (unless the batch resized it) grow around its members, innermost first.
 */
function fitChanges(before: ReadonlyMap<string, BoardItem>, after: Map<string, BoardItem>, changes: readonly BoardChange[], resized: ReadonlySet<string> = new Set()): BoardChange[] {
  const touched = new Set(changes.flatMap(change => (change.type === "create" ? [change.item.id] : change.type === "rename" || change.type === "settings" ? [] : [change.itemId])));
  const affected = new Set<BoardItem>();
  for (const id of touched) {
    for (const map of [before, after]) {
      const item = map.get(id);
      if (item) for (const parent of [...(item.kind === "group" ? [item] : []), ...ancestorsOf(item, after)]) affected.add(after.get(parent.id) ?? parent);
    }
  }
  const groups = [...affected].filter(item => isContainer(item) && !item.deleted && !resized.has(item.id)).sort((p, q) => ancestorsOf(q, after).length - ancestorsOf(p, after).length);
  const fits: BoardChange[] = [];
  for (const group of groups) {
    const members = [...after.values()].filter(item => !item.deleted && item.kind !== "connector" && parentOf(item, after)?.id === group.id);
    if (!members.length) continue;
    const current = after.get(group.id)!;
    const wrapped = padded(boundsOf(members.map(member => after.get(member.id)!)));
    // A group is its members' outline; a frame is an area its author sized, which only grows to take in what sticks out.
    const box = group.kind === "group" ? wrapped : boundsOf([current, wrapped]);
    if (sameGeometry(box, current)) continue;
    after.set(group.id, { ...current, ...box });
    fits.push({ type: "move", itemId: group.id, ...box });
  }
  return fits;
}

/** What a collapsed group hides: each member (transitively) and the outermost collapsed group shown in its place. */
export function collapsedOwners(items: readonly BoardItem[]): Map<string, string> {
  const byId = new Map(items.map(item => [item.id, item]));
  const owners = new Map<string, string>();
  for (const item of items) {
    if (item.deleted || item.kind === "connector") continue;
    const owner = ancestorsOf(item, byId).filter(isCollapsed).at(-1);
    if (owner) owners.set(item.id, owner.id);
  }
  return owners;
}

/** How many live containers hold the item. */
export function depthOf(item: BoardItem, byId: ReadonlyMap<string, BoardItem>): number {
  return ancestorsOf(item, byId).length;
}

/** Copies (duplicate, paste) inside the copies of their containers: `copies` maps an original id to its copy's. */
export function copiedParents(originals: readonly BoardItem[], copies: ReadonlyMap<string, string>): BoardChange[] {
  return originals.flatMap(item => {
    const copy = copies.get(item.id), parent = item.parent ? copies.get(item.parent) : undefined;
    return copy && parent ? [{ type: "parent", itemId: copy, parentId: parent } as const] : [];
  });
}
