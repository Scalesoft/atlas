/**
 * The layered layout of a directed graph, left to right (the relations graph draws with it): a node sits one layer after the last
 * node that leads into it, a cycle is cut where the walk meets it again (never loops), and a layer wider than `wrap` nodes
 * continues in a further column so that a hub with a hundred neighbours stays a readable block instead of a skyscraper. No Vue, no DOM.
 */
export interface LayerNode { id: string; width: number; height: number }
export interface LayerEdge { from: string; to: string }
export interface LayerOptions {
  /** Space between layers and between nodes of one layer. */
  gapLayer?: number; gapNode?: number;
  /** Most nodes in one column of a layer. */
  wrap?: number;
}
export interface Layout { positions: Map<string, { x: number; y: number }>; width: number; height: number; layers: string[][] }

/** Longest-path layers over the edges that are no back edges of a depth-first walk (the walk starts at nodes in the given order). */
export function layerOf(nodes: readonly LayerNode[], edges: readonly LayerEdge[]): Map<string, number> {
  const known = new Set(nodes.map(node => node.id));
  const out = new Map<string, string[]>(nodes.map(node => [node.id, []]));
  for (const { from, to } of edges) if (from !== to && known.has(from) && known.has(to)) out.get(from)!.push(to);
  // Cut the back edges: an edge to a node still on the walk's stack closes a cycle.
  const state = new Map<string, 1 | 2>();
  const forward = new Map<string, string[]>(nodes.map(node => [node.id, []]));
  const walk = (start: string) => {
    const stack: { id: string; next: number }[] = [{ id: start, next: 0 }];
    state.set(start, 1);
    while (stack.length) {
      const top = stack.at(-1)!;
      const targets = out.get(top.id)!;
      if (top.next >= targets.length) { state.set(top.id, 2); stack.pop(); continue; }
      const to = targets[top.next++]!;
      if (state.get(to) === 1) continue;
      forward.get(top.id)!.push(to);
      if (!state.has(to)) { state.set(to, 1); stack.push({ id: to, next: 0 }); }
    }
  };
  for (const node of nodes) if (!state.has(node.id)) walk(node.id);
  // Longest path from the sources, over the acyclic rest (Kahn's order).
  const incoming = new Map<string, number>(nodes.map(node => [node.id, 0]));
  for (const targets of forward.values()) for (const to of targets) incoming.set(to, incoming.get(to)! + 1);
  const layer = new Map<string, number>(nodes.map(node => [node.id, 0]));
  const queue = nodes.filter(node => incoming.get(node.id) === 0).map(node => node.id);
  for (let head = 0; head < queue.length; head++) {
    const id = queue[head]!;
    for (const to of forward.get(id)!) {
      layer.set(to, Math.max(layer.get(to)!, layer.get(id)! + 1));
      incoming.set(to, incoming.get(to)! - 1);
      if (incoming.get(to) === 0) queue.push(to);
    }
  }
  return layer;
}

export function layoutLayers(nodes: readonly LayerNode[], edges: readonly LayerEdge[], options: LayerOptions = {}): Layout {
  const { gapLayer = 96, gapNode = 28, wrap = 8 } = options;
  const layer = layerOf(nodes, edges);
  const count = Math.max(-1, ...layer.values()) + 1;
  const layers: string[][] = Array.from({ length: count }, () => []);
  for (const node of nodes) layers[layer.get(node.id)!]!.push(node.id);
  // One sweep of barycentres (the mean place of the neighbours in the layer before) to cut crossings; ties keep the input order.
  const byId = new Map(nodes.map(node => [node.id, node]));
  const rank = new Map<string, number>();
  const into = new Map<string, string[]>();
  for (const { from, to } of edges) into.set(to, [...(into.get(to) ?? []), from]);
  layers.forEach((ids, depth) => {
    if (depth > 0) {
      const mean = (id: string) => {
        const before = (into.get(id) ?? []).filter(from => layer.get(from) === depth - 1).map(from => rank.get(from)!);
        return before.length ? before.reduce((a, b) => a + b, 0) / before.length : Infinity;
      };
      const order = new Map(ids.map((id, index) => [id, index]));
      ids.sort((a, b) => (mean(a) - mean(b)) || order.get(a)! - order.get(b)!);
    }
    ids.forEach((id, index) => rank.set(id, index));
  });
  // A wide layer wraps into columns that follow each other.
  const along = (id: string) => byId.get(id)!.width;
  const across = (id: string) => byId.get(id)!.height;
  const columns = layers.map(ids => Array.from({ length: Math.ceil(ids.length / wrap) }, (_, index) => ids.slice(index * wrap, index * wrap + wrap)));
  const extent = (column: string[]) => column.reduce((sum, id) => sum + across(id), 0) + gapNode * Math.max(0, column.length - 1);
  const tallest = Math.max(0, ...columns.flat().map(extent));
  const positions = new Map<string, { x: number; y: number }>();
  let cursor = 0;
  for (const layerColumns of columns) {
    layerColumns.forEach((column, index) => {
      let offset = (tallest - extent(column)) / 2;
      for (const id of column) {
        positions.set(id, { x: cursor, y: offset });
        offset += across(id) + gapNode;
      }
      cursor += Math.max(...column.map(along)) + (index === layerColumns.length - 1 ? gapLayer : gapNode);
    });
  }
  const length = Math.max(0, cursor - gapLayer);
  return { positions, width: length, height: tallest, layers };
}
