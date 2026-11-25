import { watch, type Ref } from "vue";

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

/**
 * The close rules of every menu (ActionMenu, ActionDisclosure; the September UI audit), which reka-ui does not have: a menu never
 * outlives the route underneath it, and a menu whose anchor the reader scrolled out of sight is closed rather than relocated (reka-ui
 * returns the focus to the trigger, the rest of that behaviour). Listens only while the menu is open.
 */
export function closeMenuWhenLost(open: Ref<boolean>, anchor: () => Element | null | undefined) {
  const close = () => { open.value = false; };
  const closeIfAnchorLeft = () => {
    const rect = anchor()?.getBoundingClientRect();
    if (rect && (rect.bottom < 0 || rect.top > window.innerHeight)) close();
  };
  watch(open, (isOpen, _, onCleanup) => {
    if (!isOpen) return;
    window.addEventListener("hashchange", close);
    document.addEventListener("scroll", closeIfAnchorLeft, true);
    window.addEventListener("resize", closeIfAnchorLeft);
    onCleanup(() => {
      window.removeEventListener("hashchange", close);
      document.removeEventListener("scroll", closeIfAnchorLeft, true);
      window.removeEventListener("resize", closeIfAnchorLeft);
    });
  }, { immediate: true });
}
