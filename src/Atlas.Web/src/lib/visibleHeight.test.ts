import { afterEach, expect, it, vi } from "vitest";
import { visibleHeight } from "./visibleHeight";

afterEach(() => { vi.restoreAllMocks(); document.body.replaceChildren(); });

function listed(overflowY: string) {
  document.body.innerHTML = `<div class="list" style="overflow-y: ${overflowY}"><ul><li><div class="tree"></div></li></ul></div>`;
  const list = document.querySelector(".list")!, tree = document.querySelector(".tree")!;
  vi.spyOn(window, "innerHeight", "get").mockReturnValue(900);
  vi.spyOn(list, "getBoundingClientRect").mockReturnValue({ top: 280, bottom: 815 } as DOMRect);
  vi.spyOn(tree, "getBoundingClientRect").mockReturnValue({ top: 500 } as DOMRect);
  return tree;
}

it("is what lies above the bottom of the window", () => {
  const tree = listed("visible");
  expect(visibleHeight(tree, 5000)).toBe(400);
  expect(visibleHeight(tree, 120)).toBe(120);
});

it("ends at the bottom of the list that scrolls the element, so the motion comes to rest in view", () => {
  // The sidebar's list of spaces ends above the account row: to the window's edge the last 85 px moved behind it.
  expect(visibleHeight(listed("auto"), 5000)).toBe(315);
  expect(visibleHeight(listed("scroll"), 5000)).toBe(315);
});

it("is nothing for an element below the view", () => {
  const tree = listed("auto");
  vi.spyOn(tree, "getBoundingClientRect").mockReturnValue({ top: 1200 } as DOMRect);
  expect(visibleHeight(tree, 300)).toBe(0);
});
