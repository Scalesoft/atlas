import type { Geometry } from "./boardApi";
import { boundsOf, centreOf, geometryOf } from "./geometry";
import { COLLAPSED_GROUP, collapsedOwners, parentOf } from "./boardGroups";
import type { BoardItem } from "./boardModel";

/**
 * When and how a board item moves (interaction spec §5), decided from what was drawn and what is drawn now; the canvas runs it
 * on the Atlas tokens (motion/aiMotion.ts). A new item enters, a removed one leaves (the model is not held up: the node only
 * outlives it on screen), an item moved by someone else, by undo or by align glides there, or fades under reduced motion. What
 * follows my own pointer, a change of size alone and the items of a board that has just opened do not animate. My own new
 * items enter one after another (`order` × 40 ms, the first eight), so do up to eight of someone else's; a larger batch of
 * someone else's enters at once.
 */
export type MotionKind = "enter" | "leave" | "glide" | "fade" | "gather" | "freed" | "tuck" | "untuck" | "refit";
/**
 * `variant` shapes an enter or leave (a group's outline draws in or dissolves); `from` is the box a drawing or refitting outline
 * starts from, `toward` the offset from an item to the card of its collapsing or expanding group.
 */
export interface ItemMotion { kind: MotionKind; order: number; variant?: "draw" | "dissolve"; from?: Geometry; toward?: { x: number; y: number } }

const STAGGERED = 8;

export function planMotion(
  prev: ReadonlyMap<string, Geometry> | null,
  next: ReadonlyMap<string, Geometry>,
  options: { local: ReadonlySet<string>; reduced: boolean; own: boolean },
): Map<string, ItemMotion> {
  const motion = new Map<string, ItemMotion>();
  if (!prev) return motion;
  const entering = [...next.keys()].filter(id => !prev.has(id));
  const stagger = options.own || entering.length <= STAGGERED;
  entering.forEach((id, index) => motion.set(id, { kind: "enter", order: stagger ? Math.min(index, STAGGERED - 1) : 0 }));
  for (const id of prev.keys()) if (!next.has(id)) motion.set(id, { kind: "leave", order: 0 });
  for (const [id, g] of next) {
    const before = prev.get(id);
    if (!before || options.local.has(id) || (before.x === g.x && before.y === g.y)) continue;
    motion.set(id, { kind: options.reduced ? "fade" : "glide", order: 0 });
  }
  return motion;
}

const membersOf = (items: ReadonlyMap<string, BoardItem>, groupId: string) =>
  [...items.values()].filter(item => item.kind !== "connector" && parentOf(item, items)?.id === groupId);
const sameIds = (a: readonly BoardItem[], b: readonly BoardItem[]) => a.length === b.length && a.every(item => b.some(other => other.id === item.id));

/**
 * Which animation shows what happened to a group (interaction spec §5), from the items drawn before and now, whoever changed them:
 * a new group's outline draws in from its members' bounds and the members gather; an ungrouped one dissolves and its members light
 * up; collapsing tucks the members into the group's card, expanding brings them out one after another (40 ms, the first eight) and
 * draws their connectors again; a member joining or leaving refits the outline from where it was, so does a frame that grew
 * around a member (J1) unless it is in `local` (my own resize, which follows the pointer). A group only moved is left to
 * planMotion. The canvas lays this over planMotion; the model is never held up.
 */
export function planGroupMotion(prev: ReadonlyMap<string, BoardItem> | null, next: ReadonlyMap<string, BoardItem>, local: ReadonlySet<string> = new Set()): Map<string, ItemMotion> {
  const motion = new Map<string, ItemMotion>();
  if (!prev) return motion;
  const hiddenBefore = collapsedOwners([...prev.values()]), hiddenNow = collapsedOwners([...next.values()]);
  for (const before of prev.values()) {
    if (before.kind !== "group" || next.has(before.id)) continue;
    const freed = membersOf(prev, before.id).filter(member => next.has(member.id));
    if (!freed.length) continue;
    motion.set(before.id, { kind: "leave", order: 0, variant: "dissolve" });
    for (const member of freed) motion.set(member.id, { kind: "freed", order: 0 });
  }
  for (const group of next.values()) {
    const before = prev.get(group.id);
    if (group.kind === "frame" && before && !local.has(group.id)
      && (before.width !== group.width || before.height !== group.height || !sameIds(membersOf(prev, group.id), membersOf(next, group.id)))) {
      motion.set(group.id, { kind: "refit", order: 0, from: geometryOf(before) });
    }
    if (group.kind !== "group") continue;
    const members = membersOf(next, group.id);
    if (!before) {
      const gathered = members.filter(member => prev.has(member.id));
      if (!gathered.length) continue;
      motion.set(group.id, { kind: "enter", order: 0, variant: "draw", from: boundsOf(gathered.map(geometryOf)) });
      for (const member of gathered) motion.set(member.id, { kind: "gather", order: 0 });
      continue;
    }
    const card = centreOf({ x: group.x, y: group.y, ...COLLAPSED_GROUP });
    const toCard = (item: BoardItem) => { const centre = centreOf(item); return { x: card.x - centre.x, y: card.y - centre.y }; };
    const collapsed = group.props?.collapsed === true, wasCollapsed = before.props?.collapsed === true;
    if (collapsed && !wasCollapsed) {
      for (const item of next.values()) if (hiddenNow.get(item.id) === group.id && !hiddenBefore.has(item.id)) motion.set(item.id, { kind: "tuck", order: 0, toward: toCard(item) });
    } else if (!collapsed && wasCollapsed) {
      const shown = [...next.values()].filter(item => hiddenBefore.get(item.id) === group.id && !hiddenNow.has(item.id));
      shown.forEach((item, index) => motion.set(item.id, { kind: "untuck", order: Math.min(index, STAGGERED - 1), toward: toCard(item) }));
      // Their connectors, merged into the card while it was collapsed, split again: each draws in with the later of its ends.
      const orderOf = (id: string | undefined) => (id ? motion.get(id)?.order : undefined);
      for (const item of next.values()) {
        const ends = [orderOf(item.from), orderOf(item.to)].filter((order): order is number => order !== undefined);
        if (item.kind === "connector" && ends.length) motion.set(item.id, { kind: "enter", order: Math.max(...ends) });
      }
    } else if (!collapsed && (before.width !== group.width || before.height !== group.height || !sameIds(membersOf(prev, group.id), members))) {
      motion.set(group.id, { kind: "refit", order: 0, from: geometryOf(before) });
    }
  }
  return motion;
}
