import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import type { DiagramsEntryKey } from './strings'
import './strings'

/** Default (Czech) text of the `diagrams` section that only the lazy runtime renders. */
export const diagramsStrings: MarkdownStrings['diagrams'] = registerMarkdownStrings('diagrams', {
  sourceSummary: 'Zdroj diagramu',
  sourceLabel: 'Zdroj Mermaid',
  hideSource: 'Hotovo',
  sourceEditorTitle: 'Mermaid',
  captionLabel: 'Popisek',
  captionPlaceholder: 'Volitelný popisek diagramu',
  editHint: 'Náhled se mění za psaní. Esc zavře, Ctrl+Z vrátí.',
  emptySource: 'Napište zdroj Mermaid pro zobrazení diagramu.',
  invalidSource: 'Neplatný zdroj.',
  renderFailed: (message, keptPreviousImage) => `${keptPreviousImage ? 'Zobrazuje se poslední platný náhled. ' : ''}Diagram nelze vykreslit: ${message}`,
  saveFailed: 'Zdroj nelze uložit.',
} satisfies Omit<MarkdownStrings['diagrams'], DiagramsEntryKey>)
