import { registerMarkdownStrings, type MarkdownStrings } from '../../core'

/** Default (Czech) text of the `math` section that the plugin entry renders. */
const entry = {
  expressionRequired: 'Vyplňte matematický výraz LaTeX (nejvýše 10 000 znaků).',
  blockTitle: 'Matematický vzorec',
  blockDescription: 'Vzorec LaTeX na samostatném řádku',
  inlineTitle: 'Vzorec v textu',
  inlineDescription: 'Vzorec LaTeX uprostřed věty',
} satisfies Partial<MarkdownStrings['math']>
/** Keys registered here; ./runtimeStrings adds the rest when the lazy runtime loads. */
export type MathEntryKey = keyof typeof entry
export const mathStrings: Pick<MarkdownStrings['math'], MathEntryKey> = registerMarkdownStrings('math', entry)
