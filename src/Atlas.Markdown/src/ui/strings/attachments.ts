/** Keys of the `attachments` section of `MarkdownStrings`; the Czech defaults live in plugins/attachments/. */
export interface AttachmentsStrings {
  /** Slash menu item and toolbar button label for the attachment block module. */
  label: string
  /** Second line of the slash menu item. */
  slashDescription: string
  /** Default name shown for a newly inserted attachment before the user renames it. */
  newAttachmentName: string
  invalidAttachment: string
  invalidNameOrVersion: string
  invalidUrl: string
  invalidSize: string
  invalidMimeType: string
  invalidDescription: string
  /** Link text in the read-only (fallback HTML) rendering of an attachment. */
  downloadAttachment: string
  /** Thrown when a host upload service resolves without a usable file address. */
  uploadMissingUrl: string
  /** Upload popover title when an attachment already has a file. */
  replaceFileTitle: string
  /** Upload popover title when an attachment has no file yet. */
  uploadFileTitle: string
  choosePlaceholder: string
  replaceHint: string
  newUploadHint: string
  cancelUpload: string
  uploadCancelled: string
  /** Upload progress status, e.g. "Načítání report.pdf…". */
  uploading: (name: string) => string
  uploadFailedGeneric: string
  /** Download link text on the rendered attachment card. */
  download: string
  /** "Stáhnout přílohu {name}" aria-label for the download link. */
  downloadAriaLabel: (name: string) => string
  noFileAttached: string
  editAttachmentTitle: string
  fieldFileName: string
  fieldFileUrl: string
  fieldDescription: string
  /** Small secondary button on a card that already has a file. */
  replaceShort: string
  /** Hint beside the upload button on an empty card. */
  dropHint: string
  addDescription: string
  /** Collapsed section holding the technical file URL. */
  more: string
  addUrl: string
}
