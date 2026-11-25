import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ContentHealthExample from "./ContentHealthExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

it("lists the pages read-and-unverified first with local data and verifies one in place", async () => {
  wrapper = mount(ContentHealthExample, { attachTo: document.body });
  await flushPromises();
  const live = () => wrapper!.findAll("[data-health-row]").filter(row => row.element.closest("dialog") === null);
  // The first shell is the populated one: the expired page with most readers leads.
  expect(live()[0]!.text()).toContain("Přístup VPN");
  expect(wrapper.text()).toContain("Ověřování obsahu je v tomto prostoru vypnuté");
  await live()[0]!.get("[data-health-row-verify]").trigger("click");
  await flushPromises();
  expect(wrapper.get('[data-page="00000000-0000-4000-8000-000000000101"] [data-health-state]').attributes("data-health-state")).toBe("verified");
});
