import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AdminAiExample from "./AdminAiExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.innerHTML = "";
});

it("shows the progressive form and the generation swap with local data and never the AI glyph", async () => {
  wrapper = mount(AdminAiExample, { attachTo: document.body });
  await flushPromises();
  const fields = wrapper.findAll("[data-ai-fields]");
  expect(fields).toHaveLength(3);
  // Nothing depends on a provider until one is chosen.
  expect(fields[0]!.find("[data-ai-url]").exists()).toBe(false);
  await fields[0]!.get('[data-value="openai"] [role="radio"]').trigger("click");
  expect(fields[0]!.get("[data-ai-default-url]").text()).toContain("https://api.openai.com/v1");
  expect(wrapper.get("[data-ai-index-flow]").attributes("data-ai-index-flow")).toBe("replace");
  expect(wrapper.get('[data-step="profile"]').element).toBeTruthy();
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
  // A group grant reveals its "AI s nástroji" switch, on by default, above its limits.
  const grant = wrapper.get("[data-ai-access-group]");
  await grant.get("[data-ai-group-limits]").trigger("click");
  expect(grant.get("[data-ai-group-tools]").attributes("aria-checked")).toBe("true");
  expect(grant.find('[data-quota="agentDailyLimit"]').exists()).toBe(true);
});

it("shows a working Gateway profile with a model warning as needing attention, not as unavailable", async () => {
  wrapper = mount(AdminAiExample, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.get('[data-ai-model-summary="chat"]').text()).toContain("Vyžaduje pozornost");
  expect(wrapper.get('[data-ai-model-summary="quick"]').text()).toContain("Vyžaduje pozornost");
  expect(wrapper.get('[data-ai-model-summary="embeddings"]').text()).toContain("Nedostupné");
});
