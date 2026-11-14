import type { BoardColor } from "../boards/boardApi";
import { COLLAPSED_GROUP, GROUP_PADDING } from "../boards/boardGroups";
import type { BoardViewItem } from "../boards/boardSession";
import { layoutLayers } from "./layoutLayers";
import t from "../i18n/objects";
import { kindInfo, plainTitle, typeInfo, type RelationEdge, type RelationGraph, type RelationNode } from "./relationsApi";

/**
 * The relations of an object as derived items of a board: a card per
 * node, a connector per edge, laid out in layers (layoutLayers). Nothing is stored; BoardCanvas draws it read-only and brings
 * the neighbour highlighting, the path between two nodes, the merged connectors of collapsed groups and the motion with it.
 * A node the reader may not read is left out with its edges (the server sent no title); an edge to an id that is no node at all (or one gone, in the trash) is
 * a broken one and ends at the „Nedostupné“ node, red and dashed. Past `limit` nodes everything but the focus and its direct
 * neighbours folds into groups by branch of the tree (collapsed until clicked), so the browser never draws thousands of cards.
 */
export const NODE_SIZE = { width: 248, height: 96 };
export const GROUP_LIMIT = 150;
export const UNAVAILABLE_ID = "unavailable";
const GROUP_TITLE = 36;
const TYPE_COLOURS: Record<string, BoardColor | undefined> = { "atlas.page": undefined, "atlas.board": "teal", "atlas.contact": "pink" };

export interface RelationItemsOptions {
  /** The node the graph is about: kept out of groups, with its neighbours, and tinted. */
  focusId?: string;
  /** Ids (`group:<key>`) of the groups that are open. */
  expanded?: ReadonlySet<string>;
  limit?: number;
  /** The branch of the tree a node belongs to (the first part of its path in Obsah); its space's name when unknown. */
  branchOf?: (node: RelationNode) => string | undefined;
}

