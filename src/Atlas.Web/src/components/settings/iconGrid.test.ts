// @vitest-environment node
import { describe, expect, it } from "vitest";
import { moveIconFocus, type IconGridKey } from "./iconGrid";

// Two groups of 6 and 3 options in 4 columns:
//   group 0:  0 1 2 3      group 1:  0 1 2
//             4 5
const move = (from: string, key: IconGridKey) => {
  const [section, index] = from.split(":").map(Number) as [number, number];
  const to = moveIconFocus([6, 3], { section, index }, key, 4);
  return `${to.section}:${to.index}`;
};

describe("moveIconFocus", () => {
  it("moves inside a group; a shorter last row gives its last option", () => {
    expect(move("0:1", "ArrowDown")).toBe("0:5");
    expect(move("0:3", "ArrowDown")).toBe("0:5");
    expect(move("0:5", "ArrowUp")).toBe("0:1");
    expect(move("0:2", "ArrowRight")).toBe("0:3");
    expect(move("0:4", "ArrowLeft")).toBe("0:3");
  });

  it("crosses a group boundary in reading order and keeps the column between rows", () => {
    expect(move("0:5", "ArrowDown")).toBe("1:1");
    expect(move("1:2", "ArrowUp")).toBe("0:2");
    expect(move("1:0", "ArrowUp")).toBe("0:4");
    expect(move("0:5", "ArrowRight")).toBe("1:0");
    expect(move("1:0", "ArrowLeft")).toBe("0:5");
  });

  it("stays at the edges without wrapping; Home and End reach the first and the last option", () => {
    expect(move("1:1", "ArrowDown")).toBe("1:1");
    expect(move("0:1", "ArrowUp")).toBe("0:1");
    expect(move("1:2", "ArrowRight")).toBe("1:2");
    expect(move("0:0", "ArrowLeft")).toBe("0:0");
    expect(move("1:1", "Home")).toBe("0:0");
    expect(move("0:1", "End")).toBe("1:2");
  });
});
