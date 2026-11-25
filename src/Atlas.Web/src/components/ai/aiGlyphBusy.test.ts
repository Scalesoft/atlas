import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { iconMarkup } from "@atlas/markdown/icons";
import { AI_GLYPH_MOVES, cycleBusy } from "./aiGlyphBusy";

function glyph() {
  const element = document.createElement("span");
  element.innerHTML = `<svg class="ai-glyph-mark">${iconMarkup.sparkles}</svg>`;
  const loop = (child: Element) => child.dispatchEvent(new Event("animationiteration", { bubbles: true }));
  return { element, star: element.querySelector(".ai-glyph-mark > :first-child")!, loop };
}

describe("cycleBusy", () => {
  it("tosses in another variant at the end of each loop of the big star, never the same twice, and cleans up", () => {
    const { element, star, loop } = glyph();
    const rolls = [0, 0, 0.5, 0.5, 0.99, 0];
    const stop = cycleBusy(element, () => rolls.shift()!);
    expect(element.dataset.move).toBeUndefined();
    loop(star); // 0 is the current state's own loop, rolled again
    expect(element.dataset.move).toBe("12");
    loop(element.querySelector("circle")!); // another star's loop or a pseudo-element does not count
    loop(element);
    expect(element.dataset.move).toBe("12");
    loop(star);
    expect(element.dataset.move).toBe("24");
    loop(star);
    expect(element.dataset.move).toBeUndefined();
    element.dataset.move = "3";
    stop();
    expect(element.dataset.move).toBeUndefined();
    loop(star);
    expect(element.dataset.move).toBeUndefined();
  });

  it("names exactly the variants the stylesheet defines", () => {
    const css = readFileSync(resolve(process.cwd(), "src/styles/aiBusy.css"), "utf8");
    const defined = [...css.matchAll(/\[data-move="(\d+)"\]/g)].map((match) => Number(match[1]));
    expect(defined).toEqual(AI_GLYPH_MOVES.map((_, index) => index + 1));
  });
});
