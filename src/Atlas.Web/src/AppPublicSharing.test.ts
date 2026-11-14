import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import App from "./App.vue";

afterEach(() => { vi.unstubAllGlobals(); history.replaceState(null, "", "/"); document.body.replaceChildren(); });

it("resolves a public path before setup or authentication and renders it outside the private shell", async () => {
  history.replaceState(null, "", "/s/sas_public");
  const contentJson = JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Veřejný obsah" }] }] } });
  const fetchMock = vi.fn(async (input: string | URL | Request) => {
    expect(String(input)).toBe("/api/shared/sas_public");
    return new Response(JSON.stringify({ title: "Sdílená stránka", contentJson, markdown: "Veřejný obsah", revisionVersion: 2, publishedAt: "2026-09-13T09:00:00Z", expiresAt: "2026-09-14T09:00:00Z" }), { status: 200, headers: { "Content-Type": "application/json" } });
  });
  vi.stubGlobal("fetch", fetchMock);
  const wrapper = mount(App, { attachTo: document.body });
  await flushPromises(); await flushPromises();
  // The public page is its own lazy chunk (the renderer stays out of the private shell's eager set).
  await vi.waitFor(() => expect(wrapper.text()).toContain("Sdílená stránka"), { timeout: 5000 });
  expect(wrapper.find('input[aria-label="Uživatelské jméno"]').exists()).toBe(false);
  expect(fetchMock).toHaveBeenCalledTimes(1);
  wrapper.unmount();
});
