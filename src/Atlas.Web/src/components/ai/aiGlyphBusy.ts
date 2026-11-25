import css from "../../styles/aiBusy.css?inline";

// Injected here, not imported as a stylesheet: the eager glyph's import() then carries no list of preloads (first-load budget).
document.head.append(Object.assign(document.createElement("style"), { textContent: css }));

/** The busy variants of styles/aiBusy.css, `data-move` 1…24 (their names are the gallery's, AiGlyphExample.vue). */
export const AI_GLYPH_MOVE_COUNT = 24;

/**
 * While the glyph works, every time its big star finishes a loop the glyph tosses in another variant at random, never
 * the same twice in a row; 0 is the working state's own loop (styles/ai.css), which also plays first. Loops end at rest,
 * so switching there never jumps; no timer, so a hidden tab or reduced motion (no animation, no loop end) costs nothing.
 */
export function cycleBusy(element: HTMLElement, random: () => number = Math.random): () => void {
  let current = 0;
  const onLoop = (event: Event) => {
    const target = event.target as Element;
    if (!target.parentElement?.classList.contains("ai-glyph-mark") || target.previousElementSibling) return;
    let next = current;
    while (next === current) next = Math.floor(random() * (AI_GLYPH_MOVE_COUNT + 1));
    current = next;
    if (next) element.dataset.move = String(next);
    else delete element.dataset.move;
  };
  element.addEventListener("animationiteration", onLoop);
  return () => {
    element.removeEventListener("animationiteration", onLoop);
    delete element.dataset.move;
  };
}
