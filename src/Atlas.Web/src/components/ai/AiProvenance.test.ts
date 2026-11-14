import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AiProvenance from "./AiProvenance.vue";
import { provenance, source, withProviders } from "../dev/ai/aiSampleData";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.useRealTimers(); });
const mountLine = (props: Record<string, unknown>, slots: Record<string, () => unknown> = {}) => {
  wrapper = mount(withProviders(AiProvenance, { provenance, ...props }, slots), { attachTo: document.body });
  return wrapper;
};

it("says which model, how many sources and when, with common wording for company and cloud models", () => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-10-05T12:00:00Z"));
  mountLine({ sources: [source(1), source(2), source(3)], createdAt: "2026-10-05T11:58:00Z" });
  const line = document.querySelector("[data-ai-provenance]")!;
  expect(line.textContent).toContain("Firemní model");
  expect(line.textContent).toContain("3 zdroje");
  expect(line.textContent).toContain("před 2 minutami");
  expect(line.querySelectorAll("button")).toHaveLength(1);
  // the provider and model are only in the tooltip, not in the common wording
  expect(line.textContent).not.toContain("LM Studio");
  expect(line.querySelector("[data-ai-glyph]")).not.toBeNull();
  wrapper!.unmount();

  mountLine({ provenance: { location: "cloud", provider: "Anthropic", model: "x" }, sources: [source(1)], createdAt: "2026-10-03T08:00:00Z" });
  const cloud = document.querySelector("[data-ai-provenance]")!.textContent!;
  expect(cloud).toContain("Cloudový model");
  expect(cloud).toContain("1 zdroj");
  // after 24 hours the time is absolute
  expect(cloud).not.toContain("před");
  expect(cloud).toMatch(/3\. 10\. 2026/);
});

it("explains the answer in a popover with the scope and every source it was given, cited or not", async () => {
  mountLine({ sources: [source(1), source(2, { cited: false })], scope: { kind: "space", spaceId: "s" }, scopeName: "IT" }, { trailing: () => "👍" });
  expect(document.querySelector(".ai-provenance-trailing")!.textContent).toBe("👍");
  document.querySelector<HTMLButtonElement>("[data-ai-provenance-why]")!.click();
  await flushPromises();
  const popover = document.querySelector("[data-ai-provenance-popover]")!;
  expect(popover.textContent).toContain("Prostor IT");
  expect(popover.textContent).toContain("Odpověď používá jen publikované stránky; filtry změny a řazení se nepoužily.");
  expect(popover.querySelectorAll("[data-source-chip]")).toHaveLength(2);
  expect(popover.textContent).toContain("použito, necitováno");
});
