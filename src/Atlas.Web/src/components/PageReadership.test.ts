import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageReadership from "./PageReadership.vue";
import { api } from "../api";
import { pragueNow } from "@atlas/markdown/core";
vi.mock("../api", async original => ({ ...(await original<typeof import("../api")>()), api: vi.fn() }));
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.resetAllMocks(); document.body.innerHTML = ""; });

const year = pragueNow().date.slice(0, 4);
const day = `${year}-01-01`; // never a future day, which the calendar disables
const view = (userName: string, at = `${day}T10:00:00Z`) => ({ kind: "view", userId: userName, userName, at });
const secret = { kind: "secret", userId: "jana", userName: "Jana", at: `${day}T11:00:00Z`, secretTitle: "Router", fields: ["userName", "password"], operation: "reveal" };
const summary = (items: unknown[], extra = {}) => ({ views: 2, readers: 1, secrets: 1, days: [{ day, views: 2, secrets: 1 }], items, hasMore: false, ...extra });
const open = () => (wrapper = mount(PageReadership, { props: { pageId: "p" }, attachTo: document.body }));
const text = () => document.body.textContent ?? "";

it("loads the year: counts, a calendar with the password day marked and the log with the release set apart", async () => {
  vi.mocked(api).mockResolvedValueOnce(summary([secret, view("Jana")]));
  open(); await flushPromises();
  expect(vi.mocked(api).mock.calls[0]![0]).toBe(`/analytics/pages/p?from=${year}-01-01&to=${year}-12-31&offset=0`);
  expect(text()).toContain("2 zobrazení");
  expect(text()).toContain("1 čtenář");
  expect(text()).not.toContain("1 čtenářů");
  expect(document.querySelector("[data-readership-secrets]")?.textContent).toContain("1× zobrazené heslo");
  expect(document.querySelector(`[data-day="${day}"]`)?.hasAttribute("data-marked")).toBe(true);
  const release = document.querySelector('[data-kind="secret"]')!;
  expect(release.textContent).toContain("Heslo zobrazeno");
  expect(release.textContent).toContain("Router · uživatelské jméno, heslo");
  expect(document.querySelectorAll('[data-kind="view"]')).toHaveLength(1);
});

it("narrows the log to the day picked in the calendar, pages it and returns to the whole year", async () => {
  vi.mocked(api)
    .mockResolvedValueOnce(summary([view("Jana"), view("Petr")], { hasMore: true }))
    .mockResolvedValueOnce(summary([view("Eva")], { hasMore: true }))
    .mockResolvedValueOnce(summary([view("Ota")]))
    .mockResolvedValueOnce(summary([view("Jana")]));
  open(); await flushPromises();
  (document.querySelector(`[data-day="${day}"]`) as HTMLButtonElement).click(); await flushPromises();
  expect(vi.mocked(api).mock.calls[1]![0]).toBe(`/analytics/pages/p?from=${day}&to=${day}&offset=0`);
  expect(text()).toContain("Eva"); expect(text()).not.toContain("Petr");
  (document.querySelector("[data-more]") as HTMLButtonElement).click(); await flushPromises();
  expect(vi.mocked(api).mock.calls[2]![0]).toBe(`/analytics/pages/p?from=${day}&to=${day}&offset=1`);
  expect(text()).toContain("Eva"); expect(text()).toContain("Ota");
  (document.querySelector("[data-readership-year-log]") as HTMLButtonElement).click(); await flushPromises();
  expect(vi.mocked(api).mock.calls[3]![0]).toBe(`/analytics/pages/p?from=${year}-01-01&to=${year}-12-31&offset=0`);
  expect(text()).not.toContain("Eva");
});

it("shows a retryable error", async () => {
  vi.mocked(api).mockRejectedValueOnce(new Error("Výpadek")).mockResolvedValueOnce(summary([view("Jana")]));
  open(); await flushPromises();
  expect(document.querySelector('[role="alert"]')?.textContent).toContain("Výpadek");
  (document.querySelector("[data-readership-error] button") as HTMLButtonElement).click(); await flushPromises();
  expect(text()).toContain("Jana");
});

it("waits for the side panel exit before asking its owner to unmount it", async () => {
  const animations: EventTarget[] = [];
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: vi.fn(() => {
    const animation = Object.assign(new EventTarget(), { finished: new Promise(() => {}), cancel: vi.fn() });
    animations.push(animation); return animation;
  }) });
  try {
    vi.mocked(api).mockResolvedValue(summary([]));
    open(); await flushPromises();
    await wrapper!.get('button[aria-label="Zavřít"]').trigger("click");
    expect(wrapper!.emitted("close")).toBeUndefined();
    expect(wrapper!.get("dialog").attributes("inert")).toBeDefined();
    animations.at(-1)!.dispatchEvent(new Event("finish"));
    expect(wrapper!.emitted("close")).toHaveLength(1);
  } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); }
});