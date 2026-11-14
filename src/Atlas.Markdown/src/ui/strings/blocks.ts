import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `blocks` section of `MarkdownStrings` (see ../strings.ts). */
export interface BlocksStrings {
  calloutNote: string
  calloutTip: string
  calloutWarning: string
  calloutImportant: string
  calloutCaution: string
  /** Selection toolbar label for a callout block. */
  calloutToolbarLabel: string
  /** The callout-type menu's title (the box's own edit). */
  calloutTypeLabel: string
  /** The toolbar's edit button („Upravit“, P12) that opens the type menu, as a click on the box's label, Enter or F2 do. */
  calloutTypeButtonText: string
  calloutRemove: string
  /** The timed item stamp's edit (a click on it, F2): the field's title, its label and the error of a value that is no date. */
  timedStampEdit: string
  timedStampLabel: string
  timedStampInvalid: string
  /** Shown when a budget check rejects a pasted image before upload starts. */
  pasteLimitExceeded: string
  /** Shown when a budget check rejects a dropped image before upload starts. */
  imageLimitExceeded: string
}

export const blocksStrings: BlocksStrings = registerMarkdownStrings('blocks', {
  calloutNote: 'Poznámka',
  calloutTip: 'Tip',
  calloutWarning: 'Upozornění',
  calloutImportant: 'Důležité',
  calloutCaution: 'Pozor',
  calloutToolbarLabel: 'Informační box',
  calloutTypeLabel: 'Typ informačního boxu',
  calloutTypeButtonText: 'Upravit',
  calloutRemove: 'Smazat informační box',
  timedStampEdit: 'Upravit datum a čas',
  timedStampLabel: 'Datum a čas',
  timedStampInvalid: 'Zadejte datum jako 3. 11. 2026, případně i čas 17:19.',
  pasteLimitExceeded: 'Limit importu byl překročen.',
  imageLimitExceeded: 'Limit obrázku byl překročen.',
} satisfies BlocksStrings)
