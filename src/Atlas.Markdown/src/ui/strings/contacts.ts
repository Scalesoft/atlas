/** Keys of the `contacts` section of `MarkdownStrings`; the Czech defaults live in plugins/contacts/. */
export interface ContactsStrings {
  /** Shared display name of the contacts feature: block/inline module label, manage popover title, suggestion group label. */
  label: string
  /** Second line of the slash menu item. */
  slashDescription: string
  /** Slash menu item and toolbar button label for the inline (in-text) contact module. */
  inlineLabel: string
  /** Second line of the slash menu item. */
  inlineDescription: string
  /** The slash row of a typed name, „{pickLabel}: vyhledat „jan““ (`/kontakt jan`). */
  pickLabel: string
  /** Default name shown for a newly inserted contact before the user fills it in. */
  newContactName: string
  inlineSingleContactOnly: string
  /** Generic field label used in "Neplatné pole: {label}." / "Vyplňte pole: {label}." for the contact name. */
  nameFieldLabel: string
  /** Field label in "Neplatné pole: {label}." for the page-local role of a contact. */
  roleFieldLabel: string
  /** Field label in "Neplatné pole: {label}." for the contact's username. */
  usernameFieldLabel: string
  internalUserNameFieldLabel: string
  internalUserPhotoFieldLabel: string
  /** "Neplatné pole: {label}." for a malformed contact field. */
  invalidField: (label: string) => string
  /** "Vyplňte pole: {label}." for a missing required contact field. */
  requiredField: (label: string) => string
  invalidContact: string
  invalidDisplayKind: string
  addressNoNewline: string
  tooManyPhonesEmails: string
  invalidPhones: string
  invalidEmails: string
  invalidInternalUser: string
  invalidInternalUserId: string
  invalidContactsList: string
  invalidContactsCount: string
  kindAuto: string
  kindInternal: string
  kindExternal: string
  /** "Kontakt: {name}" title of the read-only contact detail popover. */
  readerTitle: (name: string) => string
  /** "Podrobnosti kontaktu {name}" aria-label for the inline contact's trigger button. */
  contactDetailsAriaLabel: (name: string) => string
  web: string
  /** Download-vCard button/link text. */
  downloadCard: string
  /** "Stáhnout vizitku {name}" aria-label for the download button. */
  downloadCardAriaLabel: (name: string) => string
  downloadCardTitle: string
  /** Reader card: the quick actions under the person (the first e-mail and phone). */
  sendEmail: string
  call: string
  /** Reader card: „QR kód“ beside „Stáhnout vizitku“ unfolds the card as a QR code; its accessible name, the hint under it, the fields a dense code left out, a failure. */
  qrCode: string
  qrCodeLabel: (name: string) => string
  qrHint: string
  qrOmitted: (fields: string) => string
  qrFailed: string
  /** Reader card: copy a phone or an e-mail, the link to the directory contact, the places it is used. */
  copyValue: (value: string) => string
  copied: string
  copyFailed: string
  usedOn: string
  usedOnMore: (count: number) => string
  downloadAll: string
  editContactTitle: string
  /** Also used as the manage-popover "add" button and the toolbar "add contact" button label. */
  addContactTitle: string
  fieldFullName: string
  fieldOrganization: string
  fieldTitle: string
  fieldDepartment: string
  /** Form label of the username field; also labels the username on the card and in the reader panel. */
  /** Edit-form label of the page-local role. */
  fieldRole: string
  /** Muted label of a contact that is in the trash or gone from the directory. */
  unavailable: string
  fieldUsername: string
  fieldEmails: string
  fieldPhones: string
  fieldDisplayKind: string
  fieldPhotoUrl: string
  moreDetailsSection: string
  fieldAddress: string
  manageEdit: string
  /** "Upravit kontakt {name}" aria-label. */
  manageEditAriaLabel: (name: string) => string
  manageRemove: string
  /** "Odebrat kontakt {name}" aria-label. */
  manageRemoveAriaLabel: (name: string) => string
  /** Also used as the toolbar "add from directory" button label. */
  addFromDirectory: string
  refreshFromDirectory: string
  searchFieldLabel: string
  searchPlaceholder: string
  noneFound: string
  searchFailed: string
  refreshing: string
  someMissingFromDirectory: string
  refreshFailed: string
  /** Quiet status when a contact could not be linked to the directory yet (the snapshot stays). */
  linkFailed: string
  /** Contact form of a linked contact: the person's data changes in the directory, not on the page. */
  directoryFieldsNote: string
  /** Link from that form to the directory contact. */
  openInDirectory: string
  suggestionHint: string
  suggestionEmpty: string
  /** Selection action in the formatting bubble: the selected name becomes a contact. */
  selectionLabel: string
  /** Why a person of an ambiguous name ranks high: the document holds them already. */
  rankOnPage: string
  /** … they are used in this page's space. */
  rankInSpace: string
  /** … how often they are used. */
  rankUsed: (count: number) => string
  /** … pages close in meaning to this one name them (the host's AI boost). */
  rankRelated: string
  /** Actions menu row: several people have the name, decide later. */
  intentionUnresolved: string
  /** Second line of „Udělat kontakt“ in the actions menu. */
  intentionPickDescription: string
  /** Screen-reader label of an unresolved contact chip. */
  unresolvedAriaLabel: (name: string) => string
  /** The most likely person after an unresolved chip's name, „nejspíš Modelsoft“. */
  unresolvedLikely: (who: string) => string
  /** The others after it, „ · nebo Example“. */
  unresolvedOthers: (who: string) => string
  /** An unresolved chip's person with no organization, function, department or e-mail. */
  unresolvedNoDetail: string
  /** Tooltip of an unresolved chip. */
  unresolvedTitle: (count: number) => string
  /** Screen-reader label of a template's empty place for a person („PM“). */
  placeAriaLabel: (role: string) => string
  /** Tooltip of that place. */
  placeTitle: (role: string) => string
  /** Last row of the people offered for a place: the host's people picker. */
  placeSomeoneElse: string
  /** Accessible label of the „Je to …?“ list at a written name. */
  nameOfferLabel: string
  /** A row of that list, „Je to Jana Nováková“. */
  nameOfferIs: (name: string) => string
  /** Its last row for one person: the name is someone else. */
  nameOfferNo: string
  /** Its last row for several people of that name. */
  nameOfferNone: string
}
