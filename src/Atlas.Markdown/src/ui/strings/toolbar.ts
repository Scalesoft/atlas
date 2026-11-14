import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `toolbar` section of `MarkdownStrings` (see ../strings.ts). */
export interface ToolbarStrings {
  /** Block-kind bubble menu item: switch the current block to a plain paragraph. */
  textBlockLabel: string
  heading1Label: string
  heading2Label: string
  heading3Label: string
  heading4Label: string
  heading5Label: string
  heading6Label: string
  /** "Typ bloku: {label}" title on the block-kind trigger button. */
  blockTypeTitle: (label: string) => string
  blockTypeMenuAriaLabel: string
  formattingGroupAriaLabel: string
  /** The reader's bubble over a text selection (J5.4): only the read actions. */
  readerGroupAriaLabel: string
  copyTitle: string
  /** Copies the link of the heading the reader's selection starts in. */
  copyLinkTitle: string
  /** Said for a moment by the copy button that copied. */
  copiedTitle: string
  boldTitle: string
  italicTitle: string
  underlineTitle: string
  strikeTitle: string
  highlightTitle: string
  inlineCodeTitle: string
  linkTitle: string
  bulletListTitle: string
  orderedListTitle: string
  taskListTitle: string
  timedListTitle: string
  blockquoteTitle: string
  codeBlockTitle: string
  alignLeftTitle: string
  alignCenterTitle: string
  alignRightTitle: string
  editLinkAriaLabel: string
  linkAddressLabel: string
  linkPlaceholder: string
  linkTargetLabel: string
  linkSearchPlaceholder: string
  linkResultsLabel: string
  linkNoPage: string
  linkSearchError: string
  removeLinkLabel: string
  linkSaveError: string
  pluginToolbarAriaLabel: string
  pluginOverflowLabel: string
  pluginOverflowSrLabel: string
}

export const toolbarStrings: ToolbarStrings = registerMarkdownStrings('toolbar', {
  textBlockLabel: 'Text',
  heading1Label: 'Nadpis 1',
  heading2Label: 'Nadpis 2',
  heading3Label: 'Nadpis 3',
  heading4Label: 'Nadpis 4',
  heading5Label: 'Nadpis 5',
  heading6Label: 'Nadpis 6',
  blockTypeTitle: label => `Typ bloku: ${label}`,
  blockTypeMenuAriaLabel: 'Typ bloku',
  formattingGroupAriaLabel: 'Formátování textu',
  readerGroupAriaLabel: 'Akce s výběrem',
  copyTitle: 'Kopírovat',
  copyLinkTitle: 'Kopírovat odkaz',
  copiedTitle: 'Zkopírováno',
  boldTitle: 'Tučně (Ctrl+B)',
  italicTitle: 'Kurzíva (Ctrl+I)',
  underlineTitle: 'Podtržené (Ctrl+U)',
  strikeTitle: 'Přeškrtnuté',
  highlightTitle: 'Zvýraznit',
  inlineCodeTitle: 'Inline kód',
  linkTitle: 'Odkaz',
  bulletListTitle: 'Odrážky',
  orderedListTitle: 'Číslovaný seznam',
  taskListTitle: 'Checklist',
  timedListTitle: 'Časová odrážka (Ctrl+Shift+D)',
  blockquoteTitle: 'Citace',
  codeBlockTitle: 'Blok kódu',
  alignLeftTitle: 'Doleva',
  alignCenterTitle: 'Na střed',
  alignRightTitle: 'Doprava',
  editLinkAriaLabel: 'Upravit odkaz',
  linkAddressLabel: 'Adresa odkazu',
  linkPlaceholder: 'https://…',
  linkTargetLabel: 'Stránka nebo adresa',
  linkSearchPlaceholder: 'Název stránky nebo https://…',
  linkResultsLabel: 'Stránky wiki',
  linkNoPage: 'Žádná stránka tohoto názvu. Adresu napište s https://.',
  linkSearchError: 'Stránky se nepodařilo načíst.',
  removeLinkLabel: 'Odebrat odkaz',
  linkSaveError: 'Odkaz nelze uložit. Zkontrolujte adresu (https://…, mailto:…, tel:… nebo relativní cesta).',
  pluginToolbarAriaLabel: 'Doplňky editoru',
  pluginOverflowLabel: 'Další',
  pluginOverflowSrLabel: 'pluginové akce',
} satisfies ToolbarStrings)
