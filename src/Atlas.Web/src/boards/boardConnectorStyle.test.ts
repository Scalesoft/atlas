// @vitest-environment node
import { describe, expect, it } from "vitest";
import { directionOf, directionPatch, loadConnectorStyle, rememberConnectorStyle, saveConnectorStyle } from "./boardConnectorStyle";

const storage = () => {
  const values = new Map<string, string>();
  return { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => void values.set(key, value), values };
};

describe("connector quick styles", () => {
  it("reads the four directions from the arrows and writes them back", () => {
    expect(directionOf({ arrowStart: "none", arrowEnd: "none" })).toBe("none");
    expect(directionOf({ arrowStart: "none", arrowEnd: "arrow" })).toBe("forward");
    expect(directionOf({ arrowStart: "arrow", arrowEnd: "none" })).toBe("backward");
    expect(directionOf({ arrowStart: "arrow", arrowEnd: "arrow" })).toBe("both");
    expect(directionOf({})).toBe("forward");
    expect(directionPatch("both")).toEqual({ arrowStart: "arrow", arrowEnd: "arrow" });
    expect(directionPatch("backward")).toEqual({ arrowStart: "arrow", arrowEnd: "none" });
  });

  it("remembers the style keys and the colour of a restyle, null forgetting one, and ignores everything else", () => {
    let style = rememberConnectorStyle({}, { props: { dash: "dashed", animated: true, state: "done", fromSide: "left" }, color: "red" });
    expect(style).toEqual({ dash: "dashed", animated: true, color: "red" });
    style = rememberConnectorStyle(style, { props: { animated: null, thickness: "l" }, color: null });
    expect(style).toEqual({ dash: "dashed", thickness: "l" });
  });

  it("keeps the style per browser and reads back only known values", () => {
    const local = storage();
    saveConnectorStyle({ route: "curved", arrowStart: "arrow", animated: true, color: "teal" }, local);
    expect(loadConnectorStyle(local)).toEqual({ route: "curved", arrowStart: "arrow", animated: true, color: "teal" });
    local.setItem("atlas.board.connectorStyle", JSON.stringify({ route: "zigzag", dash: "dashed", color: "brown", thickness: 3, extra: 1 }));
    expect(loadConnectorStyle(local)).toEqual({ dash: "dashed" });
    local.setItem("atlas.board.connectorStyle", "{not json");
    expect(loadConnectorStyle(local)).toEqual({});
    const broken = { getItem: () => { throw new Error("blocked"); }, setItem: () => { throw new Error("blocked"); } };
    expect(loadConnectorStyle(broken)).toEqual({});
    expect(() => saveConnectorStyle({ dash: "dashed" }, broken)).not.toThrow();
  });
});
