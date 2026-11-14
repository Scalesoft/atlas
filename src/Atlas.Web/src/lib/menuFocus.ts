/**
 * Every menu opens with its first action lit (the owner's rule: a menu, like a search, preselects its first row, so Enter acts and
 * the arrows start from the top). reka-ui focuses the panel itself on a pointer opening; this is the handler of its `open-auto-focus`:
 * it takes that focus to the first enabled row that is shown (a breakpoint may hide some), and `data-highlighted` follows the focus.
 */
export function focusFirstAction(event: Event) {
  const panel = event.target;
  if (!(panel instanceof HTMLElement)) return;
  const first = [...panel.querySelectorAll<HTMLElement>('[role^="menuitem"]:not([data-disabled])')].find(row => getComputedStyle(row).display !== "none");
  if (!first) return;
  event.preventDefault();
  first.focus({ preventScroll: true });
}
