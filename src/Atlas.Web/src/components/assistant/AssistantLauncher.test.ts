import { afterEach, describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import { useAiCapabilities } from "../../composables/useAiCapabilities";
import AssistantLauncher from "./AssistantLauncher.vue";

// The capabilities are loaded through the real composable; the tests set them through its fetch.
const realFetch = globalThis.fetch;
async function withCapability(assistant: boolean) {
  (globalThis as { fetch: typeof fetch }).fetch = async () => new Response(JSON.stringify({ assistant, answer: assistant }), { status: 200, headers: { "Content-Type": "application/json" } });
  useAiCapabilities().stop();
  await useAiCapabilities().refresh();
  await nextTick();
}
afterEach(() => { useAiCapabilities().stop(); (globalThis as { fetch: typeof fetch }).fetch = realFetch; });

describe("AssistantLauncher", () => {
  it("appears only with the assistant capability and on a published page in reading mode", async () => {
    await withCapability(false);
    const off = mount(AssistantLauncher, { props: { variant: "sidebar" } });
    const offPage = mount(AssistantLauncher, { props: { variant: "page", pageReading: true, canAsk: true } });
    expect(off.find("[data-assistant-launch]").exists()).toBe(false);
    expect(offPage.find("[data-assistant-launch]").exists()).toBe(false);
    expect(off.html()).not.toContain("data-ai-glyph");

    await withCapability(true);
    const sidebar = mount(AssistantLauncher, { props: { variant: "sidebar" } });
    expect(sidebar.get("[data-assistant-launch=sidebar]").text()).toContain("Asistent");
    expect(sidebar.find("[data-ai-glyph]").exists()).toBe(true);
    // The page entry is for reading a published page only (editing, a draft or another view: no button).
    expect(mount(AssistantLauncher, { props: { variant: "page", pageReading: false } }).find("[data-assistant-launch]").exists()).toBe(false);
    // It asks, so it follows the one availability rule (P11): hidden while no question can be answered now.
    expect(mount(AssistantLauncher, { props: { variant: "page", pageReading: true, canAsk: false } }).find("[data-assistant-launch]").exists()).toBe(false);
    const page = mount(AssistantLauncher, { props: { variant: "page", pageReading: true, canAsk: true } });
    expect(page.get("[data-assistant-launch=page]").text()).toContain("Zeptat se na tuto stránku");
    await page.get("[data-assistant-launch=page]").trigger("click");
    expect(page.emitted("launch")).toHaveLength(1);

    // Losing the capability removes the entry again.
    await withCapability(false);
    expect(sidebar.find("[data-assistant-launch]").exists()).toBe(false);
  });
});
