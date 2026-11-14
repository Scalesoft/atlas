import { flushPromises, mount } from "@vue/test-utils";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../../api";
import type { RequirementBlock, RequirementOverview, WaitingList as WaitingListShape } from "./overviewApi";
import DeploymentOverview from "./DeploymentOverview.vue";
import WaitingList from "./WaitingList.vue";

const api = vi.hoisted(() => vi.fn());
vi.mock("../../api", async original => ({ ...(await original<typeof import("../../api")>()), api }));
const list = vi.hoisted(() => vi.fn());
vi.mock("../../objects/objectApi", () => ({ objectApi: { list } }));

const block = (blockId: string, key: string, fields: Partial<RequirementBlock> = {}): RequirementBlock => ({
  blockId, key, title: key, direction: "internal", state: "planned", date: "", party: "", where: "", requires: [], parentBlockId: null, position: 0, ...fields,
});
const longTitle = "Nasazení portálu a MPI v nemocnici Dubovice, produkční prostředí po migraci na novou infrastrukturu";
const all: RequirementOverview = {
  keys: ["portal.sms", "portal.ssl"],
  pages: [
    { pageId: "kbn", title: longTitle, percent: 50, cells: [block("b1", "portal.sms", { title: "SMS brána", direction: "inbound", state: "waiting_for_input", date: "2026-10-12" }), block("b2", "portal.ssl", { title: "SSL certifikát", state: "done", date: "2026-09-10" })] },
    // A key only some pages have: Kostelec has no SMS gateway block.
    { pageId: "pri", title: "Kostelec", percent: 0, cells: [block("b3", "portal.ssl", { title: "SSL" })] },
  ],
};

function serve(answers: Record<string, unknown>) {
  api.mockImplementation(async (path: string) => {
    const answer = answers[path];
    if (answer instanceof Error) throw answer;
    if (answer === undefined) throw new Error(`unexpected ${path}`);
    return structuredClone(answer);
  });
}
const overviewPath = (filter: string) => `/projector/spaces/s1/overview?filter=${filter}`;
const catalog = { definitions: [{ key: "portal.ssl", title: "SSL certifikát", bundle: "Portál" }, { key: "portal.sms", title: "SMS brána", bundle: "Portál" }], warnings: [] };

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date("2026-10-27T10:00:00Z"));
  list.mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, canCreate: false });
});
afterEach(() => { vi.useRealTimers(); api.mockReset(); list.mockReset(); });

