// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { PageRevisionSummary, RevisionChanges } from "../../history";
import { activityBuckets, describeChanges, shares, sizeBar } from "./versionSummary";

const changes = (o: Partial<RevisionChanges> = {}): RevisionChanges => ({ added: 0, removed: 0, blocks: 0, sections: [], states: [], requirementsAdded: 0, requirementsRemoved: 0, contactsAdded: [], contactsRemoved: [], attachmentsAdded: 0, attachmentsRemoved: 0, titleBefore: null, contributors: [], ...o });
describe("version summary", () => {
  it("says what changed in the components in Czech", () => {
    const lines = describeChanges(changes({ titleBefore: "Starý", states: [{ title: "a", from: "planned", to: "done" }, { title: "b", from: "planned", to: "done" }, { title: "c", from: "done", to: "blocked" }], requirementsAdded: 2, contactsAdded: ["A", "B", "C", "D"], attachmentsRemoved: 1 }));
    expect(lines).toEqual(["Název změněn (dříve: Starý)", "2 požadavky → Hotovo", "1 požadavek → Blokováno", "+2 požadavky", "Kontakty: +A, B, C a 1 další", "−1 příloha"]);
  });
  it("scales the size bar by log and the shares by checkpoints", () => {
    expect(sizeBar(null)).toBe(0);
    expect(sizeBar(changes({ added: 5 }))).toBeLessThan(sizeBar(changes({ added: 500 })));
    expect(shares(changes({ contributors: [{ id: "1", name: "A", checkpoints: 3 }, { id: "2", name: "B", checkpoints: 1 }] })).map(c => c.share)).toEqual([75, 25]);
  });
  it("buckets by hour within two days and by day beyond", () => {
    const v = (at: string, ai = false) => ({ activeFrom: at, isAi: ai, changes: changes({ added: 2, removed: 1 }) }) as PageRevisionSummary;
    expect(activityBuckets([v("2026-11-02T08:10:00"), v("2026-11-02T08:50:00", true), v("2026-11-02T10:00:00")]).map(b => [b.key, b.people, b.ai])).toEqual([["2026-11-02 08", 3, 3], ["2026-11-02 10", 3, 0]]);
    expect(activityBuckets([v("2026-11-02T08:10:00"), v("2026-11-09T08:10:00")]).map(b => b.key)).toEqual(["2026-11-02", "2026-11-09"]);
  });
});
