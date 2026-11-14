import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import ApiTokensExample from "./ApiTokensExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
});

it("shows the full, trimmed and MCP-without-module token variants with sample data", async () => {
  wrapper = mount(ApiTokensExample, { attachTo: document.body });
  await new Promise((resolve) => setTimeout(resolve, 250));
  await flushPromises();
  expect(wrapper.find("[data-api-access]").exists()).toBe(true);
  expect(wrapper.get('[data-token="t1"] [data-token-state]').text()).toBe("Omezeno správcem na čtení.");
  expect(wrapper.get('[data-token="m1"] [data-token-state]').text()).toContain("dokud správce nezapne MCP server");
  expect(wrapper.get('[data-token="f1"] [data-token-scope]').text()).toBe("Zápis");
  expect(wrapper.get('[data-api-token="a2"] [data-token-channel]').text()).toBe("MCP");
});
