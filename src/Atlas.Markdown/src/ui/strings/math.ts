/** Keys of the `math` section of `MarkdownStrings`; the Czech defaults live in plugins/math/. */
export interface MathStrings {
  expressionRequired: string
  /** Module label (toolbar/slash item) for the block (display) math module. */
  blockTitle: string
  /** Second line of the slash menu item. */
  blockDescription: string
  /** Module label (toolbar/slash item) for the inline math module. */
  inlineTitle: string
  /** Second line of the slash menu item. */
  inlineDescription: string
  /** KaTeX render-failure status text, e.g. " Vzorec nelze vykreslit: {message}". */
  renderFailed: (message: string) => string
  invalidExpression: string
  editTitle: string
  expressionLabel: string
  /** Close button of the in-place source panel. */
  hideSource: string
  /** Hint under the formula field. */
  editHint: string
}
