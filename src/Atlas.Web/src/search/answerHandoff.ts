import { animateAtlas, motionMs, prefersReducedMotion, relocate } from "../motion/aiMotion";
/** A visual copy travels above the search dialog; Vue retains ownership of both real answer trees. */
export async function moveAnswerToPanel(origin: HTMLElement, target: HTMLElement) {
  if (!origin.isConnected || !target.isConnected) return;
  if (prefersReducedMotion(origin)) {
    origin.style.visibility = "hidden";
    try { await animateAtlas(target, [{ opacity: 0 }, { opacity: 1 }], { duration: motionMs(target, "--atlas-motion-base") })?.finished.catch(() => {}); }
    finally { origin.style.visibility = ""; }
    return;
  }
  const from = origin.getBoundingClientRect(), to = target.getBoundingClientRect();
  const copy = origin.cloneNode(true) as HTMLElement;
  copy.setAttribute("aria-hidden", "true"); copy.setAttribute("inert", ""); copy.setAttribute("popover", "manual");
  Object.assign(copy.style, { position: "fixed", inset: "auto", left: from.left + "px", top: from.top + "px", width: from.width + "px", height: from.height + "px", margin: "0", padding: "0", border: "0", background: "var(--surface)", overflow: "hidden", pointerEvents: "none" });
  document.querySelector(".atlas")?.appendChild(copy);
  copy.showPopover?.();
  origin.style.visibility = "hidden"; target.style.visibility = "hidden";
  try {
    const movement = relocate(copy, () => Object.assign(copy.style, { left: to.left + "px", top: to.top + "px", width: to.width + "px", height: to.height + "px" }));
    await movement?.finished.catch(() => {});
    target.style.visibility = "";
    const fade = animateAtlas(copy, [{ opacity: 1 }, { opacity: 0 }], { duration: motionMs(copy, "--atlas-motion-base") });
    const arrival = animateAtlas(target, [{ opacity: 0 }, { opacity: 1 }], { duration: motionMs(target, "--atlas-motion-base") });
    await Promise.all([fade?.finished.catch(() => {}), arrival?.finished.catch(() => {})]);
  } finally { copy.remove(); origin.style.visibility = ""; target.style.visibility = ""; }
}
