import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import { mountPage, problem, testContext } from "../general/testing";
import SpaceSearchGapsPage from "./SpaceSearchGapsPage.vue";

const gaps = [
  { query: "vpn heslo", normalizedQuery: "vpn heslo", users: 5, lastSearchedOn: "2026-10-03" },
  { query: "dovolená", normalizedQuery: "dovolena", users: 3, lastSearchedOn: "2026-10-01" },
];
const list = (items = gaps) => ({ items, minimumUsers: 3, retentionDays: 90 });
const ofDays = (path: string) => new URL(path, "http://atlas.test").searchParams.get("days");

let mounted: ReturnType<typeof mountPage> | undefined;
// Like general/testing fakeApi, but keeps the query string: the period is part of what is asked.
type Handler = (method: string, path: string) => unknown;
async function open(handler: Handler, context = testContext()) {
  clearCsrf();
  const calls: { method: string; path: string; body: unknown }[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: string, init?: RequestInit) => {
    const url = new URL(String(input), "http://atlas.test");
    const method = init?.method ?? "GET";
    if (url.pathname === "/api/auth/csrf") return Response.json({ token: "csrf" });
    const path = `${url.pathname.slice("/api".length)}${url.search}`;
    calls.push({ method, path, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const result = handler(method, path);
    if (result instanceof Response) return result;
    return result === undefined ? new Response(null, { status: 204 }) : Response.json(result);
  }));
  mounted = mountPage(SpaceSearchGapsPage, { spaceId: "provoz" }, { hash: "#/spaces/provoz/settings/search-gaps", context });
  await flushPromises();
  return { calls, view: mounted.wrapper, flash: mounted.flash, context };
}
afterEach(() => {
  mounted?.wrapper.unmount();
  mounted = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("Nenalezené dotazy", () => {
  it("lists the gaps for the chosen period", async () => {
    const { calls, view } = await open((method, path) => (path.startsWith("/spaces/provoz/search-gaps") ? list(ofDays(path) === "7" ? [gaps[0]!] : gaps) : undefined));
    expect(calls.map((call) => call.path)).toEqual(["/spaces/provoz/search-gaps?days=30"]);
    const rows = view.findAll("[data-search-gap]");
    expect(rows.map((row) => row.get("strong").text())).toEqual(["„vpn heslo“", "„dovolená“"]);
    expect(rows[0]!.text()).toContain("Hledalo 5 lidí · naposledy 3. 10. 2026");
    expect(rows[1]!.text()).toContain("Hledali 3 lidé · naposledy 1. 10. 2026");
    expect(view.text()).toContain("hledali je aspoň 3 různí lidé");
    expect(view.text()).toContain("uchovává se 90 dní");
    await view.get('[data-value="7"]').trigger("click");
    await flushPromises();
    expect(calls.at(-1)!.path).toBe("/spaces/provoz/search-gaps?days=7");
    expect(view.findAll("[data-search-gap]")).toHaveLength(1);
    expect(view.findAll("[data-search-gap]")[0]!.text()).toContain("vpn heslo");
  });

  it("labels unanswered and reported queries and looks as before without them", async () => {
    const withReasons = [
      { ...gaps[0]!, unanswered: 3, reported: 4 },
      { ...gaps[1]!, unanswered: 3 },
      { query: "cestovní náhrady", normalizedQuery: "cestovni nahrady", users: 4, lastSearchedOn: "2026-09-01" },
    ];
    const { view } = await open((method, path) => (path.startsWith("/spaces/provoz/search-gaps") ? list(withReasons) : undefined));
    const row = (key: string) => view.get(`[data-search-gap="${key}"]`);
    expect(row("vpn heslo").get("[data-gap-unanswered]").text()).toBe("Bez odpovědi asistenta: 3");
    expect(row("vpn heslo").get("[data-gap-reported]").text()).toBe("Nahlášeno: 4");
    expect(row("dovolena").find("[data-gap-unanswered]").exists()).toBe(true);
    expect(row("dovolena").find("[data-gap-reported]").exists()).toBe(false);
    // Without a reason (the server omits zero counts) the row has no badge at all, as before phase 3.
    expect(row("cestovni nahrady").find("[data-gap-unanswered], [data-gap-reported]").exists()).toBe(false);
    expect(view.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
  });

  it("resolving removes the row and confirms it", async () => {
    const { calls, view, flash } = await open((method, path) => (method === "POST" ? undefined : list()));
    await view.get('[data-search-gap="dovolena"] [data-resolve-gap]').trigger("click");
    await flushPromises();
    const post = calls.find((call) => call.method === "POST")!;
    expect(post.path).toBe("/spaces/provoz/search-gaps/resolutions");
    expect(post.body).toEqual({ normalizedQuery: "dovolena" });
    expect(view.find('[data-search-gap="dovolena"]').exists()).toBe(false);
    expect(view.find('[data-search-gap="vpn heslo"]').exists()).toBe(true);
    expect(flash().message.value?.text).toContain("dovolená");
  });

  it("folds the resolved row away before it leaves the list", async () => {
    // jsdom has no Web Animations: stand in for one that ends when the test says so.
    let finish!: () => void;
    const finished = new Promise<void>((resolve) => { finish = resolve; });
    const animate = vi.fn(() => ({ finished }));
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: animate });
    try {
      const { view } = await open((method) => (method === "POST" ? undefined : list()));
      await view.get('[data-search-gap="dovolena"] [data-resolve-gap]').trigger("click");
      await flushPromises();
      const row = view.get('[data-search-gap="dovolena"]');
      // collapseHeight from the A0 primitives: out of use at once, still drawn while it animates.
      expect(animate).toHaveBeenCalledOnce();
      expect(row.attributes("inert")).toBeDefined();
      expect(row.attributes("aria-hidden")).toBe("true");
      finish();
      await flushPromises();
      expect(view.find('[data-search-gap="dovolena"]').exists()).toBe(false);
    } finally {
      delete (HTMLElement.prototype as { animate?: unknown }).animate;
    }
  });

  it("keeps the row and says why when resolving fails", async () => {
    const { view, flash } = await open((method) => (method === "POST" ? problem(500, "Server neodpovídá.") : list()));
    await view.get('[data-search-gap="dovolena"] [data-resolve-gap]').trigger("click");
    await flushPromises();
    expect(view.find('[data-search-gap="dovolena"]').exists()).toBe(true);
    expect(view.get("[role=alert]").text()).toContain("Server neodpovídá.");
    expect(flash().message.value).toBeNull();
  });

  it("create page passes the query as the title", async () => {
    const { view, context } = await open(() => list());
    await view.get('[data-search-gap="vpn heslo"] [data-create-page]').trigger("click");
    expect(context.newPage).toHaveBeenCalledWith("provoz", { title: "vpn heslo" });
  });

  it("shows the empty state", async () => {
    const { view } = await open(() => list([]));
    expect(view.findAll("[data-search-gap]")).toHaveLength(0);
    expect(view.get("[role=status]").text()).toContain("Zatím tu nejsou dotazy, které by bez výsledku hledali aspoň 3 lidé.");
  });

  it("is not shown when the server answers 404, and retries after a failure", async () => {
    const gone = await open(() => problem(404, "Nenalezeno"));
    expect(gone.view.text()).toContain("Tato sekce neexistuje");
    expect(gone.view.find("[data-period]").exists()).toBe(false);
    mounted?.wrapper.unmount();
    let fail = true;
    const { view } = await open(() => (fail ? problem(503, "Hledání je nedostupné.") : list()));
    expect(view.get("[role=alert]").text()).toContain("Hledání je nedostupné.");
    fail = false;
    await view.get("[role=alert] button").trigger("click");
    await flushPromises();
    expect(view.findAll("[data-search-gap]")).toHaveLength(2);
  });
});
