import { h, render } from "vue";
import AiGlyph, { type AiGlyphState } from "./AiGlyph.vue";

/** Mount the same glyph in a DOM-owned editor action; the editor owns its lifetime. */
export function mountAiGlyph(element: HTMLElement, props: { working?: boolean; size?: number; state?: AiGlyphState } = {}): () => void {
  render(h(AiGlyph, props), element);
  return () => render(null, element);
}
