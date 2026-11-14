// @vitest-environment node
import { describe, expect, it } from "vitest";
import { gridLevels } from "./boardGrid";

const visible = (zoom: number, x = 0, y = 0) => gridLevels({ x, y, zoom }).filter(level => level.opacity > 0.001);

describe("adaptive dot grid (interaction spec §2)", () => {
  it("draws the 24-unit grid at 100 %, with the four times coarser one over it", () => {
    expect(gridLevels({ x: 0, y: 0, zoom: 1 })).toEqual([
      { spacing: 24, size: 24, x: 0, y: 0, opacity: 1 },
      { spacing: 96, size: 96, x: 0, y: 0, opacity: 1 },
    ]);
  });

  it("chooses the level so that the dots on screen are 12 to 48 px apart, from 5 to 400 %", () => {
    expect(gridLevels({ x: 0, y: 0, zoom: 0.05 })[0]).toMatchObject({ spacing: 384, size: expect.closeTo(19.2) });
    expect(gridLevels({ x: 0, y: 0, zoom: 0.49 })[0]).toMatchObject({ spacing: 96 });
    expect(gridLevels({ x: 0, y: 0, zoom: 0.5 })[0]).toMatchObject({ spacing: 24, size: 12 });
    expect(gridLevels({ x: 0, y: 0, zoom: 1.99 })[0]).toMatchObject({ spacing: 24 });
    expect(gridLevels({ x: 0, y: 0, zoom: 2 })[0]).toMatchObject({ spacing: 6, size: 12 });
    expect(gridLevels({ x: 0, y: 0, zoom: 4 })[0]).toMatchObject({ spacing: 6, size: 24 });
    for (let zoom = 0.05; zoom <= 4; zoom *= 1.013) {
      const [fine] = gridLevels({ x: 0, y: 0, zoom });
      expect(fine!.size).toBeGreaterThanOrEqual(12 - 1e-9);
      expect(fine!.size).toBeLessThan(48);
    }
  });

  it("never lets visible dots come closer than 12 px: the finer level fades out on the way there", () => {
    expect(gridLevels({ x: 0, y: 0, zoom: 0.5 })[0]!.opacity).toBe(0);
    expect(gridLevels({ x: 0, y: 0, zoom: 0.75 })[0]!.opacity).toBeCloseTo(0.5);
    for (let zoom = 0.05; zoom <= 4; zoom *= 1.013) for (const level of visible(zoom)) expect(level.size).toBeGreaterThanOrEqual(12);
  });

  it("cross-fades across a level change, so the dots neither jump nor thicken", () => {
    for (const boundary of [0.5, 2, 0.125]) {
      const below = visible(boundary * 0.999).map(level => [level.spacing, Math.round(level.opacity * 100)]);
      const above = visible(boundary * 1.001).map(level => [level.spacing, Math.round(level.opacity * 100)]);
      // Just above the boundary the new fine level is still invisible: what shows is the grid that showed just below (whose
      // coarse level lies on every fourth dot of it, drawn in the same opaque colour).
      expect(above).toHaveLength(1);
      expect(below).toContainEqual(above[0]);
    }
  });

  it("moves the pattern with the view, also to the left of and above the origin", () => {
    expect(gridLevels({ x: 10, y: -10, zoom: 1 })[0]).toMatchObject({ x: 10, y: 14 });
    expect(gridLevels({ x: 1000, y: 0, zoom: 1 })[1]).toMatchObject({ x: 40 });
  });
});
