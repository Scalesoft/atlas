// @vitest-environment node
import { describe, expect, it } from "vitest";
import { dateAt, timelineLevel, timelineTicks, xOfDate } from "./boardTimeline";

const timeline = { origin: "2026-01-01", pxPerDay: 10 };

describe("timeline background (interaction spec §3.7)", () => {
  it("places dates on board x from the origin and the scale, and reads them back", () => {
    expect(xOfDate(timeline, "2026-01-01")).toBe(0);
    expect(xOfDate(timeline, "2026-01-11")).toBe(100);
    expect(xOfDate(timeline, "2025-12-31")).toBe(-10);
    // Across the change to summer time a day is still a day.
    expect(xOfDate(timeline, "2026-04-01")).toBe(900);
    expect(dateAt(timeline, 105)).toBe("2026-01-11");
    expect(dateAt(timeline, -1)).toBe("2025-12-31");
  });

  it("shows the finest period whose division is far enough apart to be read, from days to decades", () => {
    // A division is readable from 48 px on screen.
    expect(timelineLevel({ ...timeline, pxPerDay: 60 }, 1)).toBe("day");
    expect(timelineLevel(timeline, 1)).toBe("week");
    expect(timelineLevel(timeline, 0.5)).toBe("month");
    expect(timelineLevel(timeline, 0.1)).toBe("quarter");
    expect(timelineLevel(timeline, 0.05)).toBe("year");
    expect(timelineLevel({ ...timeline, pxPerDay: 1 }, 0.05)).toBe("decade");
    expect(timelineLevel({ ...timeline, pxPerDay: 0.05 }, 0.05)).toBe("decade");
  });

  it("divides by real calendar periods, from the one the view starts in, with Czech labels and a fade-in of the next finer level", () => {
    const view = { x: 0, width: 1000 };
    const months = timelineTicks(timeline, 0.5, view);
    expect(months.level).toBe("month");
    expect(months.ticks.slice(0, 3).map(tick => [tick.x, tick.label])).toEqual([[0, "leden 2026"], [310, "únor 2026"], [590, "březen 2026"]]);
    expect(months.finer).toBe("week");
    expect(months.finerOpacity).toBeGreaterThan(0);
    expect(months.finerOpacity).toBeLessThan(1);
    const days = timelineTicks({ ...timeline, pxPerDay: 60 }, 1, { x: 0, width: 300 });
    expect(days.level).toBe("day");
    expect(days.ticks.slice(0, 2).map(tick => tick.label)).toEqual(["1. 1.", "2. 1."]);
    expect(timelineTicks({ ...timeline, pxPerDay: 0.05 }, 0.05, { x: 0, width: 4000 }).ticks.slice(0, 2).map(tick => tick.label)).toEqual(["2020–2029", "2030–2039"]);
    expect(timelineTicks(timeline, 0.1, { x: 0, width: 1000 }).ticks[0]).toMatchObject({ x: 0, label: "Q1 2026" });
  });

  it("starts weeks on Monday", () => {
    const { level, ticks } = timelineTicks(timeline, 1, { x: 0, width: 300 });
    expect(level).toBe("week");
    // 2026-01-01 is a Thursday: its week began on Monday 29 December, the next one on 5 January.
    expect(ticks.slice(0, 2).map(tick => [tick.x, tick.label])).toEqual([[-30, "29. 12."], [40, "5. 1."]]);
  });
});
