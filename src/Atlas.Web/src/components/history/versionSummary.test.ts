// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { PageRevisionSummary, RevisionChanges } from "../../history";
import { activityBuckets, activityPeople, describeChanges, shares, sizeBar } from "./versionSummary";

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
  it("splits a version's lines by the contributors' saves so they add up, the author alone when nobody is listed", () => {
    const people = [{ id: "a", name: "A", checkpoints: 1 }, { id: "b", name: "B", checkpoints: 1 }, { id: "c", name: "C", checkpoints: 1 }];
    const items = [
      { activeFrom: "2026-11-02T08:10:00", authorId: "a", authorName: "A", changes: changes({ added: 10, contributors: people }) },
      { activeFrom: "2026-11-02T08:40:00", authorId: "c", authorName: "C", changes: changes({ added: 4 }) },
    ] as PageRevisionSummary[];
    const [bucket] = activityBuckets(items);
    expect(bucket!.byPerson).toEqual({ a: 3, b: 4, c: 7 });
    expect(Object.values(bucket!.byPerson).reduce((s, n) => s + n, 0)).toBe(bucket!.people);
    expect(activityPeople(items, [bucket!], 2)).toEqual({ shown: [{ id: "c", name: "C" }, { id: "b", name: "B" }], others: true });
  });
  it("leaves versions without a valid time out of the buckets", () => {
    const items = [{ activeFrom: "nope", authorId: "a", authorName: "A", changes: changes({ added: 1 }) }, { activeFrom: "2026-11-02T08:10:00", authorId: "a", authorName: "A", changes: changes({ added: 2 }) }] as PageRevisionSummary[];
    expect(activityBuckets(items).map(b => b.key)).toEqual(["2026-11-02 08"]);
  });
});
