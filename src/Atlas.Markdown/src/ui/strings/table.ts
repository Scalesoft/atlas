import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `table` section of `MarkdownStrings` (see ../strings.ts). */
export interface TableStrings {
  noFillLabel: string
  accentLabel: string
  greenLabel: string
  amberLabel: string
  redLabel: string
  /** "Vložit řádek za řádek {boundary}" hover-insert aria-label. */
  insertRowAfterLabel: (boundary: number) => string
  /** "Vložit sloupec za sloupec {boundary}" hover-insert aria-label. */
  insertColumnAfterLabel: (boundary: number) => string
  /** "{count} sloupec/sloupce/sloupců" with Czech plural agreement. */
  columnQuantityLabel: (count: number) => string
  /** "{count} řádek/řádky/řádků" with Czech plural agreement. */
  rowQuantityLabel: (count: number) => string
  /** Column-chip drag title built from a `columnQuantityLabel` phrase. */
  moveColumnsTitle: (quantity: string) => string
  /** Row-chip drag title built from a `rowQuantityLabel` phrase. */
  moveRowsTitle: (quantity: string) => string
  resizeRowHeightLabel: (position: number) => string
  columnPositionLabel: (position: number) => string
  rowPositionLabel: (position: number) => string
  toolbarLabel: string
  cellAsHeaderLabel: string
  mergeSplitCellsLabel: string
  toggleHeaderRowLabel: string
  rowOperationsLabel: string
  insertRowAboveLabel: string
  insertRowBelowLabel: string
  deleteRowLabel: string
  columnOperationsLabel: string
  insertColumnLeftLabel: string
  insertColumnRightLabel: string
  sortHint: string
  sortAscendingLabel: string
  sortDescendingLabel: string
  deleteColumnLabel: string
  deleteTableLabel: string
  cellSettingsLabel: string
  cellAlignLabel: string
  columnAlignLabel: string
  alignGroupLabel: string
  backgroundColorLabel: string
  clearContentLabel: string
  alignLeftLabel: string
  alignCenterLabel: string
  alignRightLabel: string
}

export const tableStrings: TableStrings = registerMarkdownStrings('table', {
  noFillLabel: 'Bez výplně',
  accentLabel: 'Akcentová',
  greenLabel: 'Zelená',
  amberLabel: 'Oranžová',
  redLabel: 'Červená',
  insertRowAfterLabel: boundary => `Vložit řádek za řádek ${boundary}`,
  insertColumnAfterLabel: boundary => `Vložit sloupec za sloupec ${boundary}`,
  columnQuantityLabel: count => `${count} ${count === 1 ? 'sloupec' : count < 5 ? 'sloupce' : 'sloupců'}`,
  rowQuantityLabel: count => `${count} ${count === 1 ? 'řádek' : count < 5 ? 'řádky' : 'řádků'}`,
  moveColumnsTitle: quantity => `Přesunout ${quantity}. Kliknutí vybere, Shift+klik rozšíří výběr. Shift + ← / → přesune výběr.`,
  moveRowsTitle: quantity => `Přesunout ${quantity}. Kliknutí vybere, Shift+klik rozšíří výběr. Shift + ↑ / ↓ přesune výběr. Záhlaví zůstává nahoře.`,
  resizeRowHeightLabel: position => `Změnit výšku řádku ${position}`,
  columnPositionLabel: position => `Sloupec ${position}`,
  rowPositionLabel: position => `Řádek ${position}`,
  toolbarLabel: 'Tabulka',
  cellAsHeaderLabel: 'Buňka jako záhlaví',
  mergeSplitCellsLabel: 'Sloučit / rozdělit buňky (rich)',
  toggleHeaderRowLabel: 'Přepnout řádek záhlaví',
  rowOperationsLabel: 'Operace s řádkem',
  insertRowAboveLabel: 'Vložit řádek nad',
  insertRowBelowLabel: 'Vložit řádek pod',
  deleteRowLabel: 'Smazat řádek',
  columnOperationsLabel: 'Operace se sloupcem',
  insertColumnLeftLabel: 'Vložit sloupec vlevo',
  insertColumnRightLabel: 'Vložit sloupec vpravo',
  sortHint: 'Vyberte jeden sloupec. Záhlaví zůstává nahoře, prázdné buňky na konci. Svisle sloučené řádky nelze řadit.',
  sortAscendingLabel: 'Seřadit vzestupně',
  sortDescendingLabel: 'Seřadit sestupně',
  deleteColumnLabel: 'Smazat sloupec',
  deleteTableLabel: 'Smazat tabulku',
  cellSettingsLabel: 'Nastavení buňky',
  cellAlignLabel: 'Zarovnání buňky',
  columnAlignLabel: 'Zarovnání sloupce',
  alignGroupLabel: 'Zarovnání',
  backgroundColorLabel: 'Barva pozadí',
  clearContentLabel: 'Vymazat obsah',
  alignLeftLabel: 'Doleva',
  alignCenterLabel: 'Na střed',
  alignRightLabel: 'Doprava',
} satisfies TableStrings)
