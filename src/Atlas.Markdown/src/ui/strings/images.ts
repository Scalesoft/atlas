import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `images` section of `MarkdownStrings` (see ../strings.ts). */
export interface ImagesStrings {
  /** Toolbar/menu button and popover title: the layout (size + alignment) panel. */
  layoutPanelTitle: string
  /** Toolbar/menu button and popover title: the appearance (filters/adjustments) panel. */
  effectsPanelTitle: string
  /** Toolbar/menu button and popover title: the crop panel. */
  cropPanelTitle: string
  /** Toolbar/menu button and popover title: the caption/alt-text panel. */
  textPanelTitle: string
  /** Toolbar/menu button and popover title: the replace-image panel. */
  replacePanelTitle: string
  alignLabel: string
  alignLeft: string
  alignCenter: string
  alignRight: string
  widthLabel: string
  filterLabel: string
  filterOriginal: string
  filterGrayscale: string
  filterSepia: string
  adjustmentsLabel: string
  brightnessLabel: string
  contrastLabel: string
  saturationLabel: string
  cropHint: string
  aspectRatioLabel: string
  aspectRatioFull: string
  captionLabel: string
  altTextLabel: string
  altTextHint: string
  newImageUrlLabel: string
  replaceHint: string
  checkImageSettings: string
  zoomedImageLabel: string
  cornerTopLeft: string
  cornerTopRight: string
  cornerBottomLeft: string
  cornerBottomRight: string
  /** "Ořez: {corner} roh" aria-label for a crop-handle button. */
  cropCornerLabel: (corner: string) => string
  dragCornerHint: string
  preparingImage: string
  exportFailed: string
  resizeWidthLabel: string
  resizeWidthFromLeftLabel: string
  resizeHint: string
  toolbarLabel: string
  rotateLabel: string
  flipLabel: string
  zoomLabel: string
  moreOptionsLabel: string
  moreOptionsTitle: string
  downloadEditedLabel: string
  resetAppearanceLabel: string
  removeImageLabel: string
  downloadRequiresCors: string
  invalidImageGeometry: string
  exportNotSupported: string
  pngCreationFailed: string
  invalidPresentation: string
  adjustmentOutOfRange: string
  cropMustKeepMinimum: string
  invalidSourceDimensions: string
  /** Drop-preview status text, e.g. "Nahrávám obrázek…". */
  filePreviewStatus: (kind: 'image' | 'attachment' | 'diagram' | 'text') => string
  textFileTooLarge: string
  importCancelled: string
  fileReadFailed: string
  uploadInvalidUrl: string
  markdownImportDisabled: string
  importLimitExceeded: string
  noAttachmentStorage: string
  fileInsertFailed: string
  filesInsertFailed: string
  uploadCancelled: string
  uploadFailed: string
  /** "Upload selhal: {status}" for the default HTTP upload adapter. */
  httpUploadFailed: (status: number) => string
  invalidImageDimensions: string
  invalidImageAddress: string
  dataSourceNotAllowed: string
  blobSourceNotAllowed: string
  originNotAllowed: string
  protocolRelativeNotAllowed: string
  httpRequired: string
  relativeNotAllowed: string
  tooManyFiles: string
  invalidFileSize: string
  totalSizeExceeded: string
  dimensionsExceedLimit: string
}

