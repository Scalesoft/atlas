import { animateAtlas, motionMs, prefersReducedMotion } from "../motion/aiMotion";

/** The attribute that lays the search out as a dialog or as the whole page (command-palette.css). */
const VIEW = "data-search-view";

/** The panel's box and corner in `view`, measured without a frame on screen (the attribute is put back before anything paints). */
function layout(panel: HTMLElement, view: string) {
  const current = panel.getAttribute(VIEW);
  panel.setAttribute(VIEW, view);
  const box = panel.getBoundingClientRect(), radius = getComputedStyle(panel).borderTopLeftRadius || "0px";
  if (current === null) panel.removeAttribute(VIEW); else panel.setAttribute(VIEW, current);
  return { box, radius };
}

/** A clip of `whole` (the panel's border box) down to the dialog's box and corner. */
function clipTo(whole: DOMRect, { box, radius }: { box: DOMRect; radius: string }) {
  return `inset(${Math.round(box.top - whole.top)}px ${Math.round(whole.right - box.right)}px ${Math.round(whole.bottom - box.bottom)}px ${Math.round(box.left - whole.left)}px round ${radius})`;
}

/**
 * The search becomes the whole page or a dialog again (owner 2026-10-28): one panel, two layouts, its content never scales.
 * Growing, `apply` lays the page out at once and the clip opens from the dialog's box (`--atlas-motion-base`, .7 → 1 like the
 * unfolding from its trigger); shrinking, the clip closes onto the dialog's box (`--atlas-motion-surface`, as exits are shorter)
 * and `apply` switches the layout when it gets there. Reduced motion: the switch, then a fade of at most 160 ms. A width
 * transition of the dialog (the command mode's) never runs during the switch.
 */
export async function morphSearchPanel(panel: HTMLElement | null | undefined, view: "dialog" | "page", apply: () => Promise<void>): Promise<void> {
  if (!panel) return apply();
  panel.style.transition = "none";
  try {
    const dialog = layout(panel, "dialog");
    if (view === "page") {
      await apply();
      animateAtlas(panel, [{ clipPath: clipTo(panel.getBoundingClientRect(), dialog), opacity: 0.7 }, { clipPath: "inset(0px)", opacity: 1 }],
        { duration: motionMs(panel, "--atlas-motion-base") });
    } else if (prefersReducedMotion(panel)) {
      await apply();
      animateAtlas(panel, [{ opacity: 0.7 }, { opacity: 1 }], { duration: motionMs(panel, "--atlas-motion-surface") });
    } else {
      const shrink = animateAtlas(panel, [{ clipPath: "inset(0px)" }, { clipPath: clipTo(panel.getBoundingClientRect(), dialog) }],
        { duration: motionMs(panel, "--atlas-motion-surface"), fill: "forwards" });
      await shrink?.finished.catch(() => {});
      await apply();
      shrink?.cancel();
    }
  } finally {
    void panel.offsetWidth; // the new layout's styles are computed before the transition comes back
    panel.style.removeProperty("transition");
  }
}
