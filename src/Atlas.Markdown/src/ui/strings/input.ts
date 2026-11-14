import { registerMarkdownStrings } from '../strings'

/** Default (Czech) text for the `input` section of `MarkdownStrings` (see ../strings.ts). */
export interface InputStrings {
  /** Import-degraded-construct label: a wiki-style `[[link]]`. */
  wikiLinkLabel: string
  /** Import-degraded-construct label: an embedded file or page. */
  embedLabel: string
  /** Import-degraded-construct label: raw HTML. */
  htmlLabel: string
  /** Import-degraded-construct label: an HTML comment. */
  commentLabel: string
  footnoteLabel: string
  highlightLabel: string
  subscriptLabel: string
  superscriptLabel: string
  mathLabel: string
  /** Import-degraded-construct label: a table-of-contents directive. */
  tocLabel: string
  /** "N částí bylo vloženo jako text" with Czech plural agreement. */
  degradedNoticeTitle: (count: number) => string
  /** Default callout title when a GitHub/Obsidian/Wiki.js alert has none. */
  calloutNote: string
  calloutTip: string
  calloutWarning: string
  calloutImportant: string
  calloutCaution: string
  /** Shown when a table paste exceeds the supported dimensions or attributes. */
  tablePasteRejected: string
  /** Markdown dialect (`a-` components): an `a-` fence or link name the editor does not know. */
  dialectUnknownComponent: (name: string) => string
  /** Markdown dialect: a component whose data failed validation, with the validator's reason. */
  dialectInvalidComponent: (name: string, reason: string) => string
  dialectInvalidLink: string
  /** A key the component does not carry. */
  dialectUnknownKey: (key: string) => string
  /** The body is not the shape the component expects (a map, or a sequence for `a-contact`). */
  dialectInvalidShape: string
  /** An a-YAML error at a line of the component's body (1-based). */
  dialectYamlAt: (line: number, reason: string) => string
  dialectYamlUnsupported: string
  dialectYamlIndentation: string
  dialectYamlTooDeep: string
  dialectYamlDuplicateKey: (key: string) => string
}

/** Keys the lazy Markdown dialect registers when it loads (./inputDialect). */
export type DialectStringKey = Extract<keyof InputStrings, `dialect${string}`>

export const inputStrings: InputStrings = registerMarkdownStrings('input', {
  wikiLinkLabel: 'Wiki odkaz',
  embedLabel: 'Vložený soubor nebo stránka',
  htmlLabel: 'HTML',
  commentLabel: 'Komentář',
  footnoteLabel: 'Poznámka pod čarou',
  highlightLabel: 'Zvýraznění',
  subscriptLabel: 'Dolní index',
  superscriptLabel: 'Horní index',
  mathLabel: 'Matematický vzorec',
  tocLabel: 'Obsah stránky',
  degradedNoticeTitle: count => count === 1 ? '1 část byla vložena jako text'
    : count >= 2 && count <= 4 ? `${count} části byly vloženy jako text`
    : `${count} částí bylo vloženo jako text`,
  calloutNote: 'Poznámka',
  calloutTip: 'Tip',
  calloutWarning: 'Upozornění',
  calloutImportant: 'Důležité',
  calloutCaution: 'Pozor',
  tablePasteRejected: 'Obsah nelze vložit: překračuje povolené rozměry tabulky nebo obsahuje nepodporované atributy.',
} satisfies Omit<InputStrings, DialectStringKey>)
