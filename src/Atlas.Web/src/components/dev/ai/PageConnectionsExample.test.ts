import { afterEach, expect, it } from "vitest";
import { DOMWrapper, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageConnectionsExample from "./PageConnectionsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

it("explains why the component exists and draws no ai glyph", async () => {
  wrapper = mount(PageConnectionsExample, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.text()).toContain("Proč nová komponenta");
  expect(wrapper.find(".atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
});

it("demonstrates the real graph variant with an outgoing link, many connections and an explicit empty state", async () => {
  wrapper = mount(PageConnectionsExample, { attachTo: document.body });
  await flushPromises();
  const graphs = wrapper.findAll(".page-connections-graph");
  // Both, many, related-only and the outage render a section; the empty and the failing demo render none.
  expect(graphs).toHaveLength(4);
  expect(graphs[0]!.findAll(".connections-graph-node")).toHaveLength(4);
  expect(graphs[0]!.text()).toContain("Odkaz ve stránce");
  expect(graphs[0]!.findAll(".connections-tags a[title^='Podobné tématem']")).toHaveLength(2);
  expect(graphs[1]!.findAll(".connections-graph-node").length).toBeGreaterThan(6);
  expect(graphs[0]!.get("details").attributes("open")).toBeUndefined();
});
// The demo that follows a caption; a caption is found by its text so that the test does not depend on the order of the demos.
function demoAfter(caption: string) {
  const label = wrapper!.findAll("p.connections-demo-label").find(p => p.text().includes(caption));
  const next = label?.element.nextElementSibling;
  return next ? new DOMWrapper(next) : undefined;
}

it("demonstrates a page with only similar articles, the outage of the related capability and a failing server", async () => {
  wrapper = mount(PageConnectionsExample, { attachTo: document.body });
  await flushPromises();
  const onlySimilar = demoAfter("řádek tagů bez mapy")!;
  expect(onlySimilar.findAll(".connections-tags a[title^='Podobné tématem']")).toHaveLength(3);
  expect(onlySimilar.find("details").exists()).toBe(false);
  const outage = demoAfter("Výpadek embeddingů")!;
  expect(outage.find("a[title^='Podobné tématem']").exists()).toBe(false);
  expect(outage.findAll(".connections-tags a")).toHaveLength(4);
  expect(demoAfter("Chyba serveru")).toBeUndefined();
});

it("demonstrates the list layout with its empty message, more than six rows, every relation, similar articles only and no capability", async () => {
  wrapper = mount(PageConnectionsExample, { attachTo: document.body });
  await flushPromises();
  const lists = wrapper.findAll("[data-connections-list]");
  expect(lists).toHaveLength(5);
  const relations = (list: (typeof lists)[number]) => list.findAll(".connections-list-card small").map(small => small.text().split(" · ")[0]);
  expect(lists[0]!.text()).toContain("Žádná stránka sem neodkazuje");
  expect(lists[1]!.findAll(".connections-list-card")).toHaveLength(6);
  expect(lists[1]!.get(".connections-list-more").text()).toContain("Zobrazit další (8)");
  expect(relations(lists[2]!)).toEqual(["Vzájemné odkazy", "Odkazuje sem", "Odkazuje sem", "Odkaz z textu", "Podobné tématem", "Podobné tématem"]);
  expect(relations(lists[3]!)).toEqual(["Podobné tématem", "Podobné tématem", "Podobné tématem"]);
  expect(relations(lists[4]!)).not.toContain("Podobné tématem");
});

it("ranks the person the related pages name first in the @ list while AI is on, and by the name alone when it is off", async () => {
  wrapper = mount(PageConnectionsExample, { attachTo: document.body });
  const names = () => wrapper!.get("[data-related-people-demo]").findAll("li").map(item => item.text().split(" · ")[0]);
  await flushPromises();
  expect(names()).toEqual(["Jan Dvořák", "Jan Novák", "Petr Janda"]);
  await wrapper.get(".related-people-switch button, .related-people-switch [role='switch']").trigger("click");
  expect(names()).toEqual(["Jan Novák", "Jan Dvořák", "Petr Janda"]);
});
