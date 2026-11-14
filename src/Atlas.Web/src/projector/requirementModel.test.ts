// @vitest-environment node
import { describe, expect, it } from "vitest";
import {
  awaitedBy, dependencyCycle, keyed, missingPrerequisites, nestedProgress, resetRequirementJson, parseDate, slugKey, summarize, toggleState, uniqueKey, withState, type PageRequirement,
} from "./requirementModel";
import { requirementDefaults, type RequirementAttributes } from "./requirementTypes";

let next = 0;
const item = (title: string, attrs: Partial<RequirementAttributes> = {}, ancestors: string[] = []): PageRequirement =>
  ({ id: `id-${++next}`, title, attrs: { ...requirementDefaults, ...attrs }, ancestors });

describe("requirement model", () => {
  it("makes a key from a title as a slug, unique among the page's keys", () => {
    expect(slugKey("SSL certifikát – Ověření č. 1.2")).toBe("ssl-certifikat-overeni-c.-1.2");
    expect(slugKey("  😀  ")).toBe("");
    expect(slugKey("x".repeat(120))).toHaveLength(96);
    // Two blocks with the same title get distinct keys.
    expect(uniqueKey("SMS brána", new Set())).toBe("sms-brana");
    expect(uniqueKey("SMS brána", new Set(["sms-brana"]))).toBe("sms-brana-2");
    expect(uniqueKey("SMS brána", new Set(["sms-brana", "sms-brana-2"]))).toBe("sms-brana-3");
  });

  it("waits only for dependencies on the page that are not satisfied; done and not applicable both satisfy", () => {
    const ssl = item("SSL", { key: "ssl", state: "done" });
    const sms = item("SMS", { key: "sms", state: "not_applicable" });
    const vpn = item("VPN", { key: "vpn", state: "in_progress" });
    const portal = item("Portál", { key: "portal", requires: "ssl,sms,vpn,gone" });
    expect(awaitedBy(portal, keyed([ssl, sms, vpn, portal])).map(entry => entry.title)).toEqual(["VPN"]);
    // A dependency removed from the page leaves no ghost "čeká na".
    expect(awaitedBy(portal, keyed([ssl, sms, portal]))).toEqual([]);
    // A block that is itself done waits for nothing.
    expect(awaitedBy({ ...portal, attrs: { ...portal.attrs, state: "done" } }, keyed([vpn]))).toEqual([]);
  });

  it("counts nested progress without completing the parent", () => {
    const parent = item("Portál");
    const children = [item("a", { state: "done" }, [parent.id]), item("b", { state: "done" }, [parent.id]), item("c", { state: "done" }, [parent.id, "x"]),
      item("d", {}, [parent.id]), item("e", { state: "not_applicable" }, [parent.id]), item("other", { state: "done" })];
    const progress = nestedProgress([parent, ...children]);
    expect(progress.get(parent.id)).toEqual({ done: 3, total: 4 });
    expect(parent.attrs.state).toBe("planned");
    expect(progress.get(children[5]!.id)).toEqual({ done: 0, total: 0 });
  });

  it("summarizes the page: the percent of the projection, counts by state, missing inputs and who is waited on", () => {
    expect(summarize([])).toMatchObject({ percent: 0, done: 0, total: 0, missing: [], waiting: [] });
    const items = [
      item("SSL", { state: "done", direction: "inbound" }), item("SMS", { state: "waiting_for_input", direction: "inbound", party: "IT KBN" }),
      item("Testy", { state: "in_progress" }), item("Archiv", { state: "not_applicable", direction: "inbound" }),
    ];
    const summary = summarize(items);
    expect(summary).toMatchObject({ percent: 33, done: 1, total: 3 });
    expect(summary.counts).toEqual({ planned: 0, waiting_for_input: 1, in_progress: 1, done: 1, blocked: 0, not_applicable: 1 });
    expect(summary.missing.map(entry => entry.title)).toEqual(["SMS"]);
    expect(summary.waiting.map(entry => entry.title)).toEqual(["SMS"]);
    expect(summarize(items.map(entry => ({ ...entry, attrs: { ...entry.attrs, state: "done" as const } }))).percent).toBe(100);
  });

  it("finds the cycle a new dependency would close, itself included", () => {
    const items = [item("A", { key: "a", requires: "b" }), item("B", { key: "b", requires: "c" }), item("C", { key: "c" })];
    expect(dependencyCycle(items, "c", "a")).toEqual(["a", "b", "c"]);
    expect(dependencyCycle(items, "a", "a")).toEqual(["a"]);
    expect(dependencyCycle(items, "a", "c")).toBeNull();
  });

  it("toggles done: fills today when there is no date, and the second toggle restores the previous state and date", () => {
    const planned = { ...requirementDefaults, state: "in_progress" as const, date: "" };
    const done = toggleState(planned, "done", "2026-10-27");
    expect(done.next).toEqual({ state: "done", date: "2026-10-27" });
    expect(toggleState({ ...planned, ...done.next }, "done", "2026-10-28", done.previous).next).toEqual({ state: "in_progress", date: "" });
    // With a date of its own, done keeps it; without a remembered state, undone is planned and the completion date goes.
    expect(toggleState({ ...planned, date: "2026-10-01" }, "done", "2026-10-27").next).toEqual({ state: "done", date: "2026-10-01" });
    expect(toggleState({ ...planned, state: "done", date: "2026-10-01" }, "done", "2026-10-27").next).toEqual({ state: "planned", date: "" });
    expect(withState(planned, "done", "2026-10-27")).toEqual({ state: "done", date: "2026-10-27" });
    expect(withState({ ...planned, date: "2026-10-02" }, "blocked", "2026-10-27")).toEqual({ state: "blocked", date: "2026-10-02" });
  });

  it("toggles „netýká se“ the same way: back to what it was, keeping the date", () => {
    const waiting = { ...requirementDefaults, state: "waiting_for_input" as const, date: "2026-10-01" };
    const dismissed = toggleState(waiting, "not_applicable", "2026-10-27");
    expect(dismissed.next).toEqual({ state: "not_applicable", date: "2026-10-01" });
    expect(toggleState({ ...waiting, ...dismissed.next }, "not_applicable", "2026-10-28", dismissed.previous).next).toEqual({ state: "waiting_for_input", date: "2026-10-01" });
    expect(toggleState({ ...waiting, state: "not_applicable" }, "not_applicable", "2026-10-28").next).toEqual({ state: "planned", date: "2026-10-01" });
  });

  it("reads a date the Czech way or as ISO, and refuses one that does not exist", () => {
    expect(parseDate("10. 9. 2026")).toBe("2026-09-10");
    expect(parseDate("1.2.2027")).toBe("2027-02-01");
    expect(parseDate(" 2026-09-10 ")).toBe("2026-09-10");
    expect(parseDate("")).toBe("");
    expect(parseDate("29. 2. 2028")).toBe("2028-02-29");
    // PR2-7: the way a date is spoken, without the year: this year's (the one of `today`).
    expect(parseDate("12. 11.", "2026-10-27")).toBe("2026-11-12");
    expect(parseDate("12.11", "2026-10-27")).toBe("2026-11-12");
    expect(parseDate("30. 2.", "2026-10-27")).toBeNull();
    expect(parseDate("12. 11.")).toBeNull(); // no year to take: as before
    for (const wrong of ["31. 2. 2026", "1. 13. 2026", "1. 1. 20266", "2026-13-45", "zítra"]) expect(parseDate(wrong)).toBeNull();
  });

  it("resets a catalog block for a new page: state, date, party and place go, key, direction, dependencies and content stay", () => {
    const block = { type: "requirement", attrs: { id: "x", key: "ssl", direction: "inbound", state: "done", date: "2026-09-10", party: "IT", where: "Keepass", requires: "a" },
      content: [{ type: "paragraph", content: [{ type: "text", text: "SSL" }] },
        { type: "requirement", attrs: { id: "y", key: "ssl.ok", state: "blocked", party: "Jana" }, content: [{ type: "paragraph" }] }] };
    const reset = resetRequirementJson(block);
    expect(reset.attrs).toMatchObject({ key: "ssl", direction: "inbound", requires: "a", state: "planned", date: "", party: "", where: "" });
    expect(reset.attrs!.id).not.toBe("x");
    expect(reset.content![1]!.attrs).toMatchObject({ key: "ssl.ok", state: "planned", party: "" });
    expect(reset.content![1]!.attrs!.id).not.toBe("y");
    expect(reset.content![0]).toEqual(block.content[0]);
    expect(block.attrs.state).toBe("done");
  });

  it("leaves a copied block's history behind: its dated list lines go, a list left empty too, other lines stay", () => {
    const line = (text: string) => ({ type: "listItem", content: [{ type: "paragraph", content: [{ type: "text", text }] }] });
    const block = { type: "requirement", attrs: { id: "x" }, content: [{ type: "paragraph", content: [{ type: "text", text: "SSL" }] },
      { type: "bulletList", content: [line("2. 11. 2026 09:30 — Urgováno: IT"), line("doplnit adresu")] },
      { type: "bulletList", content: [line("1. 9. 2026 — Vyzváno")] }] };
    expect(resetRequirementJson(block).content).toEqual([block.content[0], { type: "bulletList", content: [line("doplnit adresu")] }]);
  });
});

describe("missingPrerequisites", () => {
  it("names the keys unfinished blocks require and the page lacks, with who needs them; other pages' blocks, own key and finished blocks aside", () => {
    const a = item("A", { key: "a", requires: "b,server,other@p1,a" }), b = item("B", { key: "b", requires: "server" }), c = item("C", { key: "c", state: "done", requires: "certs" });
    expect(missingPrerequisites([a, b, c])).toEqual([{ key: "server", neededBy: [a, b] }]);
  });
});