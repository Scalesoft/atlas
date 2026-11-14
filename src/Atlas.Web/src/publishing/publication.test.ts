// @vitest-environment node
import { describe, expect, it } from "vitest";
import { approveRequest, changeSummaryError, CHANGE_SUMMARY_MAX, normalizeChangeSummary, publishRequest } from "./publication";

describe("change summary", () => {
  it("trims the summary, maps blank to null and rejects multi-line or overlong text with the server wording", () => {
    expect(normalizeChangeSummary("  Doplněn postup pro mobil  ")).toEqual({ value: "Doplněn postup pro mobil" });
    expect(normalizeChangeSummary("   ")).toEqual({ value: null });
    expect(normalizeChangeSummary(undefined)).toEqual({ value: null });
    expect(normalizeChangeSummary("a".repeat(CHANGE_SUMMARY_MAX)).error).toBeUndefined();
    const tooLong = normalizeChangeSummary("a".repeat(CHANGE_SUMMARY_MAX + 1));
    expect(tooLong).toEqual({ value: null, error: "Popis změny smí mít jeden řádek a nejvýše 500 znaků." });
    expect(changeSummaryError()).toBe(tooLong.error);
    // Line breaks and other control characters, as the server's char.IsControl; trimming happens first.
    for (const text of ["první\ndruhý", "první\r\ndruhý", "tab\tuvnitř", "nul\u0000x", "c1\u0085x"])
      expect(normalizeChangeSummary(text).error).toBe(changeSummaryError());
    expect(normalizeChangeSummary("\n konec \n").value).toBe("konec");
  });

  it("builds request bodies that carry the summary only when there is one", () => {
    expect(publishRequest(3, null)).toEqual({ expectedVersion: 3 });
    expect(JSON.stringify(publishRequest(3, null))).toBe('{"expectedVersion":3}');
    expect(publishRequest(3, "Popis")).toEqual({ expectedVersion: 3, changeSummary: "Popis" });
    // Outside editing (UX EP2-8): the server refuses over unsaved live edits the saved revision would leave out.
    expect(publishRequest(3, null, true)).toEqual({ expectedVersion: 3, refuseUnsavedLiveEdits: true });
    expect(JSON.stringify(approveRequest(7, "rev-7", null))).toBe('{"expectedVersion":7,"expectedRevisionId":"rev-7"}');
    expect(approveRequest(7, "rev-7", "Popis")).toEqual({ expectedVersion: 7, expectedRevisionId: "rev-7", changeSummary: "Popis" });
  });
});