export const groupId = (key: string) => `group:${key}`;
export const isGroupId = (id: string) => id.startsWith("group:");
/** A name as one line of Markdown text that reads literally. */
const literal = (text: string) => text.replace(/([\\`*_{}[\]()#+!|<>~-])/g, "\\$1");

interface Prepared { nodes: RelationNode[]; edges: (RelationEdge & { broken?: boolean })[] }

/** The readable nodes, the edges between them and the broken ones, with the stand-in node when one is needed. */
export function prepare(graph: RelationGraph): Prepared {
  const nodes = new Map<string, RelationNode>();
  const hidden = new Set<string>();
  // The server sends no type (null) for a node that is gone or in the trash: its edge is a broken one, not a hidden one (the type says string, the wire says null).
  for (const node of graph.nodes) (node.readable ? nodes.set(node.id.toLowerCase(), node) : node.typeId && hidden.add(node.id.toLowerCase()));
  const edges: Prepared["edges"] = [];
  let broken = false;
  for (const edge of graph.edges) {
    const source = edge.sourceId.toLowerCase(), target = edge.targetId.toLowerCase();
    // A tree reference is a place for „Kde je použito“, not a line of the graph.
    if (edge.kind === "reference" || hidden.has(source) || hidden.has(target) || source === target || !nodes.has(source)) continue;
    if (nodes.has(target)) edges.push({ ...edge, sourceId: source, targetId: target });
    else { broken = true; edges.push({ ...edge, sourceId: source, targetId: UNAVAILABLE_ID, broken: true }); }
  }
  const list = [...nodes.values()].map(node => ({ ...node, id: node.id.toLowerCase() }));
  if (broken) list.push({ id: UNAVAILABLE_ID, typeId: UNAVAILABLE_ID, title: t.unavailable, spaceId: "", spaceName: "", readable: true });
  return { nodes: list, edges };
}

interface Cluster { id: string; key: string; members: string[] }

function clusters(prepared: Prepared, options: RelationItemsOptions): Cluster[] {
  const limit = options.limit ?? GROUP_LIMIT;
  if (prepared.nodes.length <= limit) return [];
  const kept = new Set<string>([UNAVAILABLE_ID]);
  const focus = options.focusId?.toLowerCase();
  if (focus) {
    kept.add(focus);
    for (const edge of prepared.edges) { if (edge.sourceId === focus) kept.add(edge.targetId); if (edge.targetId === focus) kept.add(edge.sourceId); }
  }
  const byKey = new Map<string, string[]>();
  for (const node of prepared.nodes) {
    if (kept.has(node.id)) continue;
    const key = options.branchOf?.(node) || node.spaceName || typeInfo(node.typeId).plural;
    byKey.set(key, [...(byKey.get(key) ?? []), node.id]);
  }
  return [...byKey].filter(([, members]) => members.length > 1).map(([key, members]) => ({ id: groupId(key), key, members }));
}

export function relationItems(graph: RelationGraph, options: RelationItemsOptions = {}): BoardViewItem[] {
  const prepared = prepare(graph);
  const focus = options.focusId?.toLowerCase();
  const groups = clusters(prepared, options);
  const clusterOf = new Map(groups.flatMap(group => group.members.map(id => [id, group] as const)));
  const expanded = options.expanded ?? new Set<string>();
  const nodeOf = new Map(prepared.nodes.map(node => [node.id, node]));
  const edgesBetween = (members: ReadonlySet<string>) => prepared.edges.filter(edge => members.has(edge.sourceId) && members.has(edge.targetId));

  // Inside a group the members are laid out on their own (so that opening it needs no further guess); the outer layout sees
  // the group as one card while closed and as its box while open.
  const inner = new Map(groups.map(group => {
    const members = new Set(group.members);
    const layout = layoutLayers(group.members.map(id => ({ id, ...NODE_SIZE })), edgesBetween(members).map(edge => ({ from: edge.sourceId, to: edge.targetId })), { wrap: 6, gapLayer: 64, gapNode: 20 });
    return [group.id, { layout, width: layout.width + GROUP_PADDING * 2, height: layout.height + GROUP_PADDING * 2 + GROUP_TITLE }] as const;
  }));
  const outerId = (id: string) => clusterOf.get(id)?.id ?? id;
  const outerNodes = [
    ...prepared.nodes.filter(node => !clusterOf.has(node.id)).map(node => ({ id: node.id, ...NODE_SIZE })),
    ...groups.map(group => (expanded.has(group.id) ? { id: group.id, width: inner.get(group.id)!.width, height: inner.get(group.id)!.height } : { id: group.id, ...COLLAPSED_GROUP })),
  ];
  const seen = new Set<string>();
  const outerEdges = prepared.edges.map(edge => ({ from: outerId(edge.sourceId), to: outerId(edge.targetId) }))
    .filter(edge => edge.from !== edge.to && !seen.has(`${edge.from}>${edge.to}`) && !!seen.add(`${edge.from}>${edge.to}`));
  const outer = layoutLayers(outerNodes, outerEdges, { wrap: 8, gapLayer: 120, gapNode: 28 });

  const items: BoardViewItem[] = [];
  const card = (node: RelationNode, x: number, y: number, parent?: string): BoardViewItem => {
    const unavailable = node.id === UNAVAILABLE_ID;
    const type = typeInfo(node.typeId);
    return {
      id: node.id, kind: "page", x, y, ...NODE_SIZE, z: 2, targetResourceId: node.id, props: { display: "card" },
      color: unavailable ? "red" : node.id === focus ? "yellow" : TYPE_COLOURS[node.typeId] ?? (node.typeId in TYPE_COLOURS ? undefined : "gray"),
      ...(parent && { parent }),
      target: { typeId: node.typeId, title: plainTitle(node.title) || t.untitled, available: true, spaceTitle: unavailable ? t.leadsNowhere : [type.label, node.spaceName].filter(Boolean).join(" · ") },
    };
  };
  for (const node of prepared.nodes) {
    const group = clusterOf.get(node.id);
    if (!group) { const at = outer.positions.get(node.id)!; items.push(card(node, at.x, at.y)); continue; }
    const box = outer.positions.get(group.id)!, place = inner.get(group.id)!.layout.positions.get(node.id)!;
    items.push(card(node, box.x + GROUP_PADDING + place.x, box.y + GROUP_PADDING + GROUP_TITLE + place.y, group.id));
  }
  for (const group of groups) {
    const box = outer.positions.get(group.id)!, size = inner.get(group.id)!;
    items.push({
      id: group.id, kind: "group", x: box.x, y: box.y, width: size.width, height: size.height, z: 1, color: "gray",
      text: `${literal(group.key)} · ${group.members.length}`, props: { collapsed: !expanded.has(group.id) },
    });
  }
  const placed = new Map(items.map(item => [item.id, item]));
  for (const edge of prepared.edges) {
    const info = kindInfo[edge.kind];
    // A line that runs with the layers leaves its card on the right and enters the next one on the left; a back edge finds its own way.
    const from = placed.get(edge.sourceId), to = placed.get(edge.targetId);
    const forward = !!from && !!to && !from.parent && !to.parent && to.x >= from.x + from.width;
    items.push({
      id: `edge:${edge.kind}:${edge.sourceId}>${edge.targetId}`, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: edge.sourceId, to: edge.targetId,
      text: edge.broken ? t.brokenLink : `${info.label}${edge.count > 1 ? ` ×${edge.count}` : ""}`, color: edge.broken ? "red" : info.colour,
      props: { route: "curved", arrowStart: "none", arrowEnd: "arrow", dash: edge.broken ? "dashed" : "solid", ...(forward && { fromSide: "right" as const, toSide: "left" as const }), ...(edge.broken && { state: "blocked" as const }) },
    });
  }
  return items;
}

/** Where an item of the graph leads: a node's id, or null for a group, the stand-in node and connectors. */
export const openableId = (id: string) => (isGroupId(id) || id === UNAVAILABLE_ID || id.startsWith("edge:") ? null : id);
