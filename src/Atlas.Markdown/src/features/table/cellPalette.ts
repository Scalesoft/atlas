import { tableStrings } from '../../ui/strings/table'

export const CELL_BG_PALETTE = [
  { key: null, testid: 'cell-bg-none', get label() { return tableStrings.noFillLabel } },
  { key: 'accent', testid: 'cell-bg-accent', get label() { return tableStrings.accentLabel } },
  { key: 'green', testid: 'cell-bg-green', get label() { return tableStrings.greenLabel } },
  { key: 'amber', testid: 'cell-bg-amber', get label() { return tableStrings.amberLabel } },
  { key: 'red', testid: 'cell-bg-red', get label() { return tableStrings.redLabel } },
] as const