describe("DeploymentOverview", () => {
  it("switches to deployments × products: whole, in part or missing, a link to the product's first block", async () => {
    serve({ [overviewPath("all")]: all, "/projector/spaces/s1/catalog?bodies=false": { definitions: [
      { key: "portal.ssl", title: "SSL certifikát", bundle: "Portál", moduleKey: "h1" }, { key: "portal.sms", title: "SMS brána", bundle: "Portál", moduleKey: "h1" },
      { key: "mpi.nis", title: "NIS", bundle: "MPI", moduleKey: "h2" }], warnings: [] } });
    const wrapper = mount(DeploymentOverview, { props: { spaceId: "s1" }, attachTo: document.body });
    await flushPromises();
    await wrapper.get('[data-overview-view] [data-value="products"]').trigger("click");
    await flushPromises();
    const table = wrapper.get("[data-overview-products]");
    expect(table.findAll("thead [data-column]").map(cell => cell.text())).toEqual(["Portál", "MPI"]);
    const cells = table.findAll("tbody tr").map(row => row.findAll("td a").map(link => [link.text(), link.attributes("data-product")]));
    expect(cells).toEqual([[["má", "have"], ["–", "none"]], [["1/2", "partial"], ["–", "none"]]]);
    // The first of the product's requirements in catalog order.
    expect(table.get("tbody tr td a").attributes("href")).toBe("#/pages/kbn?heading=b2");
    wrapper.unmount();
  });

  it("shows what is missing first: pages × keys with state dots, dates and days waiting, grouped by bundle", async () => {
    serve({ [overviewPath("all")]: all, "/projector/spaces/s1/catalog?bodies=false": catalog });
    const wrapper = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(wrapper.get('[role="radio"][data-state="checked"]').text()).toBe("Co chybí");
    expect(wrapper.findAll("thead [data-group]").map(cell => cell.text())).toEqual(["Portál"]);
    expect(wrapper.findAll("thead [data-column]").map(cell => cell.text())).toEqual(["SMS brána", "SSL certifikát"]);
    const rows = wrapper.findAll("tbody tr");
    expect(rows[0]!.get("th a").attributes("href")).toBe("#/pages/kbn");
    // A very long title is cut with an ellipsis; the whole of it stays in its title.
    expect(rows[0]!.get("th a").attributes("title")).toBe(longTitle);
    expect(rows[0]!.get("[data-percent]").text()).toBe("50 % hotovo");
    const [sms, ssl] = rows[0]!.findAll("td");
    expect(sms!.get("a").attributes("href")).toBe("#/pages/kbn?heading=b1");
    expect(sms!.get("a").attributes("aria-label")).toBe("SMS brána: čeká na vstup, 15 d");
    expect(sms!.attributes("data-matches")).toBe("true");
    expect(ssl!.attributes("data-matches")).toBe("false");
    expect(ssl!.text()).toContain("10. 9.");
    // The key Kostelec does not have is an empty cell, not a link.
    expect(rows[1]!.findAll("td")[0]!.find("a").exists()).toBe(false);
  });

  it("asks the server once for the whole space and filters on the page, hiding a row with nothing left", async () => {
    serve({ [overviewPath("all")]: all, "/projector/spaces/s1/catalog?bodies=false": catalog });
    const wrapper = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    // Kostelec's only block (SSL) is planned, so it stays under „Co chybí“; under „Co nám nebylo dodáno“ nothing of it is left.
    await wrapper.findAll('[role="radio"]').find(radio => radio.text() === "Co nám nebylo dodáno")!.trigger("click");
    await flushPromises();
    expect(wrapper.findAll("tbody th a").map(link => link.text())).toEqual([longTitle]);
    await wrapper.findAll('[role="radio"]').find(radio => radio.text() === "Vše")!.trigger("click");
    await flushPromises();
    expect(wrapper.findAll("tbody th a").map(link => link.text())).toEqual([longTitle, "Kostelec"]);
    expect(api.mock.calls.map(call => call[0]).filter(path => path.includes("overview"))).toEqual([overviewPath("all")]);
  });

  it("draws a cell in at most three nodes, and a long list of pages in chunks after the first few", async () => {
    const many: RequirementOverview = { keys: ["portal.sms", "portal.ssl"], pages: Array.from({ length: 150 }, (_, n) => ({ pageId: `p${n}`, title: `Nasazení ${n}`, percent: 50,
      cells: [block(`s${n}`, "portal.sms", { direction: "inbound", state: "waiting_for_input", date: "2026-10-12" }), block(`t${n}`, "portal.ssl", { state: "done", date: "2026-09-10" })] })) };
    serve({ [overviewPath("all")]: many, "/projector/spaces/s1/catalog?bodies=false": catalog });
    const wrapper = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    const nodes = (cell: Element) => { let count = 0; const walk = (node: Node) => node.childNodes.forEach(child => { count++; walk(child); }); walk(cell); return count; };
    for (const cell of wrapper.findAll("tbody td").slice(0, 4)) expect(nodes(cell.element)).toBeLessThanOrEqual(3);
    expect(wrapper.findAll("tbody tr")).toHaveLength(8);
    await vi.waitFor(() => expect(wrapper.findAll("tbody tr")).toHaveLength(150));
  });

  it("switching the filter changes which cells stand out", async () => {
    serve({ [overviewPath("all")]: all, "/projector/spaces/s1/catalog?bodies=false": catalog });
    const wrapper = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    await wrapper.findAll('[role="radio"]').find(radio => radio.text() === "Co nám nebylo dodáno")!.trigger("click");
    await flushPromises();
    expect(wrapper.findAll("thead [data-column]").map(cell => cell.text())).toEqual(["SMS brána"]);
    await wrapper.findAll('[role="radio"]').find(radio => radio.text() === "Vše")!.trigger("click");
    await flushPromises();
    expect(wrapper.findAll("tbody td[data-matches='false']")).toHaveLength(0);
    await wrapper.findAll('[role="radio"]').find(radio => radio.text() === "Co chybí")!.trigger("click");
    await flushPromises();
    expect(wrapper.findAll("thead [data-column]").map(cell => cell.text())).toEqual(["SMS brána", "SSL certifikát"]);
  });

  it("every row and cell is a link, so Tab reaches it and Enter opens it", async () => {
    serve({ [overviewPath("all")]: all, "/projector/spaces/s1/catalog?bodies=false": catalog });
    const wrapper = mount(DeploymentOverview, { props: { spaceId: "s1" }, attachTo: document.body });
    await flushPromises();
    const links = wrapper.findAll("table a[href]");
    expect(links.map(link => link.attributes("href"))).toEqual(["#/pages/kbn", "#/pages/kbn?heading=b1", "#/pages/kbn?heading=b2", "#/pages/pri", "#/pages/pri?heading=b3"]);
    (links[1]!.element as HTMLElement).focus();
    expect(document.activeElement).toBe(links[1]!.element);
    // The filter is one Tab stop; an arrow moves to the next filter and selects it.
    const checked = wrapper.get('[role="radio"][data-state="checked"]');
    (checked.element as HTMLElement).focus();
    await checked.trigger("keydown", { key: "ArrowRight" });
    // The radio group moves the selection after the focus has moved, which can take more than a microtask under load.
    await vi.waitFor(() => expect(wrapper.get('[role="radio"][data-state="checked"]').text()).toBe("Co nám nebylo dodáno"));
    wrapper.unmount();
  });

  it("explains how to start when the space has no requirement blocks, and says so when nothing is missing", async () => {
    const empty = { keys: [], pages: [] };
    serve({ [overviewPath("all")]: empty, "/projector/spaces/s1/catalog?bodies=false": { definitions: [], warnings: [] } });
    const none = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(none.find("table").exists()).toBe(false);
    expect(none.text()).toContain("Zatím tu nejsou žádné požadavky");
    expect(none.text()).toContain("/Požadavek");
    // No page of some customer's: the hint names none.
    expect(none.text()).not.toContain("KBN");

    const done = { keys: ["portal.ssl"], pages: [{ pageId: "kbn", title: "KBN", percent: 100, cells: [block("b2", "portal.ssl", { state: "done" })] }] };
    serve({ [overviewPath("all")]: done, "/projector/spaces/s1/catalog?bodies=false": catalog });
    const complete = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(complete.text()).toContain("Nic nechybí");
    expect(complete.find("table").exists()).toBe(false);
    expect(complete.get("[data-overview-hidden]").text()).toBe("1 hotové nasazení skryto · Zobrazit vše");
  });

  it("says how many finished deployments a filter hides, and „Zobrazit vše“ shows them", async () => {
    const finished = (n: number) => ({ pageId: `hot${n}`, title: `Hotovo ${n}`, percent: 100, cells: [block(`h${n}`, "portal.ssl", { state: "done" })] });
    serve({ [overviewPath("all")]: { ...all, pages: [...all.pages, finished(1), finished(2)] }, "/projector/spaces/s1/catalog?bodies=false": catalog });
    const wrapper = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(wrapper.findAll("tbody th a").map(link => link.text())).toEqual([longTitle, "Kostelec"]);
    expect(wrapper.get("[data-overview-hidden]").text()).toBe("2 hotová nasazení skryta · Zobrazit vše");
    await wrapper.findAll('[role="radio"]').find(radio => radio.text() === "Co nám nebylo dodáno")!.trigger("click");
    await flushPromises();
    // Not finished, only with nothing left to deliver.
    expect(wrapper.get("[data-overview-hidden]").text()).toBe("3 nasazení bez chybějících vstupů skryta · Zobrazit vše");
    await wrapper.get("[data-overview-hidden] button").trigger("click");
    await flushPromises();
    expect(wrapper.get('[role="radio"][data-state="checked"]').text()).toBe("Vše");
    expect(wrapper.findAll("tbody th a")).toHaveLength(4);
    expect(wrapper.find("[data-overview-hidden]").exists()).toBe(false);
  });

  it("a space the account cannot read is a friendly „Nemáte přístup“; a failed catalog only loses the bundles", async () => {
    serve({ [overviewPath("all")]: new ApiError(404, "Not found"), "/projector/spaces/s1/catalog?bodies=false": new ApiError(404, "Not found") });
    const denied = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(denied.text()).toContain("Nemáte přístup");

    serve({ [overviewPath("all")]: all, "/projector/spaces/s1/catalog?bodies=false": new Error("down") });
    const plain = mount(DeploymentOverview, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(plain.findAll("thead [data-group]").map(cell => cell.text())).toEqual(["portal"]);
  });
});

describe("WaitingList", () => {
  const waiting: WaitingListShape = { parties: [
    { party: "Jan Malý", items: [
      { pageId: "kbn", pageTitle: "KBN", blockId: "b1", key: "portal.sms", title: "SMS brána", direction: "inbound", state: "waiting_for_input", date: "2026-10-01" },
      { pageId: "pri", pageTitle: "Kostelec", blockId: "b9", key: "portal.smtp", title: "SMTP", direction: "inbound", state: "waiting_for_input", date: "2026-10-20" },
    ] },
    { party: "", items: [{ pageId: "pri", pageTitle: "Kostelec", blockId: "b3", key: "y", title: "Podpis smlouvy", direction: "outbound", state: "waiting_for_input", date: "" }] },
  ] };

  it("groups by party, longest waiting first, with initials for a known contact and a link to each block", async () => {
    serve({ "/projector/spaces/s1/waiting": waiting });
    list.mockImplementation(async (query: { name: string[] }) => ({ items: query.name.includes("Jan Malý") ? [{ id: "c1", title: "Jan Malý", data: { name: "Jan Malý", organization: "IT KBN" } }] : [], total: 0, page: 1, pageSize: 50, canCreate: false }));
    const wrapper = mount(WaitingList, { props: { spaceId: "s1" } });
    await flushPromises();
    const groups = wrapper.findAll("[data-party]");
    expect(groups.map(group => group.get("h3").text())).toEqual(["Jan Malý", "Bez určení"]);
    expect(groups[0]!.get(".subject-avatar").text()).toBe("JM");
    // One request for all the parties, not one each.
    expect(list).toHaveBeenCalledTimes(1);
    expect(groups[0]!.text()).toContain("IT KBN");
    expect(groups[1]!.find(".subject-avatar").text()).toBe("");
    const items = groups[0]!.findAll("a[href]");
    expect(items.map(item => item.attributes("href"))).toEqual(["#/pages/kbn?heading=b1", "#/pages/pri?heading=b9"]);
    expect(items[0]!.text()).toContain("26 dní");
    expect(items[1]!.text()).toContain("7 dní");
    expect(groups[1]!.text()).toContain("bez termínu");
    // Every row waits for input: the list says it once, not as a pill on each row.
    expect(wrapper.find(".waiting-state").exists()).toBe(false);
    expect(wrapper.text()).not.toContain("čeká na vstup");
  });

  it("says when nothing is waiting, and a space it cannot read is „Nemáte přístup“", async () => {
    serve({ "/projector/spaces/s1/waiting": { parties: [] } });
    const empty = mount(WaitingList, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(empty.text()).toContain("Na nic nečekáme");

    serve({ "/projector/spaces/s1/waiting": new ApiError(404, "Not found") });
    const denied = mount(WaitingList, { props: { spaceId: "s1" } });
    await flushPromises();
    expect(denied.text()).toContain("Nemáte přístup");
  });
});

describe("motion", () => {
  // Reduced motion rewrites the tokens to a fade of at most 160 ms (motion.css); a literal or the long tokens would escape it.
  it("every animation and transition of the views runs on the short motion tokens", () => {
    for (const file of ["DeploymentOverview.vue", "WaitingList.vue"]) {
      const css = readFileSync(resolve(__dirname, file), "utf8").split("<style")[1] ?? "";
      const timings = [...css.matchAll(/(?:animation|transition)(?:-duration|-delay)?\s*:\s*([^;]+)/g)].map(match => match[1]!);
      expect(timings.length, file).toBeGreaterThan(0);
      for (const timing of timings) expect(timing, `${file}: ${timing}`).toMatch(/var\(--atlas-motion-(fast|base|slow)\)|var\(--st-stagger/);
    }
  });
});
