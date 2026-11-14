import type { MarkdownDiagramData } from './index'

/**
 * A first-visit size for a drawing not made yet (J8.1), read from its source: a vertical flowchart grows by about 115 px per node, a
 * horizontal one stays a band, a sequence by its messages. Later visits use the height the drawing really had (block.ts).
 */
export function estimateDiagramHeight(data: MarkdownDiagramData): number {
  const lines = data.source.split(/\r?\n/).map(line => line.trim()).filter(line => line && !line.startsWith('%%'))
  const kind = lines[0] ?? ''
  const caption = data.caption ? 28 : 0
  if (/^(flowchart|graph)\s+(TB|TD|BT)\b/i.test(kind)) {
    const edges = lines.reduce((sum, line) => sum + (line.match(/-->|---|-\.->|==>|-\.-/g)?.length ?? 0), 0)
    return Math.round(70 + 115 * Math.max(2, edges + 1)) + caption
  }
  if (/^(flowchart|graph)\b/i.test(kind)) {
    // A horizontal flowchart is a band of about 170 px, and about 60 px more for each branch beside it (the most edges leaving one node).
    const leaving = new Map<string, number>()
    for (const line of lines.slice(1)) { const from = /^([\w-]+)[^-=.]*?(?:-->|---|-\.->|==>|-\.-)/.exec(line)?.[1]; if (from) leaving.set(from, (leaving.get(from) ?? 0) + 1) }
    return 170 + 60 * (Math.max(1, ...leaving.values()) - 1) + caption
  }
  // A pie is drawn at its fixed size with the legend beside it.
  if (/^pie\b/i.test(kind)) return 537 + caption
  // A sequence is drawn across the column with its participants above and below: about 380 px and 35 px a message.
  if (/^sequenceDiagram/i.test(kind)) return 380 + 35 * lines.filter(line => /->>|-->>|-x|--x|->|--/.test(line)).length + caption
  return 260 + caption
}
