import { beforeEach, describe, expect, it } from "vitest";
import { readBoardPlace, saveBoardPlace } from "./boardPlace";

beforeEach(() => sessionStorage.clear());

describe("the place a person left a board (BD2-3)", () => {
  it("is read back as it was saved, per board", () => {
    saveBoardPlace("B1", { viewport: { x: -120.5, y: 40, zoom: 0.8 }, selected: ["a", "b"] });
    expect(readBoardPlace("b1")).toEqual({ viewport: { x: -120.5, y: 40, zoom: 0.8 }, selected: ["a", "b"] });
    expect(readBoardPlace("other")).toBeUndefined();
  });

  it("is dropped by saving none", () => {
    saveBoardPlace("b1", { viewport: { x: 0, y: 0, zoom: 1 }, selected: [] });
    saveBoardPlace("b1", null);
    expect(readBoardPlace("b1")).toBeUndefined();
  });

  it("refuses what is not a view: broken JSON, a missing number, a zoom out of range", () => {
    for (const raw of ["{", "null", "{\"viewport\":{\"x\":1,\"y\":2}}", "{\"viewport\":{\"x\":1,\"y\":2,\"zoom\":9}}", "{\"viewport\":{\"x\":\"1\",\"y\":2,\"zoom\":1}}"]) {
      sessionStorage.setItem("atlas.board-place.b1", raw);
      expect(readBoardPlace("b1"), raw).toBeUndefined();
    }
  });

  it("keeps only string ids of the selection", () => {
    sessionStorage.setItem("atlas.board-place.b1", JSON.stringify({ viewport: { x: 1, y: 2, zoom: 1 }, selected: ["a", 5, null, "c"] }));
    expect(readBoardPlace("b1")?.selected).toEqual(["a", "c"]);
  });
});
