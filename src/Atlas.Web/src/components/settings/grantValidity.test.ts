// @vitest-environment node
import { describe, expect, it } from "vitest";
import { localDateTime, localInput, minimumEnd, validityError } from "./grantValidity";

// The editors check live with the server's rule (Rules.RequireFutureEnd): a new or changed end must lie
// ahead, while the stored bounds of a lapsed grant, left as they are, still let a role change save.
describe("grant validity", () => {
  const now = new Date("2026-09-28T10:00:00Z");
  const lapsed = { startAt: null, expiresAt: "2026-09-01T08:00:00.123456Z" };

  it("refuses an end that has passed unless it is the stored one left untouched", () => {
    expect(validityError("", localInput(new Date("2026-09-27T10:00:00Z")), undefined, now)).toContain("už uplynul");
    expect(validityError("", localDateTime(lapsed.expiresAt), lapsed, now)).toBe("");
    expect(validityError("", localInput(new Date("2026-09-02T10:00:00Z")), lapsed, now)).toContain("už uplynul");
    expect(validityError("", localInput(new Date("2026-10-01T10:00:00Z")), lapsed, now)).toBe("");
  });

  it("offers no end before the next minute or before a later start", () => {
    expect(minimumEnd("", new Date("2026-09-28T10:00:30Z"))).toBe(localInput(new Date("2026-09-28T10:01:00Z")));
    const start = localInput(new Date("2026-10-05T08:00:00Z"));
    expect(minimumEnd(start, now)).toBe(localInput(new Date(new Date(start).getTime() + 60_000)));
  });
});
