import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `diagrams` section that the plugin entry renders. */
const entry = {
  title: 'Diagram',
  slashDescription: 'Vývojový diagram nebo schéma (Mermaid)',
  kindFlowchart: 'Vývojový diagram',
  kindSequence: 'Sekvenční diagram',
  kindClass: 'Diagram tříd',
  kindState: 'Stavový diagram',
  kindGantt: 'Ganttův diagram',
  sourceRequired: 'Diagram vyžaduje zdroj Mermaid (nejvýše 20 000 znaků) a popisek (nejvýše 500 znaků).',
  htmlNotAllowed: 'Diagram nesmí obsahovat HTML, konfigurační direktivy ani frontmatter.',
} satisfies Partial<MarkdownStrings['diagrams']>
/** Keys registered here; ./runtimeStrings adds the rest when the lazy runtime loads. */
export type DiagramsEntryKey = keyof typeof entry
export const diagramsStrings: Pick<MarkdownStrings['diagrams'], DiagramsEntryKey> = registerMarkdownStrings('diagrams', entry)
