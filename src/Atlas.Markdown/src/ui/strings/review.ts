import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `review` section of `MarkdownStrings` (see ../strings.ts). */
export interface ReviewStrings {
  // useSectionReview.ts — `status` computed and error fallbacks.
  loadingServerState: string
  waitingForServerConfirmation: string
  sectionsSavePaused: string
  serverStateVerifyFailed: string
  unsavedSectionChanges: string
  serverStateNotVerifiedYet: string
  draftsSavedNotPublished: string
  publishedVersionLoaded: string
  operationFailed: string
  sectionChangedRemotely: string
  documentLoadFailed: string
  saveFailed: string

  // MarkdownSectionReview.vue.
  documentAriaLabel: string
  /** Button that re-reads the whole review document after an error. */
  refresh: string
  unapprovedChangesLabel: string
  statusApproved: string
  statusDraft: string
  statusPending: string
  /** Section menu trigger label/aria-label, e.g. "Sekce 2: Rozepsáno". */
  sectionLabel: (index: number, statusLabel: string) => string
  /** "<small>" caption above the approved-version preview in changes view. */
  publishedVersionLabel: string
  publishedVersionAriaLabel: string
  sectionDeletionProposed: string
  sectionContentAriaLabel: (index: number) => string
  sectionPreviewAriaLabel: (index: number) => string
  retrySave: string
  keptLocalEditsSummary: string
  localEditsBackupAriaLabel: string
  discardLocalEditsAndReload: string
  addSection: string

  // MarkdownReviewModes.vue.
  modeDocument: string
  modeChanges: string
  modeHistory: string
  viewAriaLabel: string

  // MarkdownSectionMenu.vue.
  approveAction: string
  rejectAction: string
  withdrawAction: string
  deleteAction: string
}

export const reviewStrings: ReviewStrings = registerMarkdownStrings('review', {
  loadingServerState: 'Načítám stav serveru…',
  waitingForServerConfirmation: 'Čekám na potvrzení serveru…',
  sectionsSavePaused: 'Uložení sekcí pozastaveno',
  serverStateVerifyFailed: 'Stav serveru se nepodařilo ověřit',
  unsavedSectionChanges: 'Neuložené změny sekcí',
  serverStateNotVerifiedYet: 'Stav serveru dosud neověřen',
  draftsSavedNotPublished: 'Návrhy uloženy; nejsou publikované',
  publishedVersionLoaded: 'Publikovaná verze načtena',
  operationFailed: 'Operace se nezdařila.',
  sectionChangedRemotely: 'Sekce se mezitím změnila nebo byla odstraněna. Místní úpravy zůstávají zachované níže.',
  documentLoadFailed: 'Dokument se nepodařilo načíst.',
  saveFailed: 'Uložení se nezdařilo.',

  documentAriaLabel: 'Společný dokument se schvalováním',
  refresh: 'Obnovit',
  unapprovedChangesLabel: 'Neschválené změny',
  statusApproved: 'Publikováno',
  statusDraft: 'Rozepsáno',
  statusPending: 'K posouzení',
  sectionLabel: (index, statusLabel) => `Sekce ${index}: ${statusLabel}`,
  publishedVersionLabel: 'Publikovaná podoba',
  publishedVersionAriaLabel: 'Publikovaná podoba sekce',
  sectionDeletionProposed: 'Navrženo odstranění této sekce. Publikovaná podoba zůstává do schválení zachovaná.',
  sectionContentAriaLabel: index => `Obsah sekce ${index}`,
  sectionPreviewAriaLabel: index => `Náhled sekce ${index}`,
  retrySave: 'Zkusit uložit znovu',
  keptLocalEditsSummary: 'Zachované místní úpravy',
  localEditsBackupAriaLabel: 'Záloha místních úprav',
  discardLocalEditsAndReload: 'Zahodit místní úpravy a načíst aktuální sekci',
  addSection: '+ Nová sekce',

  modeDocument: 'Dokument',
  modeChanges: 'Změny',
  modeHistory: 'Historie',
  viewAriaLabel: 'Zobrazení dokumentu',

  approveAction: 'Schválit sekci',
  rejectAction: 'Odmítnout změny',
  withdrawAction: 'Stáhnout změny',
  deleteAction: 'Navrhnout odstranění',
} satisfies ReviewStrings)
