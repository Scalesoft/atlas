import type { BoardProps } from "./boardApi";

/**
 * Neighbour highlighting and path focus (objects spec §5.3, Projector's DependencyView): hovering a node marks what leads into it
 * (`up`) and what it leads to (`down`), one hop or, with Shift, the whole chain; two selected nodes mark the shortest chain between
 * them. Everything unmarked dims. Pure functions over directed links; typed relations will feed the same links later.
 */
export interface GraphLink { id: string; from: string; to: string }
/** `keep`: left as it is while the rest dims (a filter of the board's head, U4-1). */
export type NodeMark = "self" | "up" | "down" | "path" | "keep";
export type LinkMark = "up" | "down" | "path";
export interface Highlight { nodes: ReadonlyMap<string, NodeMark>; links: ReadonlyMap<string, LinkMark> }

/** A connector as a link: from its start to its end, reversed when only its start carries the arrow. */
export function linkOf(connector: { id: string; from: string; to: string; props?: BoardProps }): GraphLink {
  const backward = connector.props?.arrowStart === "arrow" && connector.props.arrowEnd === "none";
  return backward ? { id: connector.id, from: connector.to, to: connector.from } : { id: connector.id, from: connector.from, to: connector.to };
}

/** Walks from `focus` against (`up`) or along (`down`) the links, one hop or all of them; the links walked and the nodes reached. */
function walk(links: readonly GraphLink[], focus: string, direction: LinkMark, transitive: boolean) {
  const reached = new Set<string>(), walked = new Set<string>();
  let frontier = new Set([focus]);
  while (frontier.size) {
    const next = new Set<string>();
    for (const link of links) {
      const [near, far] = direction === "up" ? [link.to, link.from] : [link.from, link.to];
      if (!frontier.has(near)) continue;
      walked.add(link.id);
      if (far !== focus && !reached.has(far)) { reached.add(far); next.add(far); }
    }
    frontier = transitive ? next : new Set();
  }
  return { reached, walked };
}

export function neighbourhood(links: readonly GraphLink[], focus: string, transitive: boolean): Highlight {
  const up = walk(links, focus, "up", transitive), down = walk(links, focus, "down", transitive);
  const nodes = new Map<string, NodeMark>([[focus, "self"]]);
  const marks = new Map<string, LinkMark>();
  for (const [mark, found] of [["up", up], ["down", down]] as const) {
    for (const id of found.reached) if (!nodes.has(id)) nodes.set(id, mark);
    for (const id of found.walked) if (!marks.has(id)) marks.set(id, mark);
  }
  return { nodes, links: marks };
}

/** The shortest chain of links between two nodes, in either direction; null when they are the same or not connected. */
export function pathBetween(links: readonly GraphLink[], from: string, to: string): Highlight | null {
  if (from === to) return null;
  const seen = new Set([from]);
  const via = new Map<string, GraphLink>();
  let frontier = new Set([from]);
  while (frontier.size && !seen.has(to)) {
    const next = new Set<string>();
    for (const link of links) {
      for (const [near, far] of [[link.from, link.to], [link.to, link.from]] as const) {
        if (frontier.has(near) && !seen.has(far)) { seen.add(far); via.set(far, link); next.add(far); }
      }
    }
    frontier = next;
  }
  if (!seen.has(to)) return null;
  const nodes = new Map<string, NodeMark>([[to, "path"]]);
  const marks = new Map<string, LinkMark>();
  for (let at = to; at !== from;) {
    const link = via.get(at)!;
    marks.set(link.id, "path");
    at = link.from === at ? link.to : link.from;
    nodes.set(at, "path");
  }
  return { nodes, links: marks };
}
