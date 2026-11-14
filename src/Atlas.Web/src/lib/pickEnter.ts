/**
 * The Enter that picks the preselected first result of a search, picker or command list. Not while an input method composes
 * a character (that Enter confirms the composition), and not with a modifier, which keeps its own meaning (Ctrl+Enter asks the
 * AI or submits, Shift+Enter is a new line).
 */
export function isPickEnter(event: KeyboardEvent): boolean {
  return event.key === "Enter" && !event.isComposing && event.keyCode !== 229 && !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey;
}

/**
 * The picker's Enter in its search field: once something is `typed`, a pick Enter is taken (it never submits a form around the
 * picker) and `pick` chooses the preselected first result, which may still decline (nothing matches, results still loading).
 */
export function pickOnEnter(event: KeyboardEvent, typed: unknown, pick: () => void): void {
  if (!isPickEnter(event) || !typed) return;
  event.preventDefault();
  pick();
}

/** Down from a picker's search: onto the preselected first result (`[data-first]`), else onto the first `fallback`. */
export function focusFirst(container: Element | null | undefined, fallback: string): void {
  (container?.querySelector<HTMLElement>("[data-first]") ?? container?.querySelector<HTMLElement>(fallback))?.focus();
}
