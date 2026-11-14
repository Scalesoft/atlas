import { animateAtlas, motionMs } from "../motion/aiMotion";

/**
 * A page arrives (refresh 2.5, board 16 „Načtení stránky a sekcí“): the header and the text rise by 8 px (--atlas-rise) and fade
 * in over --atlas-motion-slow, together. Reduced motion keeps a short fade (animateAtlas). Nothing is kept after the end,
 * so no transform stays on the document (fixed editor overlays inside it keep the viewport as their box).
 *
 * The fade is done after the first three fifths of the eased way, the rise takes all of it: the text is in the page
 * about 50 ms after a click, and with a fade as long as the rise it stayed under 90 % opacity for another 110 ms
 * (measured), twice the time the page itself took. Now it can be read after about 40 ms and still settles as before.
 *
 * It starts readable (.65, as motion.css `atlas-reveal`), never from opacity 0: text painted invisible is not shown to the reader,
 * and Chrome counts it as painted only at a later repaint of its own, which put the page's LCP (its title) about 300 ms after
 * the content stood (measured on production pages: 560-760 ms from opacity 0, 230-470 ms from .65).
 */
const FADE_DONE = 0.6;
export function articleArrives(elements: Iterable<Element>) {
  return [...elements].flatMap((element) => animateAtlas(element, [
    { opacity: 0.65, transform: "translateY(8px)" },
    { opacity: 1, offset: FADE_DONE },
    { opacity: 1, transform: "none" },
  ], { duration: motionMs(element, "--atlas-motion-slow") }) ?? []);
}
