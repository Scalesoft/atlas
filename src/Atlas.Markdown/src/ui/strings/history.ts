import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `history` section of `MarkdownStrings` (see ../strings.ts). */
export interface HistoryStrings {
  // useDocumentHistory.ts error fallbacks.
  historyLoadFailed: string
  snapshotLoadFailed: string

  // MarkdownDocumentHistory.vue.
  documentHistoryAriaLabel: string
  previousChange: string
  /** Fallback for the current-checkpoint time label before any entry is selected. */
  historyFallbackLabel: string
  nextChange: string
  zoomOutAriaLabel: string
  zoomSliderAriaLabel: string
  zoomInAriaLabel: string
  scrubAriaLabel: string
  /** Scrub slider's `aria-valuetext` fallback when there is no history yet. */
  noHistoryValueText: string
  loadingHistory: string
  noHistoryYet: string
  unapprovedChangesLabel: string
  loadOlderPeriod: string
  loadingSelectedVersion: string
  /** Fallback caption when the working snapshot has no `workingLabel` of its own. */
  workingVersionFallbackLabel: string
  publishedVersionFallbackLabel: string
  /** Suffix after the caption, e.g. "Tehdejší publikovaná podoba · pouze čtení". */
  readOnlyCaption: string
  historicalVersionAriaLabel: string
}

export const historyStrings: HistoryStrings = registerMarkdownStrings('history', {
  historyLoadFailed: 'Historii se nepodařilo načíst.',
  snapshotLoadFailed: 'Verzi se nepodařilo načíst.',

  documentHistoryAriaLabel: 'Historie celého dokumentu',
  previousChange: 'Předchozí změna',
  historyFallbackLabel: 'Historie',
  nextChange: 'Další změna',
  zoomOutAriaLabel: 'Oddálit časovou osu',
  zoomSliderAriaLabel: 'Zoom časové osy',
  zoomInAriaLabel: 'Přiblížit časovou osu',
  scrubAriaLabel: 'Čas zobrazeného dokumentu',
  noHistoryValueText: 'Bez historie',
  loadingHistory: 'Načítám historii…',
  noHistoryYet: 'Dokument zatím nemá historii.',
  unapprovedChangesLabel: 'Neschválené změny',
  loadOlderPeriod: 'Načíst starší období',
  loadingSelectedVersion: 'Načítám vybranou verzi…',
  workingVersionFallbackLabel: 'Tehdejší pracovní podoba',
  publishedVersionFallbackLabel: 'Tehdejší publikovaná podoba',
  readOnlyCaption: 'pouze čtení',
  historicalVersionAriaLabel: 'Historická verze dokumentu',
} satisfies HistoryStrings)
