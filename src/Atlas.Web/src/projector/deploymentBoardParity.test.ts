import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, it } from "vitest";
import { arrangeChanges, composeChanges, deploymentMap, type ComposeItem } from "./deploymentMap";

// „Poskládat tabuli“ on the web and projector_compose_board on the server (DeploymentBoard.cs) lay out the same board: both answer the
// cases of tests/fixtures/content/deployment-board.json the same (DeploymentBoardParityTests). Ids are numbered in the order they are made.
type Cases = { name: string; items: (Omit<ComposeItem, "target"> & { target?: { resourceId: string } | null })[]; changes: unknown }[];
const fixture = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../../../../tests/fixtures/content/deployment-board.json"), "utf8")) as {
  pageId: string; document: Parameters<typeof deploymentMap>[0]; cases: Cases; headingDocument: Parameters<typeof deploymentMap>[0]; headingCases: Cases;
  orderDocument: Parameters<typeof deploymentMap>[0]; orderCases: Cases;
  arrangeCases: { name: string; pages: { pageId: string; document: "document" | "orderDocument" }[]; ids?: string[]; items: Cases[number]["items"]; changes: unknown }[];
};
const newIds = () => { let next = 0; return () => `00000000-0000-4000-8000-${String(++next).padStart(12, "0")}`; };

it("composes the cases of the shared fixture as the server does", () => {
  const map = deploymentMap(fixture.document);
  for (const entry of fixture.cases) expect(composeChanges(map, fixture.pageId, entry.items, newIds()), entry.name).toEqual(entry.changes);
});

// A converted page (requirement headings, model §8) with one legacy block left under a plain heading: both shapes are read.
it("composes the heading page's cases of the shared fixture as the server does", () => {
  const map = deploymentMap(fixture.headingDocument);
  for (const entry of fixture.headingCases) expect(composeChanges(map, fixture.pageId, entry.items, newIds()), entry.name).toEqual(entry.changes);
});

// An order's page (owner, 4.0): its own section first, an internal integration (a `system` naming another product), a service two
// products share; and a board a person laid out (moved and resized cards, their own note and arrow), which gets only new cards below.
it("composes the order page's cases of the shared fixture as the server does", () => {
  const map = deploymentMap(fixture.orderDocument);
  for (const entry of fixture.orderCases) expect(composeChanges(map, fixture.pageId, entry.items, newIds()), entry.name).toEqual(entry.changes);
});

// „Uspořádat jako architekturu“ (arrangeChanges) and projector_compose_board's `arrange` (DeploymentBoard.Arrange): the same moves and the same
// connector sides (a null one included), in the same order.
it("arranges the cases of the shared fixture as the server does", () => {
  for (const entry of fixture.arrangeCases) {
    const pages = entry.pages.map(page => [page.pageId, deploymentMap(fixture[page.document])] as const);
    expect(arrangeChanges(pages, entry.items, entry.ids), entry.name).toEqual(entry.changes);
  }
});
