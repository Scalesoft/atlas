import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ProfilePage from "./ProfilePage.vue";
import { api } from "../api";
vi.mock("../api", async original => ({ ...(await original<typeof import("../api")>()), api: vi.fn() }));
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); vi.resetAllMocks(); });
it("shows the activity calendar, opens a day's changes and navigates to the page", async () => {
  const year = new Date().getFullYear();
  vi.mocked(api).mockImplementation(async path => {
    if (path.startsWith("/analytics/activity")) return { revisions: 3, pages: 2, days: [{ day: year + "-01-02", count: 3 }] };
    if (path.startsWith("/analytics/popular")) return { items: [] };
    if (path.startsWith("/analytics/changes")) return { items: [{ pageId: "p", title: "Příručka", authorName: "Jana", at: year + "-01-02T12:00:00Z", version: 2 }], hasMore: false };
    throw new Error(path);
  });
  wrapper = mount(ProfilePage, { props: { tab: "activity" as never, appearance: { layout: "connected", accent: "blue", mode: "light" }, appearanceDirty: false, appearancePending: false, appearanceError: "" } });
  await vi.waitFor(() => expect(wrapper!.find('[data-day="' + year + '-01-02"]').exists()).toBe(true));
  expect(wrapper.text()).toContain("3 změn");
  await wrapper.get('[data-day="' + year + '-01-02"]').trigger("click");
  await flushPromises();
  expect(wrapper.findAll('.activity-calendar button[tabindex="0"]')).toHaveLength(1);
  await wrapper.get('[data-day="' + year + '-01-02"]').trigger('keydown', { key: 'ArrowRight' });
  expect(wrapper.get('[data-day="' + year + '-01-09"]').attributes('tabindex')).toBe('0');
  expect(wrapper.text()).toContain("Jana");
  await wrapper.get("[data-change-page]").trigger("click");
  expect(wrapper.emitted("selectPage")?.[0]).toEqual(["p"]);
  // Rozsah is a FilterMenu: the choice lives in its menu, and a set filter repeats as a removable chip.
  await wrapper.get("[data-activity-scope]").trigger("click");
  await new Promise(resolve => setTimeout(resolve, 40));
  document.querySelector<HTMLElement>('.filter-menu-panel [data-value="wiki"]')!.click();
  await flushPromises();
  expect(wrapper.get('[aria-label="Použité filtry"]').text()).toContain("Aktivita wiki");
  expect(vi.mocked(api).mock.calls.some(([path]) => path.includes("scope=wiki"))).toBe(true);
});
it("counts readers of the most read pages in Czech (UX cycle 1, SH-9)", async () => {
  vi.mocked(api).mockImplementation(async path => {
    if (path.startsWith("/analytics/activity")) return { revisions: 0, pages: 0, days: [] };
    if (path.startsWith("/analytics/popular")) return { items: [{ pageId: "a", title: "Návod", views: 17, readers: 1 }, { pageId: "b", title: "Příručka", views: 1200, readers: 3 }] };
    if (path.startsWith("/analytics/changes")) return { items: [], hasMore: false };
    throw new Error(path);
  });
  wrapper = mount(ProfilePage, { props: { tab: "activity" as never, appearance: { layout: "connected", accent: "blue", mode: "light" }, appearanceDirty: false, appearancePending: false, appearanceError: "" } });
  await vi.waitFor(() => expect(wrapper!.find("[data-popular-page]").exists()).toBe(true));
  expect(wrapper.text()).toContain("17 zobrazení · 1 čtenář");
  expect(wrapper.text()).not.toContain("1 čtenářů");
  expect(wrapper.text()).toContain("3 čtenáři");
});
