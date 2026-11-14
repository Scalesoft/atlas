import { registerMarkdownStrings } from '../strings'
import type { CollaborationEntryKey, CollaborationStrings } from './collaboration'
import './collaboration'

/** Default (Czech) text of the `collaboration` section used only by the optional ./collaboration/* entries. */
export const collaborationStrings: CollaborationStrings = registerMarkdownStrings('collaboration', {
  anonymousUser: 'Uživatel',

  documentSizeLimitExceeded: 'Document size limit exceeded.',
  projectionSaveFailed: 'Document projections could not be saved.',
  connectionRefused: 'Připojení k dokumentu bylo odmítnuto.',
  remoteRemoval: name => `${name ?? 'Někdo'} odebral(a) blok, ve kterém jste psal(a).`,
  contentRefused: 'Server odmítl poslední změny a dokument se přestal ukládat. Neuložené změny zůstaly v tomto okně a v místní obnově, pokud ji aplikace nabízí; před zavřením si je zkopírujte.',

  recoveryPanelAriaLabel: 'Místní obnova dokumentu',
  recoveryIntro: 'Místní větve obsahují také dosud nepotvrzené změny. Obnovení ověří přístup a verzi dokumentu na serveru.',
  loadBranches: 'Načíst místní větve',
  invalidBranchList: 'Neplatný seznam místních větví.',
  branchScopeMismatch: 'Úložiště vrátilo větev jiného účtu nebo dokumentu.',
  noBranches: 'Pro tento účet a dokument nejsou uložené větve.',
  inspectBranch: idPrefix => `Prohlédnout větev ${idPrefix}`,
  branchSummary: (revision, pendingCount, active) => `Revize ${revision}, čekajících změn ${pendingCount}${active ? ' · právě otevřená' : ''}`,
  selectedBranchSummary: (branchId, epoch) => `Vybraná větev ${branchId} · epocha ${epoch}`,
  closePreview: 'Zavřít náhled',
  branchActionsAriaLabel: 'Akce vybrané místní větve',
  downloadJson: 'Stáhnout úplný JSON',
  downloadMarkdown: 'Stáhnout Markdown',
  resumeBranch: 'Otevřít tuto větev',
  discardBranch: 'Zahodit větev',
  confirmDiscardBranch: 'Potvrdit trvalé zahození větve',
  markdownLossyNotice: 'Markdown nezachová veškeré formátování. Úplný obsah je v JSON exportu.',
  recoveryPreviewAriaLabel: 'Náhled místní obnovy',
} satisfies Omit<CollaborationStrings, CollaborationEntryKey>)
