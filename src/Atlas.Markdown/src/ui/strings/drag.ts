import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `drag` section of `MarkdownStrings` (see ../strings.ts). */
export interface DragStrings {
  /** Fallback block-handle label when no drag range is resolved yet. */
  moveBlockLabel: string
  /** "{count} blok/bloky/bloků" with Czech plural agreement. */
  blockCountLabel: (count: number) => string
  /** "Sekce · {quantity}" combining a section prefix with a block-count phrase. */
  sectionCountLabel: (quantity: string) => string
  /** "Přesunout sekci · {quantity}" for a whole-section drag handle label. */
  moveSectionLabel: (quantity: string) => string
  /** "Přesunout {quantity}" for a multi-block drag handle label. */
  moveQuantityLabel: (quantity: string) => string
  insertAtStart: string
  insertAtEnd: string
  /** "Před sekci {title}" drop-target label before a heading. */
  beforeSectionLabel: (title: string) => string
  insertHere: string
  selectedBlocksLabel: string
  selectionRangeLabel: string
  wholeSectionLabel: string
  headingOnlyLabel: string
  headingLabel: string
  /** Opens the action menu with the rows that turn the block into another kind (Změnit na…). */
  turnIntoLabel: string
  moveUpLabel: string
  moveDownLabel: string
  addBlockBelowLabel: string
  /** Drag-grip aria-label: "Menu bloku. … {rangeLabel}. Shift+šipky přesunou." */
  gripAriaLabel: (rangeLabel: string) => string
  insertBlockHereLabel: string
  insertBlockHereTitle: string
}

export const dragStrings: DragStrings = registerMarkdownStrings('drag', {
  moveBlockLabel: 'Přesunout blok',
  blockCountLabel: count => `${count} ${count === 1 ? 'blok' : count < 5 ? 'bloky' : 'bloků'}`,
  sectionCountLabel: quantity => `Sekce · ${quantity}`,
  moveSectionLabel: quantity => `Přesunout sekci · ${quantity}`,
  moveQuantityLabel: quantity => `Přesunout ${quantity}`,
  insertAtStart: 'Vložit na začátek',
  insertAtEnd: 'Vložit na konec',
  beforeSectionLabel: title => `Před sekci ${title}`,
  insertHere: 'Vložit sem',
  selectedBlocksLabel: 'Vybrané bloky',
  selectionRangeLabel: 'Rozsah výběru',
  wholeSectionLabel: 'Celá sekce',
  headingOnlyLabel: 'Pouze nadpis',
  headingLabel: 'Nadpis',
  turnIntoLabel: 'Změnit na…',
  moveUpLabel: 'Posunout nahoru',
  moveDownLabel: 'Posunout dolů',
  addBlockBelowLabel: 'Přidat blok pod',
  gripAriaLabel: rangeLabel => `Menu bloku. Enter otevře nabídku akcí bloku, Shift+Enter rozšíří výběr, šipka dolů otevře lištu výběru. ${rangeLabel}. Shift+šipky přesunou.`,
  insertBlockHereLabel: 'Přidat blok sem',
  insertBlockHereTitle: 'Přidat blok sem (Alt+Insert)',
} satisfies DragStrings)
