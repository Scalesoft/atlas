import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { enterCells, ghostCells, glideColumns, readColumns, releaseGhosts, type GlideColumn } from "./glideColumns";

const columns: GlideColumn[] = [
  { probe: ".space-page-row .space-page-main", cells: ".space-page-main, .space-head-main" },
  { probe: ".space-page-row .space-page-path", cells: ".space-page-path, .space-head-path" },
];
let table: HTMLElement;
const lefts = new Map<Element, number>();
const animated: { element: Element; frames: Keyframe[]; options: KeyframeAnimationOptions }[] = [];

beforeEach(() => {
  table = document.createElement("div");
  table.innerHTML = `
    <div class="space-page-head"><span class="space-head-main"></span><span class="space-head-path"></span></div>
    <div class="space-page-row"><span class="space-page-main"></span><span class="space-page-path"></span></div>
    <div class="space-page-row"><span class="space-page-main"></span><span class="space-page-path"></span></div>`;
  document.body.append(table);
  lefts.clear();
  animated.length = 0;
  // jsdom has no layout and no Web Animations: the columns' positions are given, the animations recorded.
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) { return { left: lefts.get(this) ?? 0 } as DOMRect; });
  Element.prototype.animate = function (this: Element, frames: Keyframe[] | PropertyIndexedKeyframes | null, options?: number | KeyframeAnimationOptions) {
    animated.push({ element: this, frames: frames as Keyframe[], options: options as KeyframeAnimationOptions });
    return { finished: Promise.resolve(), addEventListener() {}, cancel() {} } as unknown as Animation;
  };
});
afterEach(() => { table.remove(); vi.restoreAllMocks(); delete (Element.prototype as { animate?: unknown }).animate; });

const place = (main: number, path: number) => {
  lefts.set(table.querySelector(".space-page-row .space-page-main")!, main);
  lefts.set(table.querySelector(".space-page-row .space-page-path")!, path);
};

it("reads where the columns of the first row stand", () => {
  place(350, 839.6);
  expect(readColumns(table, columns)).toEqual([350, 839.6]);
});

it("glides every cell of a column from where it stood to where it stands now", () => {
  place(350, 839.6);
  const before = readColumns(table, columns);
  place(386, 855.3);
  const glides = glideColumns(table, columns, before, { duration: 200, easing: "ease-out" });
  // Head and rows: two cells per column here, three rows of cells in all (the head's and the two rows').
  expect(glides).toHaveLength(6);
  const main = animated.filter(({ element }) => element.matches(".space-page-main, .space-head-main"));
  expect(main).toHaveLength(3);
  expect(main[0]!.frames).toEqual([{ translate: "-36px 0" }, { translate: "0 0" }]);
  const path = animated.filter(({ element }) => element.matches(".space-page-path, .space-head-path"));
  expect(path[0]!.frames[0]!.translate).toBe("-15.7px 0");
  expect(main[0]!.options.duration).toBe(200);
});

it("glides only the cells that can be seen", () => {
  place(350, 839.6);
  const before = readColumns(table, columns);
  place(386, 855.3);
  const rows = table.querySelectorAll(".space-page-row");
  // The second row is far below the window.
  Object.defineProperty(rows[1]!.querySelector(".space-page-main")!, "getBoundingClientRect", { value: () => ({ left: 386, top: 5000, bottom: 5044 }) });
  glideColumns(table, columns, before, { duration: 200, easing: "ease-out" });
  expect(animated.filter(({ element }) => element.matches(".space-page-main, .space-head-main"))).toHaveLength(2);
});

it("lets a removed column's cells go as pinned copies and removes the copies when they are gone", () => {
  table.querySelector(".space-page-row")!.insertAdjacentHTML("afterbegin", `<span class="page-selection" role="cell"><input type="checkbox"></span>`);
  const cell = table.querySelector<HTMLElement>(".page-selection")!;
  table.querySelector<HTMLInputElement>(".page-selection input")!.checked = true;
  lefts.set(cell, 316);
  lefts.set(cell.parentElement!, 300);
  const ghosts = ghostCells(table, ".page-selection");
  expect(ghosts).toHaveLength(1);
  const copy = ghosts[0]!;
  expect(copy.getAttribute("aria-hidden")).toBe("true");
  expect(copy.hasAttribute("role")).toBe(false);
  expect(copy.style.left).toBe("16px");
  expect(copy.querySelector("input")!.checked).toBe(true);
  expect(copy.parentElement).toBe(cell.parentElement);
  releaseGhosts(ghosts, -36, { duration: 200, easing: "ease-out" });
  expect(animated.at(-1)!.frames).toEqual([{ translate: "0 0", opacity: 1 }, { translate: "-36px 0", opacity: 0 }]);
});

it("brings the cells of a new column in from the edge, fading", () => {
  table.querySelectorAll(".space-page-row").forEach(row => row.insertAdjacentHTML("afterbegin", `<span class="page-selection"></span>`));
  expect(enterCells(table, ".page-selection", -36, { duration: 200, easing: "ease-out" })).toHaveLength(2);
  expect(animated.at(-1)!.frames).toEqual([{ translate: "-36px 0", opacity: 0 }, { translate: "0 0", opacity: 1 }]);
});

it("leaves a column alone that did not move, and reads nothing from a table without rows", () => {
  place(350, 839.6);
  const before = readColumns(table, columns);
  place(386, 839.6);
  glideColumns(table, columns, before, { duration: 200, easing: "ease-out" });
  expect(animated.every(({ element }) => element.matches(".space-page-main, .space-head-main"))).toBe(true);
  expect(readColumns(document.createElement("div"), columns)).toEqual([Number.NaN, Number.NaN]);
  expect(glideColumns(document.createElement("div"), columns, [Number.NaN, Number.NaN], { duration: 200, easing: "ease-out" })).toEqual([]);
});
