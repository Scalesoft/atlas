import { animateAtlas, flyGhost, motionMs, prefersReducedMotion } from "../../motion/aiMotion";

/**
 * Moment 4 of the prototype "Založení a publikace": an applied suggestion's value flies from its chip into the field it
 * filled (flyGhost, 520 ms) and the field settles. Under reduced motion, or without a chip to fly from, the value only
 * fades in where it now is. The field already holds the value; this is decoration and never waits for anything.
 */
export function flyValue(from: Element | null, to: Element, text: string) {
  if (!from || prefersReducedMotion(to)) return animateAtlas(to, [{ opacity: 0 }, { opacity: 1 }], { duration: motionMs(to, "--atlas-motion-surface") });
  const ghost = document.createElement("span");
  ghost.textContent = text;
  Object.assign(ghost.style, { display: "inline-block", padding: "2px 8px", borderRadius: "8px", border: "1px solid var(--line)", background: "var(--surface)", color: "var(--text)", fontSize: "12.5px", fontWeight: "600", boxShadow: "var(--shadow-menu)" });
  return flyGhost(from, to, ghost.outerHTML);
}
