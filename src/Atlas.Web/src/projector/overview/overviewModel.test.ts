// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { RequirementBlock, RequirementOverview, WaitingList } from "./overviewApi";
import { blockHref, cellText, daysSince, overviewMatrix, productMatrix, waitedFor, waitingGroups } from "./overviewModel";

const today = "2026-10-27";
let position = 0;
const block = (key: string, fields: Partial<RequirementBlock> = {}): RequirementBlock => ({
  blockId: `b-${key}-${position++}`, key, title: key, direction: "internal", state: "planned", date: "", party: "", where: "",
  requires: [], parentBlockId: null, position, ...fields,
});

describe("overviewMatrix", () => {
  const ssl = block("portal.ssl", { title: "SSL certifikát", state: "done", date: "2026-09-10" });
  const sms = block("portal.sms", { title: "SMS brána", direction: "inbound", state: "waiting_for_input", date: "2026-10-12" });
  const nis = block("mpi.nis", { title: "Předání NIS", state: "not_applicable" });
  const sslP = block("portal.ssl", { title: "SSL", state: "planned" });
  const all: RequirementOverview = {
    keys: ["mpi.nis", "portal.sms", "portal.ssl"],
    pages: [{ pageId: "kbn", title: "KBN — Production", cells: [nis, sms, ssl], percent: 50 }, { pageId: "pri", title: "Kostelec", cells: [sslP], percent: 0 }],
  };
  const missing: RequirementOverview = { keys: ["portal.sms", "portal.ssl"], pages: [{ pageId: "kbn", title: "KBN — Production", cells: [sms], percent: 50 }, { pageId: "pri", title: "Kostelec", cells: [sslP], percent: 0 }] };

  it("rows are every page, columns the keys of the filter grouped by bundle, else by prefix, with readable titles", () => {
    const matrix = overviewMatrix(all, "missing", { today, bundles: new Map([["portal.sms", "Portál"], ["portal.ssl", "Portál"]]) });
    expect(matrix.rows.map(row => row.title)).toEqual(["KBN — Production", "Kostelec"]);
    expect(matrix.columns.map(column => [column.key, column.title, column.group])).toEqual([["portal.sms", "SMS brána", "Portál"], ["portal.ssl", "SSL certifikát", "Portál"]]);
    expect(matrix.groups).toEqual([{ label: "Portál", span: 2 }]);
    const byPrefix = overviewMatrix(all, "all", { today });
    expect(byPrefix.groups).toEqual([{ label: "mpi", span: 1 }, { label: "portal", span: 2 }]);
  });

  it("a cell outside the filter is dimmed, a key a page lacks is null, the percent is the server's", () => {
    const matrix = overviewMatrix(all, "missing", { today });
    const [kbn, pri] = matrix.rows;
    expect(kbn!.cells.map(cell => cell && [cell.state, cell.matches])).toEqual([["waiting_for_input", true], ["done", false]]);
    expect(pri!.cells.map(cell => cell && cell.matches)).toEqual([null, true]);
    expect(kbn!.percent).toBe(50);
    expect(pri!.percent).toBe(0);
    expect(kbn!.href).toBe("#/pages/kbn");
    expect(kbn!.cells[0]!.href).toBe(`#/pages/kbn?heading=${sms.blockId}`);
  });

  it("columns of one bundle stay together in the server's key order", () => {
    const keys = ["a.one", "b.two", "c.three"];
    const overview: RequirementOverview = { keys, pages: [{ pageId: "p", title: "P", cells: keys.map(key => block(key)), percent: 0 }] };
    const matrix = overviewMatrix(overview, "all", { today, bundles: new Map([["a.one", "Síť"], ["c.three", "Síť"], ["b.two", "Portál"]]) });
    expect(matrix.columns.map(column => column.key)).toEqual(["a.one", "c.three", "b.two"]);
    expect(matrix.groups).toEqual([{ label: "Síť", span: 2 }, { label: "Portál", span: 1 }]);
  });

  it("derives the filters from the whole space as the server does: what is missing, what was not delivered", () => {
    const shape = (matrix: ReturnType<typeof overviewMatrix>) => ({ keys: matrix.columns.map(column => column.key),
      pages: matrix.rows.map(row => ({ pageId: row.pageId, matching: row.cells.filter(cell => cell?.matches).map(cell => cell!.blockId) })) });
    expect(shape(overviewMatrix(all, "missing", { today }))).toEqual({ keys: missing.keys, pages: missing.pages.map(page => ({ pageId: page.pageId, matching: page.cells.map(cell => cell.blockId) })) });
    expect(shape(overviewMatrix(all, "inbound-missing", { today }))).toEqual({ keys: ["portal.sms"], pages: [{ pageId: "kbn", matching: [sms.blockId] }] });
  });

  it("a filter leaves out a page with nothing left in it; „Vše“ shows every page", () => {
    const done: RequirementOverview = { keys: ["portal.ssl"], pages: [...all.pages, { pageId: "hot", title: "Hotovo", cells: [block("portal.ssl", { state: "done" })], percent: 100 }] };
    expect(overviewMatrix(done, "missing", { today }).rows.map(row => row.pageId)).toEqual(["kbn", "pri"]);
    expect(overviewMatrix(done, "inbound-missing", { today }).rows.map(row => row.pageId)).toEqual(["kbn"]);
    expect(overviewMatrix(done, "all", { today }).rows.map(row => row.pageId)).toEqual(["kbn", "pri", "hot"]);
    // How many the filter left out, so the view can say so.
    expect((["missing", "inbound-missing", "all"] as const).map(filter => overviewMatrix(done, filter, { today }).hidden)).toEqual([1, 2, 0]);
  });

  it("groups the columns by the blocks' section path when the server sends it, so two „Modul Core nasazen“ never share one group (U4-9)", () => {
    const portal = block("portal.core", { title: "Modul Core nasazen", section: ["Portál pacienta", "Core"] });
    const vpn = block("portal.core.vpn", { title: "VPN", parentBlockId: portal.blockId, section: ["Portál pacienta", "Core"] });
    const mpi = block("mpi.core", { title: "Modul Core nasazen", section: ["MPI", "Core"] });
    const overview: RequirementOverview = { keys: ["mpi.core", "portal.core", "portal.core.vpn"], pages: [{ pageId: "p", title: "P", cells: [mpi, portal, vpn], percent: 0 }] };
    const matrix = overviewMatrix(overview, "all", { today, bundles: new Map([["mpi.core", "Core"], ["portal.core", "Core"], ["portal.core.vpn", "Core"]]) });
    expect(matrix.groups).toEqual([{ label: "MPI › Core", span: 1 }, { label: "Portál pacienta › Core", span: 2 }]);
    expect(matrix.columns.map(column => [column.key, column.detail])).toEqual([["mpi.core", ""], ["portal.core", ""], ["portal.core.vpn", ""]]);
    // Without sections the catalog's bundle groups them; two columns of one title there say their keys.
    const bundled = overviewMatrix({ ...overview, pages: [{ ...overview.pages[0]!, cells: [mpi, portal, vpn].map(({ section: _, ...cell }) => cell) }] }, "all",
      { today, bundles: new Map([["mpi.core", "Core"], ["portal.core", "Core"], ["portal.core.vpn", "Core"]]) });
    expect(bundled.groups).toEqual([{ label: "Core", span: 3 }]);
    expect(bundled.columns.map(column => column.detail)).toEqual(["mpi.core", "portal.core", ""]);
  });

  it("says per page what can be deployed and what blocks, by the overview's one readiness rule (U4-9)", () => {
    const ssl = block("ssl", { direction: "inbound", state: "blocked" }), core = block("core", { requires: ["ssl"], state: "in_progress" });
    const nia = block("nia", { requires: ["core"] }), vpn = block("vpn", { direction: "inbound", state: "done" }), mpi = block("mpi", { requires: ["vpn"] });
    const out = block("out", { direction: "outbound" }), input = block("input", { direction: "inbound" });
    const overview: RequirementOverview = { keys: [], pages: [{ pageId: "p", title: "P", cells: [ssl, core, nia, vpn, mpi, out, input], percent: 14 }] };
    expect(overviewMatrix(overview, "all", { today }).rows[0]!.readiness).toEqual({ deployable: 1, handover: 1, blocking: 1 });
  });

  it("0 pages is an empty matrix", () => {
    expect(overviewMatrix({ keys: [], pages: [] }, "missing", { today })).toEqual({ rows: [], columns: [], groups: [], hidden: 0 });
  });
});

