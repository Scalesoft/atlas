import type { MarkdownImportDegradedKind } from '../core'
import { inputStrings } from './strings/input'

/** What the user reads for each kind of construct an import kept as literal text. */
export const degradedKindLabels: Readonly<Record<MarkdownImportDegradedKind, string>> = {
  get 'wiki-link'() { return inputStrings.wikiLinkLabel },
  get embed() { return inputStrings.embedLabel },
  get html() { return inputStrings.htmlLabel },
  get comment() { return inputStrings.commentLabel },
  get footnote() { return inputStrings.footnoteLabel },
  get highlight() { return inputStrings.highlightLabel },
  get subscript() { return inputStrings.subscriptLabel },
  get superscript() { return inputStrings.superscriptLabel },
  get math() { return inputStrings.mathLabel },
  get toc() { return inputStrings.tocLabel },
}

/** "N částí bylo vloženo jako text" with Czech plural agreement. */
export function degradedNoticeTitle(count: number): string {
  return inputStrings.degradedNoticeTitle(count)
}
