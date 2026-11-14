import { registerMarkdownStrings, type MarkdownStrings } from '../../core'
import type { AttachmentsEntryKey } from './strings'
import './strings'

/** Default (Czech) text of the `attachments` section that only the lazy runtime renders. */
export const attachmentsStrings: MarkdownStrings['attachments'] = registerMarkdownStrings('attachments', {
  replaceFileTitle: 'Nahradit soubor',
  uploadFileTitle: 'Nahrát soubor',
  choosePlaceholder: 'Vyberte soubor ze zařízení',
  replaceHint: 'Příloha se změní až po dokončení nahrávání.',
  newUploadHint: 'Název a velikost doplníme ze souboru.',
  cancelUpload: 'Zrušit nahrávání',
  uploadCancelled: 'Nahrávání zrušeno.',
  uploading: name => `Načítání ${name}…`,
  uploadFailedGeneric: 'Nahrávání se nezdařilo. Vyberte soubor a zkuste to znovu.',
  downloadAriaLabel: name => `Stáhnout přílohu ${name}`,
  noFileAttached: 'Soubor zatím není připojený',
  editAttachmentTitle: 'Upravit přílohu',
  fieldFileName: 'Název souboru',
  fieldFileUrl: 'Odkaz na soubor',
  fieldDescription: 'Popis',
  replaceShort: 'Nahradit',
  dropHint: 'nebo přetáhněte soubor sem',
  addDescription: 'Přidat popis…',
  more: 'Další',
  addUrl: 'Zadat odkaz na soubor',
} satisfies Omit<MarkdownStrings['attachments'], AttachmentsEntryKey>)
