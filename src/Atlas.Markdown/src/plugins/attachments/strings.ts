import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `attachments` section that the plugin entry renders. */
const entry = {
  label: 'Příloha',
  slashDescription: 'Soubor ke stažení: PDF, dokument, archiv',
  newAttachmentName: 'Nová příloha',
  invalidAttachment: 'Neplatná příloha.',
  invalidNameOrVersion: 'Vyplňte název přílohy a podporovanou verzi.',
  invalidUrl: 'Neplatná adresa přílohy.',
  invalidSize: 'Velikost musí být nezáporný počet bajtů.',
  invalidMimeType: 'Neplatný typ souboru.',
  invalidDescription: 'Neplatný popis přílohy.',
  downloadAttachment: 'Stáhnout přílohu',
  uploadMissingUrl: 'Upload nevrátil adresu přílohy.',
  download: 'Stáhnout',
} satisfies Partial<MarkdownStrings['attachments']>
/** Keys registered here; ./runtimeStrings adds the rest when the lazy runtime loads. */
export type AttachmentsEntryKey = keyof typeof entry
export const attachmentsStrings: Pick<MarkdownStrings['attachments'], AttachmentsEntryKey> = registerMarkdownStrings('attachments', entry)
