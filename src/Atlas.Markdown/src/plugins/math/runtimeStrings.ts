import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import type { MathEntryKey } from './strings'
import './strings'

/** Default (Czech) text of the `math` section that only the lazy runtime renders. */
export const mathStrings: MarkdownStrings['math'] = registerMarkdownStrings('math', {
  renderFailed: message => ` Vzorec nelze vykreslit: ${message}`,
  invalidExpression: 'Neplatný výraz.',
  editTitle: 'Upravit matematický výraz',
  expressionLabel: 'Výraz LaTeX',
  hideSource: 'Hotovo',
  editHint: 'Vzorec se mění za psaní. Esc zavře, Ctrl+Z vrátí.',
} satisfies Omit<MarkdownStrings['math'], MathEntryKey>)
