import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `collaboration` section of `MarkdownStrings` (see ../strings.ts). */
export interface CollaborationStrings {
  // connectionPresentation.ts — label/detail pairs for each link state.
  localDocumentLabel: string
  localDocumentDetail: string
  sessionEndedLabel: string
  sessionEndedDetail: string
  failedLabel: string
  failedDetail: string
  /** Detail shown while offline/reconnecting when the editor still accepts edits. */
  offlineEditableDetail: string
  /** Detail shown while offline/reconnecting when editing is paused. */
  offlinePausedDetail: string
  offlineLabel: string
  /** Detail for an offline session whose document never finished its first load. */
  offlineNotLoadedDetail: string
  reconnectingLabel: string
  connectingLabel: string
  connectingNotEditableDetail: string
  notSavingLabel: string
  notSavingDetail: string
  weakLabel: string
  stalledDetail: string
  weakDetail: string
  liveLabel: string
  liveDetail: string

  // ConnectionDot.vue detail panel.
  notMeasuredYet: string
  noOutages: string
  /** Outage count, e.g. "1 výpadek" / "3 výpadky" / "5 výpadků". */
  outageCount: (count: number) => string
  connectionStatusAriaLabel: (label: string) => string
  responseTimeLabel: string
  reliabilityLabel: string
  pendingChangesLabel: string
  collaboratorsLabel: string
  noOtherCollaborators: string

  // CollaborationStatus.vue badge.
  documentFullLabel: string
  flushDegradedLabel: string
  /** "Připojování…" — distinct from `connectingLabel`, used by the compact status badge. */
  connectingEllipsisLabel: string
  /** "Obnovování spojení…" — distinct from `reconnectingLabel`, used by the compact status badge. */
  reconnectingEllipsisLabel: string
  offlineShortLabel: string
  localLabel: string

  // CollaborationNotice.vue.
  pendingChangesNotice: string

  // PresenceStack.vue.
  presentCollaboratorsAriaLabel: (names: string) => string
  moreCollaboratorsAriaLabel: (names: string) => string

  /** Default display name for a remote collaborator with no name in their awareness state. */
  anonymousUser: string

  /** Adapter-reported error: the document reached its size limit. */
  documentSizeLimitExceeded: string
  /** Adapter-reported error: projections (preview/export) could not be saved. */
  projectionSaveFailed: string
  /** Simulator's `failed` state: the connection was refused. */
  connectionRefused: string
  /** Adapter-reported error: the server refused a change ([INVALID_CONTENT]); the session ended and unsent edits were kept. */
  contentRefused: string
  /** A colleague removed the block being typed in (collab/remoteRemoval.ts); `name` is null when it cannot be told who. */
  remoteRemoval: (name: string | null) => string

  // MarkdownRecoveryPanel.vue.
  recoveryPanelAriaLabel: string
  recoveryIntro: string
  loadBranches: string
  invalidBranchList: string
  branchScopeMismatch: string
  noBranches: string
  inspectBranch: (idPrefix: string) => string
  branchSummary: (revision: number, pendingCount: number, active: boolean) => string
  selectedBranchSummary: (branchId: string, epoch: string) => string
  closePreview: string
  branchActionsAriaLabel: string
  downloadJson: string
  downloadMarkdown: string
  resumeBranch: string
  discardBranch: string
  confirmDiscardBranch: string
  markdownLossyNotice: string
  recoveryPreviewAriaLabel: string
}

const entry = {
  localDocumentLabel: 'Místní dokument',
  localDocumentDetail: 'Dokument není sdílený; změny zůstávají v této aplikaci.',
  sessionEndedLabel: 'Relace ukončena',
  sessionEndedDetail: 'Dokument už není připojený.',
  failedLabel: 'Dokument nelze upravovat',
  failedDetail: 'Připojení k dokumentu se nezdařilo. Obraťte se na správce dokumentu.',
  offlineEditableDetail: 'Píšete bez spojení. Změny zatím nejsou potvrzené serverem.',
  offlinePausedDetail: 'Úpravy jsou pozastavené. Po obnovení spojení budete moci pokračovat.',
  offlineLabel: 'Server není dostupný',
  offlineNotLoadedDetail: 'Dokument se zatím nepodařilo načíst. Úpravy budou dostupné po připojení.',
  reconnectingLabel: 'Obnovujeme spojení',
  connectingLabel: 'Připojujeme dokument',
  connectingNotEditableDetail: 'Čekáme na načtení ze serveru. Zatím nelze upravovat.',
  notSavingLabel: 'Server neukládá',
  notSavingDetail: 'Spojení drží, ale server dokument neuloží. Nezavírejte tuto stránku.',
  weakLabel: 'Pomalé spojení',
  stalledDetail: 'Server zatím nepotvrdil vaše poslední změny.',
  weakDetail: 'Změny se přenášejí, ale s výraznou prodlevou.',
  liveLabel: 'Živě synchronizováno',
  liveDetail: 'Změny se průběžně ukládají na server.',

  notMeasuredYet: 'zatím neměřeno',
  noOutages: 'bez výpadku',
  outageCount: count => count === 1 ? '1 výpadek' : `${count} ${count < 5 ? 'výpadky' : 'výpadků'}`,
  connectionStatusAriaLabel: label => `Stav spolupráce: ${label}`,
  responseTimeLabel: 'Odezva',
  reliabilityLabel: 'Spolehlivost',
  pendingChangesLabel: 'Nepotvrzené změny',
  collaboratorsLabel: 'Spolupracovníci',
  noOtherCollaborators: 'nikdo další',

  documentFullLabel: 'Dokument je plný',
  flushDegradedLabel: 'Neukládá se na disk',
  connectingEllipsisLabel: 'Připojování…',
  reconnectingEllipsisLabel: 'Obnovování spojení…',
  offlineShortLabel: 'Offline',
  localLabel: 'Lokálně',

  pendingChangesNotice: 'Některé změny ještě server nepotvrdil. Nezavírejte tuto stránku.',

  presentCollaboratorsAriaLabel: names => `Přítomní spolupracovníci: ${names}`,
  moreCollaboratorsAriaLabel: names => `Další: ${names}`,
} satisfies Partial<CollaborationStrings>
/** Keys registered here; ./collaborationAddons adds the text of the optional ./collaboration/* entries. */
export type CollaborationEntryKey = keyof typeof entry
export const collaborationStrings: Pick<CollaborationStrings, CollaborationEntryKey> = registerMarkdownStrings('collaboration', entry)
