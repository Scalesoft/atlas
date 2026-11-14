// @vitest-environment node
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { awaitedBy, keyed, summarize, type PageRequirement } from "./requirementModel";
import { waitingByParty } from "./requirementOverview";
import { unknownParty } from "./requirementSummary";
import { requirementDefaults, type RequirementAttributes } from "./requirementTypes";

// The same cases as the server's RequirementSummaryParityTests: the overview card counts what the server's summary, „Na co čekáme“
// and the blocks' „čeká na“ count.
interface Block { id: string; key: string; direction: RequirementAttributes["direction"]; state: RequirementAttributes["state"]; date: string; party: string; requires: string[]; parent: string | null }
interface Case {
  name: string; blocks: Block[];
  summary: { total: number; applicable: number; done: number; percent: number; waiting: number; inboundMissing: string[]; states: Record<string, number> };
  waitingOn: Record<string, string[]>; parties: [string, string[]][];
}
const cases = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../../../../tests/fixtures/content/requirement-summary.json"), "utf8")) as Case[];

function items(blocks: readonly Block[]): PageRequirement[] {
  const byId = new Map(blocks.map(block => [block.id, block]));
  const ancestors = (block: Block): string[] => block.parent ? [...ancestors(byId.get(block.parent)!), block.parent] : [];
  return blocks.map(block => ({ id: block.id, title: block.id, ancestors: ancestors(block),
    attrs: { ...requirementDefaults, key: block.key, direction: block.direction, state: block.state, date: block.date, party: block.party, requires: block.requires.join(",") } }));
}

describe("the overview's numbers, as the server's summary", () => {
  it.each(cases.map(entry => [entry.name, entry] as const))("%s", (_name, entry) => {
    const page = items(entry.blocks), summary = summarize(page), byKey = keyed(page);
    expect({ total: page.length, applicable: summary.total, done: summary.done, percent: summary.percent, waiting: summary.waiting.length,
      inboundMissing: summary.missing.map(item => item.id), states: summary.counts }).toEqual(entry.summary);
    expect(Object.fromEntries(page.map(item => [item.id, awaitedBy(item, byKey).map(other => other.attrs.key)]).filter(([, keys]) => keys.length))).toEqual(entry.waitingOn);
    // The server names nobody with an empty party; the card says „Bez určení“.
    expect(waitingByParty(page).map(group => [group.party === unknownParty ? "" : group.party, group.items.map(item => item.id)])).toEqual(entry.parties);
  });
});
