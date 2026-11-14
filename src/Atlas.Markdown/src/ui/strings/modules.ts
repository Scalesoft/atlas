import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `modules` section of `MarkdownStrings` (see ../strings.ts). */
export interface ModulesStrings {
  /** Fallback readable content shown before a module's own view has loaded. */
  textPreview: string
  loadingFullView: string
  fullViewUnavailable: string
  renderFailed: string
  waitingForFullView: string
  preparingNewPreview: string
  /** Context-toolbar button: "Upravit: {label}". */
  editBlockLabel: (label: string) => string
  /** Context-toolbar button: "Smazat: {label}" (P12: a block deleted from the page). */
  removeBlockLabel: (label: string) => string
  /** Toolbar button of an inline chip (contact, issue, page, emoji…): back to its visible text; `keys` is the shortcut („Ctrl+Shift+Mezerník“). */
  chipToTextLabel: (keys: string) => string
  /** The keys an editable inline chip names in its tooltip: back to text, the actions menu. */
  chipHint: (toText: string, actions: string) => string
  checkValuesEntered: string
  urlRequired: string
  urlProtocolRequired: string
  /** A component's unfinished input lost when writing stopped (a lock, a revoked right, offline), named by the component's `label`. */
  draftNotSaved: (label: string) => string
  /** What everyone may do with a block (a reader too): the actions' group name, zoom, and the result of a copy. */
  readActions: string
  zoomBlock: string
  /** "Kopírovat {what}" of a block: its data, its source, its TeX. */
  copyWhat: (what: string) => string
  copied: string
  copyFailed: string
}

export const modulesStrings: ModulesStrings = registerMarkdownStrings('modules', {
  textPreview: 'Textový náhled; úplné zobrazení se načte při přiblížení.',
  loadingFullView: 'Načítání úplného zobrazení; textový náhled zůstává dostupný.',
  fullViewUnavailable: 'Úplné zobrazení není dostupné; textový náhled je zachován.',
  renderFailed: 'Zobrazení se nepodařilo načíst.',
  waitingForFullView: 'Čekání na úplné zobrazení; textový náhled zůstává dostupný.',
  preparingNewPreview: 'Připravuje se nový náhled; toto je aktuální uložený obsah.',
  editBlockLabel: label => `Upravit: ${label}`,
  removeBlockLabel: label => `Smazat: ${label}`,
  chipToTextLabel: keys => `Převést na text (${keys})`,
  chipHint: (toText, actions) => `${toText} převede na text · ${actions} nabídne, co s ním jde dělat`,
  checkValuesEntered: 'Zkontrolujte zadané hodnoty.',
  urlRequired: 'Vyplňte adresu souboru nebo webu.',
  urlProtocolRequired: 'Adresa musí používat HTTP nebo HTTPS bez přihlašovacích údajů.',
  draftNotSaved: label => `Rozpracovaná úprava (${label}) se neuložila, protože stránku teď nelze upravovat. Po návratu práva ji nabídne znovu.`,
  readActions: 'Akce bloku',
  zoomBlock: 'Zvětšit',
  copyWhat: what => `Kopírovat ${what}`,
  copied: 'Zkopírováno',
  copyFailed: 'Kopírování selhalo',
} satisfies ModulesStrings)
