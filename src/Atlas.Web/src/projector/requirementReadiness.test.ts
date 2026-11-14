// @vitest-environment node
import { describe, expect, it } from "vitest";
import { keyed, type PageRequirement } from "./requirementModel";
import { blockedPath, blockReadiness, readinessTarget, requirementReadiness, sectionReadiness } from "./requirementReadiness";
import { requirementDefaults, type RequirementAttributes } from "./requirementTypes";

const item = (id: string, attrs: Partial<RequirementAttributes> = {}): PageRequirement => ({ id, title: id, attrs: { ...requirementDefaults, key: id, ...attrs }, ancestors: [] });

it("says whether a section can be deployed: done, blocked, waiting for its unfinished prerequisites (each once), or ready", () => {
  const vpn = item("vpn", { direction: "inbound" }), ssl = item("ssl", { state: "done" }), dns = item("dns", { state: "waiting_for_input" });
  const core = item("core", { requires: "vpn,ssl,dns,gone" }), nia = item("nia", { requires: "vpn" }), na = item("na", { state: "not_applicable", requires: "vpn" });
  const byKey = keyed([vpn, ssl, dns, core, nia, na]);
  const read = (items: PageRequirement[]) => { const { state, label, prerequisites } = sectionReadiness(items, byKey); return [state, label, prerequisites.map(entry => entry.id)]; };
  expect(read([core, nia])).toEqual(["waiting", "čeká na 2 prerekvizity", ["vpn", "dns"]]);
  expect(read([nia])).toEqual(["waiting", "čeká na 1 prerekvizitu", ["vpn"]]);
  expect(read([vpn, item("x")])).toEqual(["ready", "lze nasadit", []]);
  expect(read([ssl, na])).toEqual(["done", "hotovo", []]);
  expect(read([core, item("b", { state: "blocked" })])).toEqual(["blocked", "blokováno", ["vpn", "dns"]]);
  expect(read([])).toEqual(["done", "hotovo", []]);
});

describe("readiness by direction (V1)", () => {
  it("an input waits to be delivered, a handover can be handed over, our own work deployed", () => {
    const byKey = keyed([]);
    const label = (attrs: Partial<RequirementAttributes>) => requirementReadiness(item("x", attrs), byKey).label;
    expect(label({ direction: "inbound" })).toBe("čeká na dodání");
    expect(label({ direction: "outbound" })).toBe("lze předat");
    expect(label({ direction: "internal", state: "in_progress" })).toBe("lze nasadit");
    expect(label({ direction: "inbound", state: "done" })).toBe("hotovo");
    expect(label({ direction: "inbound", state: "not_applicable" })).toBe("netýká se");
    expect(label({ direction: "inbound", state: "blocked" })).toBe("blokováno");
    // Waiting for someone's input is not ready to be deployed, whatever its prerequisites.
    expect(requirementReadiness(item("x", { state: "waiting_for_input" }), byKey)).toMatchObject({ state: "waiting", label: "čeká na vstup" });
    expect(sectionReadiness([item("x", { state: "waiting_for_input" }), item("y")], byKey).label).toBe("lze nasadit");
  });

  it("a section of inputs only waits to be delivered; with our work in it, it can be deployed", () => {
    const vpn = item("vpn", { direction: "inbound" }), out = item("out", { direction: "outbound" }), done = item("done", { state: "done" });
    const byKey = keyed([vpn, out, done]);
    expect(sectionReadiness([vpn, done], byKey).label).toBe("čeká na dodání");
    expect(sectionReadiness([out, done], byKey).label).toBe("lze předat");
    expect(sectionReadiness([vpn, out, item("m")], byKey).label).toBe("lze nasadit");
  });
});

describe("blocked is one rule for the whole path (U4-8)", () => {
  // portal.core waits for the blocked SSL; NIA needs portal.core: NIA is blocked too, through it. MPI does not reach it.
  const ssl = item("ssl", { direction: "inbound", state: "blocked" }), vpn = item("vpn", { direction: "inbound" });
  const core = item("core", { state: "in_progress", requires: "vpn,ssl" }), nia = item("nia", { requires: "core" }), mpi = item("mpi", { requires: "vpn" });
  const byKey = keyed([ssl, vpn, core, nia, mpi]);

  it("names the path to the blocked block, the block itself first; nothing for a finished block or one off the path", () => {
    expect(blockedPath(nia, byKey).map(entry => entry.id)).toEqual(["nia", "core", "ssl"]);
    expect(blockedPath(ssl, byKey).map(entry => entry.id)).toEqual(["ssl"]);
    expect(blockedPath(mpi, byKey)).toEqual([]);
    expect(blockedPath(item("d", { state: "done", requires: "ssl" }), byKey)).toEqual([]);
  });

  it("a module whose prerequisite is blocked further down is blocked, and its readiness leads to the cause", () => {
    const readiness = requirementReadiness(nia, byKey);
    expect([readiness.state, readiness.label, readiness.blocking?.id]).toEqual(["blocked", "blokováno", "ssl"]);
    expect(readinessTarget([nia], readiness).id).toBe("ssl");
    expect(sectionReadiness([mpi, nia], byKey).state).toBe("blocked");
    expect(requirementReadiness(mpi, byKey).state).toBe("waiting");
  });

  it("says a block's readiness in tones its state chip does not contradict (3.5.1 V6)", () => {
    expect(blockReadiness(requirementReadiness(nia, byKey))).toEqual({ label: "čeká na blokované: ssl", tone: "waiting_for_input" });
    expect(blockReadiness(requirementReadiness(vpn, byKey))).toEqual({ label: "čeká na dodání" });
    expect(blockReadiness(requirementReadiness(item("x"), byKey))).toEqual({ label: "lze nasadit", tone: "done" });
  });

  it("ends on a cycle the page should not have", () => {
    const a = item("a", { requires: "b" }), b = item("b", { requires: "a" });
    expect(blockedPath(a, keyed([a, b]))).toEqual([]);
  });

  it("goes to what a section's readiness counts: the first prerequisite missing, or the first block not finished", () => {
    expect(readinessTarget([mpi], requirementReadiness(mpi, byKey)).id).toBe("vpn");
    const ready = item("r"), done = item("f", { state: "done" });
    expect(readinessTarget([done, ready], sectionReadiness([done, ready], byKey)).id).toBe("r");
  });
});
