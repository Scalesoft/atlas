import { registerMarkdownStrings } from '../strings'

/** Keys of the `panels` section: the source panel, the draft confirmation and the notices of the views a host opens on purpose. */
export interface PanelsStrings {
  /** Shown in place of a view whose chunk failed to download; rendering the view again retries. */
  historyLoadError: string
  sourceLoadError: string
  reviewLoadError: string
  discardAndContinue: string
  continueEditing: string
  sourceAriaLabel: string
  fallbackAriaLabel: (label: string) => string
  preparing: string
  editorFailed: string
  readOnly: string
  saving: string
  loadFailed: string
  noticeSaved: string
  noticeUnchanged: string
  noticeInvalid: string
  noticeConflict: string
  noticeLiveEdits: string
  noticeChanged: string
  noticeError: string
  noticeUnavailable: string
  reloadCurrent: string
  reloadConfirmMessage: string
  discardDraftAndLoad: string
  diagnosticsLabel: string
  errorKind: string
  warningKind: string
  diagnosticLine: (line: number, message: string) => string
  dirtyHint: string
  cleanHint: string
  discardChanges: string
}

export const panelsStrings: PanelsStrings = registerMarkdownStrings('panels', {
  historyLoadError: 'Historii stránky se nepodařilo načíst. Zkontrolujte připojení a otevřete historii znovu.',
  sourceLoadError: 'Zdroj stránky se nepodařilo načíst. Zkontrolujte připojení a otevřete zdroj znovu.',
  reviewLoadError: 'Kontrolu sekcí se nepodařilo načíst. Zkontrolujte připojení a otevřete ji znovu.',
  discardAndContinue: 'Zahodit',
  continueEditing: 'Zůstat',
  sourceAriaLabel: 'Zdroj stránky',
  fallbackAriaLabel: label => `${label} (jen pro čtení)`,
  preparing: 'Připravuje se zdroj stránky…',
  editorFailed: 'Editor zdroje se nepodařilo načíst. Text zůstává dostupný jen pro čtení.',
  readOnly: 'Zdroj stránky je jen pro čtení.',
  saving: 'Ukládá se zdroj stránky…',
  loadFailed: 'Zdroj stránky se nepodařilo připravit. Zkontrolujte připojení a zkuste to znovu.',
  noticeSaved: 'Zdroj stránky byl uložen.',
  noticeUnchanged: 'Obsah stránky se nezměnil.',
  noticeInvalid: 'Zdroj obsahuje chyby. Opravte je a použijte změny znovu.',
  noticeConflict: 'Stránku mezitím uložil někdo jiný. Návrh zůstává zachovaný, ale nelze jej použít; zkopírujte si jej a načtěte aktuální verzi.',
  noticeLiveEdits: 'Na stránce jsou neuložené živé úpravy jiných uživatelů. Návrh zůstává zachovaný; zkuste jej použít znovu později.',
  noticeChanged: 'Stránka se mezitím změnila. Návrh vychází ze starší verze; načtěte aktuální verzi.',
  noticeError: 'Uložení se nepodařilo. Návrh zůstává zachovaný.',
  noticeUnavailable: 'Aktuální verzi stránky se nepodařilo převést na zdroj. Zkuste ji načíst znovu.',
  reloadCurrent: 'Načíst aktuální verzi',
  reloadConfirmMessage: 'Načtení aktuální verze stránky zahodí rozpracovaný návrh zdroje.',
  discardDraftAndLoad: 'Zahodit návrh a načíst',
  diagnosticsLabel: 'Problémy ve zdroji',
  errorKind: 'Chyba',
  warningKind: 'Upozornění',
  diagnosticLine: (line, message) => `Řádek ${line}: ${message}`,
  dirtyHint: 'Rozpracované změny. Použít nahradí stránku jako novou revizi.',
  cleanHint: 'Beze změn.',
  discardChanges: 'Zahodit změny',
} satisfies PanelsStrings)
