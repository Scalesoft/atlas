import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PublishFindingsExample from "./PublishFindingsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.unstubAllGlobals(); document.body.innerHTML = ""; });

it("shows all four findings, the information-only case and a live control without backend calls", async () => {
  vi.useFakeTimers();
  const fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  wrapper = mount(PublishFindingsExample, { attachTo: document.body });
  await flushPromises();
  const frames = wrapper.findAll(".findings-frame");
  // The staged panel starts with the local rules and the running link check, then the links arrive.
  expect(frames[0]!.text()).toContain("Kontroluji…");
  await vi.advanceTimersByTimeAsync(1600);
  expect(frames[0]!.findAll("li.publish-finding strong").map(row => row.text())).toEqual([
    "Možné heslo v textu (sekce Přístup)", "Odkaz na nedostupnou stránku", "Odkaz na nepublikovanou stránku „Staré VPN“", "Prázdná sekce „Poznámky“",
  ]);
  expect(frames[1]!.findAll("li.publish-finding strong").map(row => row.text())).toEqual(["Prázdná sekce „Poznámky“"]);
  expect(wrapper.find(".publish-control").exists()).toBe(true);
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
  expect(fetchMock).not.toHaveBeenCalled();
});