describe("cell text and links", () => {
  it("waiting shows the days since its date, others their date, the year only when it is not this one", () => {
    expect(cellText(block("a", { state: "waiting_for_input", date: "2026-10-12" }), today)).toBe("15 d");
    // PR2-3: a date still ahead is „za 16 d“, never „-16 d“ (the date of a waiting block is read as since when we wait, a deadline typed there must not read as a minus).
    expect(cellText(block("a", { state: "waiting_for_input", date: "2026-11-12" }), today)).toBe("za 16 d");
    expect(waitedFor(190)).toBe("190 dní");
    expect(waitedFor(-16)).toBe("za 16 dní");
    expect(waitedFor(-1)).toBe("zítra");
    expect(waitedFor(0)).toBe("od dneška");
    expect(waitedFor(null)).toBe("bez termínu");
    expect(cellText(block("a", { state: "waiting_for_input" }), today)).toBe("");
    expect(cellText(block("a", { state: "done", date: "2026-09-10" }), today)).toBe("10. 9.");
    expect(cellText(block("a", { state: "done", date: "2025-12-01" }), today)).toBe("1. 12. 2025");
    expect(daysSince("2026-10-27", today)).toBe(0);
    expect(daysSince("", today)).toBeNull();
    expect(daysSince("2026-13-45", today)).toBeNull();
  });

  it("a block link is the page route with the block as its anchor", () => {
    expect(blockHref("p-1", "0b7c")).toBe("#/pages/p-1?heading=0b7c");
  });
});

