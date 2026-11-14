// @vitest-environment node
import { describe, expect, it } from "vitest";
import { formatActivityTime, formatAgo, formatDate, formatDateTime, formatDay, formatShortDate } from "./formatDate";

describe("cs-CZ timestamps", () => {
  it("formats a timestamp with the year and a 24-hour time", () => {
    const text = formatDateTime("2026-09-07T14:33:00Z");
    expect(text).toContain("2026");
    expect(text).toContain(":");
    expect(text).not.toMatch(/\b[AP]M\b/i);
  });
  it("returns an empty string when there is nothing to show", () => {
    expect(formatDateTime(null)).toBe("");
    expect(formatDateTime(undefined)).toBe("");
    expect(formatDateTime("")).toBe("");
    expect(formatDateTime("není datum")).toBe("");
  });
  it("accepts a Date and formats the day alone without a time", () => {
    expect(formatDateTime(new Date("2026-09-07T14:33:00Z"))).toContain("2026");
    expect(formatDate("2026-09-07T14:33:00Z")).toContain("2026");
    expect(formatDate("2026-09-07T14:33:00Z")).not.toContain(":");
    expect(formatDate(null)).toBe("");
  });
  it("formats every caller through the same cs-CZ formatter", () => {
    expect(formatDateTime("2026-09-07T14:33:00Z")).toBe(
      new Intl.DateTimeFormat("cs-CZ", { dateStyle: "medium", timeStyle: "short" }).format(new Date("2026-09-07T14:33:00Z")),
    );
  });
  it("spells a calendar day out", () => {
    expect(formatDay("2026-10-09")).toBe("9. října 2026");
    expect(formatDay("2026-01-01")).toBe("1. ledna 2026");
  });
  it("names the UTC day of an instant near midnight, not the Prague one", () => {
    const instant = "2026-10-09T23:30:00Z";
    const prague = new Intl.DateTimeFormat("cs", { dateStyle: "long", timeZone: "Europe/Prague" }).format(new Date(instant));
    expect(prague).toBe("10. října 2026");
    expect(formatDay(instant)).toBe("9. října 2026");
  });
  it("shortens a date to the day within this year and keeps the year before and after", () => {
    const now = new Date("2026-10-09T12:00:00Z");
    expect(formatShortDate("2026-10-09T09:01:00Z", now)).toBe("9. 10.");
    expect(formatShortDate("2025-09-23T09:01:00Z", now)).toBe("23. 9. 2025");
    expect(formatShortDate("2027-01-02T12:00:00Z", now)).toBe("2. 1. 2027");
    expect(formatShortDate(null, now)).toBe("");
    expect(formatShortDate("nope", now)).toBe("");
  });
  it("says how long ago within a month and the day past it", () => {
    const now = Date.parse("2026-09-28T12:00:00Z");
    expect(formatAgo("2026-09-28T11:55:00Z", now)).toBe("před 5 minutami");
    expect(formatAgo("2026-09-27T11:00:00Z", now)).toBe("včera");
    expect(formatAgo("2026-09-25T12:00:00Z", now)).toBe("před 3 dny");
    expect(formatAgo("2026-07-01T12:00:00Z", now)).toBe(formatDate("2026-07-01T12:00:00Z"));
    expect(formatAgo(null, now)).toBe("");
  });
  it("formats an activity time in the same Prague day used by the calendar", () => {
    const text = formatActivityTime("2026-03-28T23:30:00Z");
    expect(text).toContain("29.");
    expect(text).toContain("0:30");
  });
});
