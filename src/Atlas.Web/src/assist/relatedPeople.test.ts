// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { nextTick } from "vue";
import { rankPeople, type PersonHit } from "../contacts/contactSearch";
import { contactUsage } from "../contacts/contactSources";
import type { PageConnections } from "../pageConnections";
import { createRelatedPeople } from "./relatedPeople";

const answer = (people?: string[]): PageConnections => ({ linksHere: { items: [], total: 0 }, ...(people ? { related: { items: [], people } } : {}) });
const hit = (name: string, objectId: string): PersonHit => ({ key: `obj:${objectId}`, contact: { name }, source: "directory", objectId, origin: "Adresář" });

describe("related people (P15 embeddings boost)", () => {
  it("asks nothing and ranks nothing while the related capability is off", () => {
    const get = vi.fn(async () => answer(["A"]));
    const related = createRelatedPeople({ get }, () => false);
    expect(related("page").size).toBe(0);
    expect(get).not.toHaveBeenCalled();
  });

  it("never waits: the first list ranks nothing, the answer arrives later once per page; without related pages nothing changes", async () => {
    let enabled = true;
    const get = vi.fn(async (pageId: string) => answer(pageId === "page" ? ["B-ID"] : undefined));
    const related = createRelatedPeople({ get }, () => enabled);
    expect(related("page").size).toBe(0);
    await nextTick(); await Promise.resolve();
    expect([...related("page")]).toEqual(["b-id"]);
    related("other"); await Promise.resolve();
    expect(related("other").size).toBe(0);
    expect(get).toHaveBeenCalledTimes(2);
    expect(get).toHaveBeenCalledWith("page", 1);
    enabled = false;
    expect(related("page").size).toBe(0);
  });

  it("a failed answer ranks nothing and is not retried on every keystroke", async () => {
    const get = vi.fn(async () => { throw new Error("503"); });
    const related = createRelatedPeople({ get }, () => true);
    related("page"); await Promise.resolve(); await Promise.resolve();
    expect(related("page").size).toBe(0);
    expect(get).toHaveBeenCalledTimes(1);
  });

  it("reorders only equal matches in the @ list: a boosted person never outranks a better match", () => {
    const hits = [hit("Jan Novák", "a"), hit("Jan Novotný", "b"), hit("Petr Janda", "c")];
    expect(rankPeople("jan", hits).map(person => person.objectId)).toEqual(["a", "b", "c"]);
    expect(rankPeople("jan", hits, new Set(["b", "c"])).map(person => person.objectId)).toEqual(["b", "a", "c"]);
  });

  it("tells a name's offer who the related pages name (contacts plugin usage.related)", async () => {
    const usage = { count: async () => ({ total: 2 }), usage: async () => ({ items: [] }) } as never;
    const signal = new AbortController().signal;
    expect(await contactUsage(() => "s", usage, () => new Set(["x"]))("X", { signal })).toEqual({ here: 0, total: 2, related: true });
    expect(await contactUsage(() => "s", usage)("X", { signal })).toEqual({ here: 0, total: 2 });
  });
});