describe("waitingGroups", () => {
  const list: WaitingList = { parties: [
    { party: "IT KBN", items: [
      { pageId: "kbn", pageTitle: "KBN", blockId: "b1", key: "portal.sms", title: "SMS brána", direction: "inbound", state: "waiting_for_input", date: "2026-10-01" },
      { pageId: "kbn", pageTitle: "KBN", blockId: "b2", key: "x", title: "Bez data", direction: "internal", state: "waiting_for_input", date: "" },
    ] },
    { party: "", items: [{ pageId: "pri", pageTitle: "Kostelec", blockId: "b3", key: "y", title: "Y", direction: "outbound", state: "waiting_for_input", date: "2026-10-20" }] },
  ] };

  it("keeps the server's order (longest waiting first), names an unnamed party and counts the days", () => {
    const groups = waitingGroups(list, today);
    expect(groups.map(group => group.label)).toEqual(["IT KBN", "Bez určení"]);
    expect(groups[0]!.items.map(item => [item.title, item.days, item.href])).toEqual([["SMS brána", 26, "#/pages/kbn?heading=b1"], ["Bez data", null, "#/pages/kbn?heading=b2"]]);
    expect(groups[1]!.items[0]!.days).toBe(7);
  });
});

describe("productMatrix", () => {
  it("says per deployment which product (catalog module by its stable key) it has whole, in part or not at all", () => {
    const all: RequirementOverview = { keys: [], pages: [
      { pageId: "kbn", title: "KBN", percent: 0, cells: [block("portal.ssl", { blockId: "s1", state: "done" }), block("portal.sms", { blockId: "s2", state: "not_applicable" }), block("nia.cert")] },
      { pageId: "pri", title: "Kostelec", percent: 0, cells: [block("portal.ssl", { blockId: "s3" })] },
    ] };
    // Two headings named „Portál“ on two catalog pages are two products; one renamed keeps its key; before the first heading, none.
    const { columns, rows } = productMatrix(all, [{ key: "portal.ssl", bundle: "Portál", moduleKey: "h-portal" }, { key: "portal.sms", bundle: "Portál", moduleKey: "h-portal" },
      { key: "nia.cert", bundle: "NIA", moduleKey: "h-nia" }, { key: "nia", bundle: "NIA", moduleKey: "h-nia" }, { key: "loose", bundle: "" }]);
    expect(columns.map(column => [column.key, column.title, column.keys])).toEqual([["h-portal", "Portál", ["portal.ssl", "portal.sms"]], ["h-nia", "NIA", ["nia.cert", "nia"]]]);
    expect(rows.map(row => row.cells.map(cell => [cell.have, cell.total, cell.done]))).toEqual([[[2, 2, 2], [1, 2, 0]], [[1, 2, 0], [0, 2, 0]]]);
    expect(rows[0]!.cells[1]!.label).toBe("NIA: má 1 z 2 požadavků, hotovo 0");
    expect(rows[1]!.cells[1]!.label).toBe("NIA: nemá");
    expect(rows[0]!.cells[0]!.href).toBe(blockHref("kbn", "s1"));
  });
});
