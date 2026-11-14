import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SearchGapsExample from "./SearchGapsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

it("shows the gaps with local data, resolves a row and previews the new-page action without leaving the gallery", async () => {
  wrapper = mount(SearchGapsExample, { attachTo: document.body });
  await flushPromises();
  const rows = () => wrapper!.findAll("[data-search-gap]");
  expect(rows().map(row => row.attributes("data-search-gap"))).toEqual(["vpn heslo", "dovolena", "zalohovani databaze"]);
  await rows()[0]!.get("[data-create-page]").trigger("click");
  expect(wrapper.get("[data-result-banner]").text()).toContain("„vpn heslo“");
  await wrapper.get('[data-search-gap="dovolena"] [data-resolve-gap]').trigger("click");
  await flushPromises();
  expect(rows().map(row => row.attributes("data-search-gap"))).toEqual(["vpn heslo", "zalohovani databaze"]);
  expect(wrapper.text()).toContain("Zatím tu nejsou dotazy");
  expect(wrapper.text()).toContain("Server neodpovídá.");
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
});
