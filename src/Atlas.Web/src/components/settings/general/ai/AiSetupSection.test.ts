import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../../api";
import { mountPage } from "../testing";
import AiModulePage from "./AiModulePage.vue";
import { useAiMock } from "../../../../testing/aiTesting";
let wrapper: VueWrapper | undefined;
beforeEach(clearCsrf);
afterEach(() => { wrapper?.unmount(); vi.unstubAllGlobals(); document.body.innerHTML = ""; history.replaceState(null, "", "/"); });
async function open(configured = false) {
 const harness = useAiMock({ enabled: configured, configured, activeIndex: configured });
 const mounted = mountPage(AiModulePage, { part: "setup" }, { hash: "#/settings/general/modules/ai/setup" });
 wrapper = mounted.wrapper; await flushPromises(); return { ...harness, ...mounted };
}
it("reveals the next settings inline only after a successful save, retaining earlier editable choices", async () => {
 const { wrapper: view, calls } = await open();
 expect(view.find('[data-ai-setup-step="power"]').exists()).toBe(true);
 expect(view.find('[data-ai-profile="embeddings"]').exists()).toBe(false);
 await view.get("[data-ai-switch]").trigger("click");
 await view.get("[data-save-bar-save]").trigger("click"); await flushPromises();
 expect(view.find('[data-ai-profile="embeddings"]').exists()).toBe(true);
 await view.get('[data-value="lmstudio"] [role="radio"]').trigger("click");
 await view.get("[data-ai-model]").setValue("text-embedding-bge-m3");
 expect(view.find("[data-ai-enabled]").exists()).toBe(false);
 await view.get("[data-save-bar-save]").trigger("click"); await flushPromises();
 expect(calls.find(c => c.method === "PUT" && c.path.endsWith("/profiles/embeddings"))?.body).toMatchObject({ enabled: true, model: "text-embedding-bge-m3" });
 expect(view.find("[data-ai-build-action]").exists()).toBe(true);
 expect(view.get('[data-ai-setup-step="embeddings"]').text()).toContain("text-embedding-bge-m3");
 expect(view.find('[data-ai-profile="chat"]').exists()).toBe(false);
});
it("leaves the open step alone when its own button is pressed again instead of closing and re-mounting its form", async () => {
 const { wrapper: view } = await open();
 const form = view.get('[data-ai-setup-step="power"] .ai-setup-form').element;
 const button = view.get('[data-ai-setup-edit="power"]');
 expect(button.attributes("aria-expanded")).toBe("true");
 await button.trigger("click"); await flushPromises();
 expect(view.get('[data-ai-setup-step="power"] .ai-setup-form').element).toBe(form);
 expect(button.attributes("aria-expanded")).toBe("true");
});
it("keeps one draft owner and prevents an inline section change from silently losing edits", async () => {
 const { wrapper: view, guard } = await open(true);
 await view.get('[data-ai-setup-edit="chat"]').trigger("click"); await flushPromises();
 await view.get("[data-ai-model]").setValue("another-chat");
 expect(guard.dirty.value).toBe(true);
 expect(view.get('[data-ai-setup-edit="embeddings"]').attributes("disabled")).toBeDefined();
 expect(view.findAll("[data-ai-profile]")).toHaveLength(1);
 await view.get("[data-save-bar-save]").trigger("click"); await flushPromises();
 expect(guard.dirty.value).toBe(false);
 expect(view.get('[data-ai-setup-step="chat"]').text()).toContain("another-chat");
});
it("does not invent unsaved changes when opening or canceling a new model form", async () => {
 const { wrapper: view, guard } = await open();
 await view.get("[data-ai-switch]").trigger("click");
 await view.get("[data-save-bar-save]").trigger("click"); await flushPromises();
 expect(guard.dirty.value).toBe(false);
 await view.get('[data-value="lmstudio"] [role="radio"]').trigger("click");
 await view.get("[data-ai-model]").setValue("temporary");
 expect(guard.dirty.value).toBe(true);
 await view.get("[data-save-bar-cancel]").trigger("click"); await flushPromises();
 expect(guard.dirty.value).toBe(false);
 expect(view.get('[data-ai-setup-edit="power"]').attributes("disabled")).toBeUndefined();
});

it("allows explicit reactivation of an existing disabled model without inventing a dirty draft", async () => {
 const { mock, calls } = useAiMock({ enabled: true, configured: true, activeIndex: true });
 mock.state.profiles.chat.enabled = false;
 const mounted = mountPage(AiModulePage, { part: "setup" }, { hash: "#/settings/general/modules/ai" });
 wrapper = mounted.wrapper; await flushPromises();
 expect(mounted.guard.dirty.value).toBe(false);
 await wrapper.get("[data-ai-enable-saved]").trigger("click"); await flushPromises();
 expect(calls.find(c => c.method === "PUT" && c.path.endsWith("/profiles/chat"))?.body).toMatchObject({ enabled: true });
 expect(wrapper.find("[data-ai-setup-complete]").exists()).toBe(true);
});

it("keeps model replacement on its dedicated page instead of an unguarded inline candidate", async () => {
 const { wrapper: view } = await open(true);
 await view.get('[data-ai-setup-edit="index"]').trigger("click"); await flushPromises();
 expect(view.find("[data-ai-replace-action]").exists()).toBe(false);
 expect(view.get("[data-ai-manage-index]").attributes("href")).toBe("#/settings/general/modules/ai/index");
});
