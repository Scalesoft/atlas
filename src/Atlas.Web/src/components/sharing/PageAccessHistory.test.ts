import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageAccessHistory from "./PageAccessHistory.vue";
import { sharingApi, type PageAccessEvent } from "../../sharing";

vi.mock("../../sharing", () => ({ sharingApi: { accessHistory: vi.fn() } }));
let wrapper: VueWrapper | undefined;
const event = (id: string, over: Partial<PageAccessEvent> = {}): PageAccessEvent => ({ id, action: "page.opened", occurredAt: "2026-10-05T10:00:00Z", actorId: "u", actorName: "Jana", shareLinkId: null, shareLinkName: null, revisionVersion: 2, ...over });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.resetAllMocks(); });

describe("page access history channel", () => {
  it("says the page was opened through MCP and keeps web and legacy rows plain", async () => {
    vi.mocked(sharingApi.accessHistory).mockResolvedValue({ items: [event("1", { channel: "mcp", apiToken: { id: "t", name: "Claude Code", revoked: false } }), event("2", { channel: "web" }), event("3", { channel: null })], nextBeforeId: null });
    wrapper = mount(PageAccessHistory, { props: { pageId: "p" } }); await flushPromises();
    const text = wrapper.text();
    expect(text).toContain("otevřel(a) stránku přes MCP");
    expect(text.match(/přes MCP/g)).toHaveLength(1);
    // The plain web and legacy opens of the same minute fold into one line ("2×").
    expect(text.match(/otevřel\(a\) stránku/g)).toHaveLength(2);
    expect(wrapper.get("[data-activity-repeat]").text()).toBe("2×");
  });

  // T3: the server reports who marked the page as its space's requirement catalog (A2-4); the history did not show it.
  it("shows who marked the page as the requirement catalog and unmarked it, when and through which channel", async () => {
    vi.mocked(sharingApi.accessHistory).mockResolvedValue({ items: [event("1")], nextBeforeId: null, catalogMarks: [
      { id: "m2", isCatalog: false, occurredAt: "2026-10-27T09:00:00Z", actorId: "u2", actorName: "Petr", channel: "mcp", apiToken: { id: "t", name: "Claude Code", revoked: false } },
      { id: "m1", isCatalog: true, occurredAt: "2026-10-26T09:00:00Z", actorId: "u", actorName: "Jana", channel: "web" },
    ] });
    wrapper = mount(PageAccessHistory, { props: { pageId: "p" } }); await flushPromises();
    const marks = wrapper.get("[data-catalog-marks]");
    expect(marks.text()).toContain("Katalog požadavků");
    expect(marks.text()).toContain("Petr");
    expect(marks.text()).toContain("zrušil(a) označení katalogu přes MCP");
    // The token sits in the row's details, as for an opening.
    expect(marks.text()).toContain("Podrobnosti");
    expect(marks.text()).toContain("Jana");
    expect(marks.text()).toContain("označil(a) stránku jako katalog");
  });

  it("shows no catalog part for a page that never was one", async () => {
    vi.mocked(sharingApi.accessHistory).mockResolvedValue({ items: [event("1")], nextBeforeId: null });
    wrapper = mount(PageAccessHistory, { props: { pageId: "p" } }); await flushPromises();
    expect(wrapper.find("[data-catalog-marks]").exists()).toBe(false);
  });
});
