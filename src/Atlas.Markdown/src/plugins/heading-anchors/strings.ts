import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `headingAnchors` section. */
export const headingAnchorsStrings: MarkdownStrings['headingAnchors'] = registerMarkdownStrings('headingAnchors', {
  linkToHeading: 'Odkaz na nadpis',
  copyLink: 'Kopírovat odkaz na nadpis',
  linkCopied: 'Odkaz zkopírován',
} satisfies MarkdownStrings['headingAnchors'])
