import { nextTick } from "vue";

/**
 * Changes the whole screen (the sign-in screen and the wiki, either way) with a cross-fade instead of cutting it over,
 * where the browser has view transitions (the timing is refresh-theme.css §6). `change` runs exactly once, at once
 * without them.
 *
 * The cross-fade is decoration and never the owner of the change:
 * - the browser may skip a transition (a hidden tab, a resize, a newer transition); `ready` then rejects with an
 *   AbortError and `finished` may too. Every promise of the transition that is not awaited is handled, so a skip
 *   is silent, and the change still runs once;
 * - a throw of `change` reaches the caller as it did before the cross-fade existed, by rejecting the promise returned
 *   here once (the transition's own `updateCallbackDone` carries it), with or without view transitions;
 * - a click during the fade is not lost: the browser hit-tests the page as <html> meanwhile, so a click that lands there
 *   ends the fade and is replayed on the control under the pointer (below).
 *
 * The page's own "Omezit animace" is the attribute `data-atlas-reduced-motion` on <html>, and the update of a sign-out
 * resets the account's appearance, which removes it before the cross-fade starts: the fade would run at the normal
 * 200 ms of somebody who asked for less. The setting is therefore read before the change and kept on a second attribute
 * (`data-crossfade-reduced`, refresh-theme.css §6) until the transition has finished or failed.
 */
export async function crossFade(change: () => void): Promise<void> {
  const root = document.documentElement;
  let transition: ViewTransition | undefined;
  if (typeof document.startViewTransition === "function") {
    if (root.hasAttribute("data-atlas-reduced-motion")) root.setAttribute("data-crossfade-reduced", "");
    try {
      transition = document.startViewTransition(() => { change(); return nextTick(); });
    } catch {
      // The browser refuses to start one (a document that is not active): the change is made without it.
      root.removeAttribute("data-crossfade-reduced");
    }
  }
  if (!transition) { change(); return; }
  const running = transition;
  // For the length of the fade the browser hit-tests the page as <html> (measured in Chromium, also with pointer-events: none
  // on the overlay), so a click on a control of the screen that has just arrived lands on the document and is lost. It ends the
  // fade at once and activates what is under the pointer, as the click would have: the field it is in gets the focus, and the
  // click is sent to the very node under the pointer, with its place and modifier keys. That node is often the icon of a
  // control, an SVG element, which has no click() of its own; a dispatched click bubbles to the control from any node. A click
  // that reached a control (an engine that lets it through) is not touched.
  const missed = (event: MouseEvent) => {
    if (event.target !== root) return;
    const { clientX, clientY, ctrlKey, metaKey, shiftKey, altKey } = event;
    running.skipTransition();
    requestAnimationFrame(() => {
      const hit = document.elementFromPoint(clientX, clientY);
      if (!hit || hit === root || hit === document.body) return;
      hit.closest<HTMLElement>("input, textarea, select, [contenteditable]:not([contenteditable='false'])")?.focus();
      hit.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, composed: true, clientX, clientY, ctrlKey, metaKey, shiftKey, altKey, detail: 1 }));
    });
  };
  document.addEventListener("click", missed, true);
  const release = () => { root.removeAttribute("data-crossfade-reduced"); document.removeEventListener("click", missed, true); };
  // `ready` is not awaited and rejects when the transition is skipped or its update fails; `finished` is only watched.
  transition.ready.catch(() => undefined);
  transition.finished.then(release, release);
  await transition.updateCallbackDone;
}
