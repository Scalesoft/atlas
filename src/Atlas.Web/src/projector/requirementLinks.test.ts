// @vitest-environment node
import { describe, expect, it } from "vitest";
import { changedText } from "./requirementLinks";


describe("who changed a block", () => {
  it("says who and how long ago", () => {
    const now = Date.parse("2026-10-30T12:00:00Z");
    expect(changedText({ kind: "save", changedById: "u", changedBy: "Jana Nováková", changedAt: "2026-10-30T10:00:00Z", title: "SSL", state: "done" }, now))
      .toBe("Upravil Jana Nováková, před 2 hodinami");
  });
});
