import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import ActivityHeatmap from "./ActivityHeatmap.vue";
import { shiftDay } from "../activity";
import { pragueNow } from "@atlas/markdown/core";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

const days = [{ day: "2025-01-02", count: 4 }, { day: "2025-01-03", count: 1 }];
const mountYear = (selected = "") => wrapper = mount(ActivityHeatmap, {
  props: { days, from: "2025-01-01", to: "2025-12-31", selected, label: "Kalendář uložených změn" },
  attachTo: document.body,
});

it("lays the range out in Monday-first weeks with intensity levels", () => {
  mountYear();
  const cells = wrapper!.findAll(".activity-calendar button");
  expect(cells[0]!.attributes("data-day")).toBe("2024-12-30");
  expect(cells[0]!.classes()).toContain("outside");
  expect(cells.at(-1)!.attributes("data-day")).toBe("2025-12-31");
  expect(wrapper!.get('[data-day="2025-01-02"]').attributes("data-level")).toBe("4");
  expect(wrapper!.get('[data-day="2025-01-03"]').attributes("data-level")).toBe("1");
  expect(wrapper!.get('[data-day="2025-01-04"]').attributes("data-level")).toBe("0");
  expect(wrapper!.get('[data-day="2025-01-02"]').attributes("aria-label")).toContain("4 změn");
  expect(wrapper!.findAll(".calendar-months span")).toHaveLength(12);
  expect(wrapper!.get('[role="group"]').attributes("aria-label")).toBe("Kalendář uložených změn");
});

it("emits the chosen day, marks the selection and moves focus by week within the range", async () => {
  mountYear("2025-01-02");
  expect(wrapper!.get('[data-day="2025-01-02"]').attributes("aria-pressed")).toBe("true");
  await wrapper!.get('[data-day="2025-01-02"]').trigger("click");
  expect(wrapper!.emitted("select")).toEqual([["2025-01-02"]]);

  await wrapper!.get('[data-day="2025-01-02"]').trigger("keydown", { key: "ArrowRight" });
  expect(wrapper!.get('[data-day="2025-01-09"]').attributes("tabindex")).toBe("0");
  expect(document.activeElement?.getAttribute("data-day")).toBe("2025-01-09");
  await wrapper!.get('[data-day="2025-01-09"]').trigger("keydown", { key: "End" });
  expect(wrapper!.get('[data-day="2025-12-31"]').attributes("tabindex")).toBe("0");
  await wrapper!.get('[data-day="2025-12-31"]').trigger("keydown", { key: "ArrowRight" });
  expect(wrapper!.get('[data-day="2025-12-31"]').attributes("tabindex")).toBe("0");
  await wrapper!.get('[data-day="2025-12-31"]').trigger("keydown", { key: "Home" });
  expect(wrapper!.get('[data-day="2025-01-01"]').attributes("tabindex")).toBe("0");
  expect(wrapper!.findAll('.activity-calendar button[tabindex="0"]')).toHaveLength(1);
});

it("starts focus on today and keeps future days unavailable", () => {
  const today = pragueNow().date;
  wrapper = mount(ActivityHeatmap, { props: { days: [], from: shiftDay(today, -20), to: shiftDay(today, 3), label: "Aktivita" } });
  expect(wrapper.get(`[data-day="${today}"]`).attributes("tabindex")).toBe("0");
  expect(wrapper.get(`[data-day="${today}"]`).attributes("disabled")).toBeUndefined();
  expect(wrapper.get(`[data-day="${shiftDay(today, 1)}"]`).attributes("disabled")).toBeDefined();
});

it("opens a calendar that does not fit scrolled to today instead of showing January", async () => {
  // jsdom has no layout: the scroller is 300 px wide and today's cell ends 900 px into it.
  const rect = (right: number) => ({ left: right - 10, right, top: 0, bottom: 10, width: 10, height: 10, x: right - 10, y: 0, toJSON: () => ({}) }) as DOMRect;
  const box = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    return this.dataset.day === pragueNow().date ? rect(900) : this.classList.contains("calendar-scroll") ? rect(300) : rect(0);
  });
  try {
    const today = pragueNow().date;
    wrapper = mount(ActivityHeatmap, { props: { days: [], from: `${today.slice(0, 4)}-01-01`, to: `${today.slice(0, 4)}-12-31`, label: "Aktivita" }, attachTo: document.body });
    await nextTick();
    expect(wrapper.get(".calendar-scroll").element.scrollLeft).toBe(603);
  } finally { box.mockRestore(); }
});

it("leaves a past year at its first week", async () => {
  const rect = (right: number) => ({ left: right - 10, right, top: 0, bottom: 10, width: 10, height: 10, x: right - 10, y: 0, toJSON: () => ({}) }) as DOMRect;
  const box = vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) { return this.classList.contains("calendar-scroll") ? rect(300) : rect(50); });
  try {
    mountYear();
    await nextTick();
    expect(wrapper!.get(".calendar-scroll").element.scrollLeft).toBe(0);
  } finally { box.mockRestore(); }
});

it("names the count with its unit and rings marked days, explained by the legend only when one is marked", () => {
  wrapper = mount(ActivityHeatmap, {
    props: { days: [{ day: "2025-01-02", count: 3, marked: 1 }, { day: "2025-01-03", count: 2 }], from: "2025-01-01", to: "2025-12-31", label: "Čtenost",
      unit: (count: number) => `${count} zobrazení`, markLabel: (count: number) => `${count}× zobrazené heslo`, markLegend: "Zobrazené heslo" },
  });
  const marked = wrapper.get('[data-day="2025-01-02"]');
  expect(marked.attributes("data-marked")).toBe("");
  expect(marked.attributes("aria-label")).toContain("3 zobrazení, 1× zobrazené heslo");
  expect(wrapper.get('[data-day="2025-01-03"]').attributes("data-marked")).toBeUndefined();
  expect(wrapper.get(".activity-legend").text()).toContain("Zobrazené heslo");
});

it("leaves the mark legend out without marked days", () => {
  wrapper = mount(ActivityHeatmap, { props: { days, from: "2025-01-01", to: "2025-12-31", label: "Čtenost", markLegend: "Zobrazené heslo" } });
  expect(wrapper.get(".activity-legend").text()).not.toContain("Zobrazené heslo");
});