export const imagesStrings: ImagesStrings = registerMarkdownStrings('images', {
  layoutPanelTitle: 'Velikost a zarovnání',
  effectsPanelTitle: 'Vzhled obrázku',
  cropPanelTitle: 'Oříznout obrázek',
  textPanelTitle: 'Popisek a alternativní text',
  replacePanelTitle: 'Nahradit obrázek',
  alignLabel: 'Zarovnání',
  alignLeft: 'Vlevo',
  alignCenter: 'Na střed',
  alignRight: 'Vpravo',
  widthLabel: 'Šířka',
  filterLabel: 'Barevný filtr',
  filterOriginal: 'Originál',
  filterGrayscale: 'Černobílý',
  filterSepia: 'Sépie',
  adjustmentsLabel: 'Úpravy',
  brightnessLabel: 'Jas',
  contrastLabel: 'Kontrast',
  saturationLabel: 'Sytost',
  cropHint: 'Ořez vyberte tažením za rohy obrázku.',
  aspectRatioLabel: 'Poměr stran',
  aspectRatioFull: 'Celý',
  captionLabel: 'Popisek pod obrázkem',
  altTextLabel: 'Alternativní text',
  altTextHint: 'Stručně popište obrázek pro čtečky obrazovky.',
  newImageUrlLabel: 'Adresa nového obrázku',
  replaceHint: 'Popisek i nastavení vzhledu zůstanou zachované.',
  checkImageSettings: 'Zkontrolujte nastavení obrázku.',
  zoomedImageLabel: 'Zvětšený obrázek',
  cornerTopLeft: 'levý horní',
  cornerTopRight: 'pravý horní',
  cornerBottomLeft: 'levý dolní',
  cornerBottomRight: 'pravý dolní',
  cropCornerLabel: corner => `Ořez: ${corner} roh`,
  dragCornerHint: 'Táhněte roh pro ořez · šipky pro posun',
  preparingImage: 'Připravuji obrázek…',
  exportFailed: 'Export obrázku se nezdařil.',
  resizeWidthLabel: 'Změnit šířku obrázku',
  resizeWidthFromLeftLabel: 'Změnit šířku obrázku zleva',
  resizeHint: 'Táhněte pro změnu velikosti · šipky pro jemné úpravy',
  toolbarLabel: 'Obrázek',
  rotateLabel: 'Otočit o 90°',
  flipLabel: 'Převrátit vodorovně',
  zoomLabel: 'Zvětšit',
  moreOptionsLabel: 'Další možnosti obrázku',
  moreOptionsTitle: 'Možnosti obrázku',
  downloadEditedLabel: 'Stáhnout upravený',
  resetAppearanceLabel: 'Obnovit vzhled',
  removeImageLabel: 'Smazat obrázek',
  downloadRequiresCors: 'Stažení vyžaduje obrázek ze stejného webu nebo server s povoleným CORS.',
  invalidImageGeometry: 'Obrázek nemá platné rozměry.',
  exportNotSupported: 'Prohlížeč nepodporuje export obrázku.',
  pngCreationFailed: 'Prohlížeč nedokázal vytvořit PNG obrázek.',
  invalidPresentation: 'Neplatné nastavení obrázku.',
  adjustmentOutOfRange: 'Hodnota úpravy obrázku je mimo povolený rozsah.',
  cropMustKeepMinimum: 'Ořez musí zachovat alespoň 5 % obrázku.',
  invalidSourceDimensions: 'Neplatné rozměry zdrojového obrázku.',
  filePreviewStatus: kind => `${kind === 'image' ? 'Nahrávám obrázek' : kind === 'attachment' ? 'Nahrávám přílohu' : kind === 'diagram' ? 'Vkládám diagram' : 'Vkládám text'}…`,
  textFileTooLarge: 'Textový soubor je příliš velký (maximum 5 MiB).',
  importCancelled: 'Import zrušen.',
  fileReadFailed: 'Soubor se nepodařilo přečíst.',
  uploadInvalidUrl: 'Upload nevrátil platnou adresu obrázku.',
  markdownImportDisabled: 'Editor nemá zapnutý import Markdownu.',
  importLimitExceeded: 'Limit importu byl překročen.',
  noAttachmentStorage: 'Pro tento soubor není připojené úložiště příloh.',
  fileInsertFailed: 'Soubor se nepodařilo vložit.',
  filesInsertFailed: 'Soubory se nepodařilo vložit do dokumentu.',
  uploadCancelled: 'Upload zrušen.',
  uploadFailed: 'Upload obrázku selhal. Zkuste to znovu nebo zkontrolujte připojení.',
  httpUploadFailed: status => `Upload selhal: ${status}`,
  invalidImageDimensions: 'Rozměry obrázku nelze bezpečně ověřit z podporované hlavičky PNG, JPEG, GIF nebo WebP.',
  invalidImageAddress: 'Obrázek obsahuje neplatnou adresu.',
  dataSourceNotAllowed: 'Datový zdroj obrázku není povolený.',
  blobSourceNotAllowed: 'Dočasný zdroj obrázku není povolený.',
  originNotAllowed: 'Původ obrázku není povolený.',
  protocolRelativeNotAllowed: 'Při omezení původu musí mít obrázek výslovné schéma HTTP nebo HTTPS.',
  httpRequired: 'Adresa obrázku musí používat HTTP nebo HTTPS bez přihlašovacích údajů.',
  relativeNotAllowed: 'Relativní zdroj obrázku není povolený.',
  tooManyFiles: 'Počet souborů překračuje nastavený limit.',
  invalidFileSize: 'Neplatná velikost souboru.',
  totalSizeExceeded: 'Celková velikost souborů překračuje nastavený limit.',
  dimensionsExceedLimit: 'Rozměry obrázku překračují povolený limit.',
} satisfies ImagesStrings)
