import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `surface` section of `MarkdownStrings` (see ../strings.ts). */
export interface SurfaceStrings {
  /** Fallback `aria-label` for the legacy `PoolEditor` when the host supplies none. */
  editorAriaLabel: string
  /** Fallback placeholder for the legacy `PoolEditor`. */
  legacyPlaceholder: string
  /** Fallback placeholder for `MarkdownEditor`/`EditorSurface`. */
  placeholder: string
  /** Placeholder of an empty heading with the caret in it. */
  headingPlaceholder: string
  /** Slash-menu item inserting an image (legacy `PoolEditor` and `MarkdownEditor`). */
  imageSlashTitle: string
  imageSlashDescription: string
  closeUploadError: string
  uploadFailed: string
  /** Word count suffix (word/words), Czech plural rules. */
  wordsLabel: (count: number) => string
  charactersUnit: string
  escReturnHint: string
  blockControlHint: string
  contentUnsafeError: string
  projectionFailedError: string
  fileDropError: string
  inputPasteError: string
  /** Status while files are being dropped into the editor, e.g. "Vkládám soubory… (2)". */
  droppingFiles: (count: number) => string
  /** The action of the notice that a colleague removed the block being typed in. */
  restoreRemovedBlock: string
  richDocumentLossyTitle: string
  markdownLosslessTitle: string
  richDocumentLabel: string
  markdownLabel: string
  sourcePanelAriaLabel: string
  sourceLossyNote: string
  sourceTextareaAriaLabel: string
  pluginPanelFailed: string
  detailAriaLabel: string
  detailTabsAriaLabel: string
  detailCloseAriaLabel: string
}

export const surfaceStrings: SurfaceStrings = registerMarkdownStrings('surface', {
  editorAriaLabel: 'Editor Markdown',
  legacyPlaceholder: 'Napište \'/\' pro příkazy…',
  placeholder: 'Pište, nebo napište „/“ pro bloky…',
  headingPlaceholder: 'Nadpis',
  imageSlashTitle: 'Obrázek',
  imageSlashDescription: 'Nahrát obrázek',
  closeUploadError: 'Zavřít chybu',
  uploadFailed: 'Upload obrázku selhal.',
  wordsLabel: count => (count === 1 ? 'slovo' : count >= 2 && count <= 4 ? 'slova' : 'slov'),
  charactersUnit: 'znaků',
  escReturnHint: 'Esc pro návrat',
  blockControlHint: 'Alt+F10: ovládání bloku',
  contentUnsafeError: 'Obsah dokumentu nelze bezpečně zpracovat. Úpravy jsou pozastavené.',
  projectionFailedError: 'Náhled nebo export dokumentu se nepodařilo vytvořit.',
  fileDropError: 'Soubor se nepodařilo vložit.',
  inputPasteError: 'Obsah se nepodařilo vložit.',
  droppingFiles: count => `Vkládám soubory… (${count})`,
  restoreRemovedBlock: 'Vrátit',
  richDocumentLossyTitle: 'Obsah nelze převést do Markdownu bez ztráty. Úplná data zůstávají v dokumentu.',
  markdownLosslessTitle: 'Aktuální obsah lze převést do Markdownu bez ztráty.',
  richDocumentLabel: 'Rich dokument',
  markdownLabel: 'Markdown',
  sourcePanelAriaLabel: 'Zdroj Markdownu, pouze pro čtení',
  sourceLossyNote: 'Rozšířené komponenty mají v Markdownu zjednodušenou podobu. Úplná data zůstávají v dokumentu.',
  sourceTextareaAriaLabel: 'Markdown dokumentu',
  pluginPanelFailed: 'Doplněk se nepodařilo zobrazit.',
  detailAriaLabel: 'Detail komponenty',
  detailTabsAriaLabel: 'Pracovní pohledy',
  detailCloseAriaLabel: 'Zavřít detail',
} satisfies SurfaceStrings)
