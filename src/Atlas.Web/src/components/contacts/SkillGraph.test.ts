import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SkillGraph from "./SkillGraph.vue";
import type { SkillGraph as Graph } from "../../contacts/skillApi";

vi.stubGlobal("ResizeObserver", class { observe() {} disconnect() {} });
// ECharts needs a canvas jsdom has not: the chart is a stand-in that only takes the option.
const setOption = vi.hoisted(() => vi.fn());
vi.mock("@atlas/markdown/plugins/charts", () => ({ loadGraphRenderer: async () => ({ createGraphChart: () => ({ on() {}, setOption, resize() {}, dispose() {} }) }) }));
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

const person: Graph = { truncated: false, nodes: [
  { id: "c-1", kind: "person", name: "Eva Dvořáková", people: 0 },
  { id: "t-1", kind: "topic", name: "Kubernetes", people: 3 },
], links: [{ source: "c-1", target: "t-1", kind: "skill", level: 4 }] };

it("draws one person's topics for their profile: asked of the server for that person, without the map's filters", async () => {
  const graph = vi.fn(async () => person);
  wrapper = mount(SkillGraph, { props: { contactId: "c-1", api: { graph, contact: vi.fn(), rate: vi.fn(), topics: vi.fn() } } });
  await flushPromises();
  expect(graph).toHaveBeenCalledWith(null, undefined, "c-1");
  expect(wrapper.find(".skill-graph-toolbar").exists()).toBe(false);
  expect(wrapper.find("[data-skill-graph]").attributes("data-person")).toBe("c-1");
  // Drawn in the profile's narrow dock too: the graph, not the map's list.
  await vi.dynamicImportSettled(); await flushPromises();
  expect(setOption.mock.calls.at(-1)?.[0].series[0].data.map((node: { name: string }) => node.name)).toEqual(["Kubernetes", "Eva Dvořáková"]);
});

it("shows nothing of its own for a person with no topic yet (the level bars beneath say so)", async () => {
  const graph = vi.fn(async () => ({ truncated: false, nodes: [], links: [] }));
  wrapper = mount(SkillGraph, { props: { contactId: "c-1", api: { graph, contact: vi.fn(), rate: vi.fn(), topics: vi.fn() } } });
  await flushPromises();
  expect(wrapper.find("[data-skill-graph]").exists()).toBe(false);
});
