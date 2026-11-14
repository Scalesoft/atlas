import { estimateDiagramHeight } from './estimate'
import { defineMarkdownBlockModule, pickNamedChoice, type MarkdownPlugin } from '../../core'
import { diagramsStrings } from './strings'

export interface MarkdownDiagramData { version: 1; source: string; caption: string }
export const DIAGRAM_SOURCE_LIMIT = 20_000
export function normalizeDiagram(value: unknown): MarkdownDiagramData {
  const data = value as Partial<MarkdownDiagramData> | null
  if (!data || data.version !== 1 || typeof data.source !== 'string'
    || data.source.length > DIAGRAM_SOURCE_LIMIT || typeof data.caption !== 'string' || data.caption.length > 500) {
    throw new TypeError(diagramsStrings.sourceRequired)
  }
  // Source is data, never a channel for changing renderer security or embedding HTML.
  if (/%%\s*\{|^\s*---|<\/?[a-z!]/im.test(data.source)) {
    throw new TypeError(diagramsStrings.htmlNotAllowed)
  }
  return { version: 1, source: data.source, caption: data.caption }
}
/** A backtick fence longer than any backtick run in the source. */
function codeFence(language: string, source: string): string {
  const ticks = '`'.repeat(Math.max(3, ...(source.match(/`+/g) ?? []).map(run => run.length + 1)))
  return `${ticks}${language}\n${source}\n${ticks}`
}
// `/diagram sekvence`: a starting diagram of that kind; matched by the Czech row title or the Mermaid word.
const starters = {
  flowchart: { names: ['tok', 'proces'], source: 'flowchart LR\n  A[Začátek] --> B[Konec]' },
  sequence: { names: ['sekvence'], source: 'sequenceDiagram\n  Klient->>Server: Požadavek\n  Server-->>Klient: Odpověď' },
  class: { names: ['tridy'], source: 'classDiagram\n  Zakaznik --> Objednavka\n  class Objednavka {\n    +cislo\n  }' },
  state: { names: ['stavy'], source: 'stateDiagram-v2\n  [*] --> Navrh\n  Navrh --> Hotovo\n  Hotovo --> [*]' },
  gantt: { names: ['harmonogram'], source: 'gantt\n  dateFormat YYYY-MM-DD\n  Návrh :a1, 2026-01-05, 5d\n  Vývoj :after a1, 10d' },
} as const
type Starter = keyof typeof starters
const starterTitle = (kind: Starter) => ({ flowchart: diagramsStrings.kindFlowchart, sequence: diagramsStrings.kindSequence, class: diagramsStrings.kindClass,
  state: diagramsStrings.kindState, gantt: diagramsStrings.kindGantt })[kind]
const starter = (text: string) => pickNamedChoice(text, (Object.keys(starters) as Starter[]).map(kind => [kind, starterTitle(kind), kind, ...starters[kind].names] as const))

const module = defineMarkdownBlockModule<MarkdownDiagramData>({
  id: 'diagrams', name: 'diagram', openOnInsert: true, stubHeight: estimateDiagramHeight, dataValidator: 'diagram', get label() { return diagramsStrings.title }, get description() { return diagramsStrings.slashDescription }, icon: 'workflow', keywords: ['diagram', 'mermaid', 'schema', 'flowchart', 'proces'],
  defaults: { version: 1, source: starters.flowchart.source, caption: '' },
  parameter: { get label() { return diagramsStrings.title }, data: text => { const kind = starter(text); return kind && { version: 1, source: starters[kind].source, caption: '' } },
    title: text => starterTitle(starter(text)!) },
  normalize: normalizeDiagram,
  text: data => `${data.caption ? `${data.caption}\n` : ''}Diagram (Mermaid):\n${data.source}`,
  // The editor dialect parses a ```mermaid fence back into a diagram; a caption has no Markdown form.
  markdown: data => data.caption || /[\r\0]/.test(data.source) ? undefined : codeFence('mermaid', data.source),
  html: data => ['figure', {}, ['pre', {}, ['code', {}, data.source]], ['figcaption', {}, data.caption]],
  // Core marks a ```mermaid fence (editor dialect); source failing validation stays a code block.
  parseHTML: [{ tag: 'div[data-md-fence="mermaid"]', data: element => ({ version: 1, source: element.getAttribute('data-md-source') ?? '', caption: '' }) }],
  load: () => import('./runtime').then(module => module.diagramRuntime),
})
export const createDiagramsPlugin = (): MarkdownPlugin => ({
  ...module.createPlugin(), schemaVersion: 2,
  migrations: [{ from: 1, to: 2, migrate: document => ({ ...document }) }],
})
export const insertDiagram = module.insert
export const createDiagramNode = module.node
