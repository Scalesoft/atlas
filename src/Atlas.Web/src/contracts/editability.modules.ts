import { vi } from "vitest";

/**
 * `settle` for the components built on the block module host: modules render lazily at the viewport and jsdom has no layout, so a
 * focus on each activates it (`prepare` would cancel a pending re-render), then the page waits until none is still rendering.
 */
export async function settleModules() {
  document.querySelectorAll("[data-md-module]").forEach(element => element.dispatchEvent(new FocusEvent("focusin")));
  await vi.waitFor(() => {
    const busy = [...document.querySelectorAll<HTMLElement>("[data-md-render-state]")].filter(element => !["ready", "fallback"].includes(element.dataset.mdRenderState!));
    if (busy.length) throw new Error(`modules still rendering: ${busy.map(element => element.dataset.mdRenderState).join(", ")}`);
  }, { timeout: 4000 });
}
