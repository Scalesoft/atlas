import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { useAssistant } from "../../../assistant/useAssistant";
import AssistantPanelExample from "./AssistantPanelExample.vue";
import { withProviders } from "./aiSampleData";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

it("shows the real panel in every scene with local data and cleans the shared state up", async () => {
  wrapper = mount(withProviders(AssistantPanelExample), { attachTo: document.body });
  await flushPromises();
  const frame = () => wrapper!.get("[data-asp-frame]");
  expect(frame().findAll("[data-ai-body]")).toHaveLength(2);
  expect(frame().find("[data-assistant-composer]").exists()).toBe(true);

  const scene = async (value: string) => { await wrapper!.get(`[data-value="${value}"]`).trigger("click"); await flushPromises(); };
  await scene("scope");
  expect(frame().get("[data-assistant-scope-change]").text()).toBe("Rozsah změněn na: Celá dostupná wiki");
  await scene("list");
  expect(frame().text()).toContain("Konverzace není dostupná");
  await scene("outdated");
  expect(frame().get("[data-assistant-readonly]").text()).toContain("Tato konverzace používá předchozí nastavení.");
  await scene("unavailable");
  expect(frame().get("[data-ai-withheld]").text()).toBe("Tato odpověď už není dostupná.");
  await scene("outage");
  expect(frame().get("[data-ai-unavailable]").text()).toBe("Tahle funkce teď není k dispozici.");
  expect(frame().find("[data-assistant-composer]").exists()).toBe(false);
  await wrapper.get("[data-asp-narrow]").setValue(true);
  expect(frame().attributes("style")).toContain("360px");

  wrapper.unmount(); wrapper = undefined;
  expect(useAssistant().items.value).toHaveLength(0);
  expect(useAssistant().open.value).toBe(false);
});

it("shows the S nástroji switch, running steps and the scene without the right", async () => {
  wrapper = mount(withProviders(AssistantPanelExample), { attachTo: document.body });
  await flushPromises();
  const frame = () => wrapper!.get("[data-asp-frame]");
  const scene = async (value: string) => { await wrapper!.get(`[data-value="${value}"]`).trigger("click"); await flushPromises(); };
  // Off by default (owner, 2026-10-28).
  expect(frame().get("[data-assistant-tools] [role=switch]").attributes("aria-checked")).toBe("false");
  expect(frame().get("[data-ai-effort-pick]").attributes("disabled")).toBeUndefined();
  expect(frame().get("[data-ai-steps-fold] button").text()).toBe("Použité kroky ▸ 2");

  await scene("steps");
  expect(frame().findAll(".ai-status-step").map((step) => step.text())).toEqual(["Procházím strom prostoru IT a infrastruktura", "Čtu stránku Instalace VPN", "Hledám zmínky", "Píšu odpověď"]);

  await scene("tools-on");
  expect(frame().get("[data-assistant-tools] [role=switch]").attributes("aria-checked")).toBe("true");
  expect(frame().get("[data-ai-effort-pick]").attributes("disabled")).toBeDefined();

  await scene("no-right");
  expect(frame().find("[data-assistant-tools]").exists()).toBe(false);
  expect(frame().get("[data-ai-effort-pick]").attributes("disabled")).toBeUndefined();
});

it("shows the proposal card scenes and answers the decision locally", async () => {
  wrapper = mount(withProviders(AssistantPanelExample), { attachTo: document.body });
  await flushPromises();
  const frame = () => wrapper!.get("[data-asp-frame]");
  const scene = async (value: string) => { await wrapper!.get(`[data-value="${value}"]`).trigger("click"); await flushPromises(); };
  await scene("proposal");
  expect(frame().get("[data-proposal-count]").text()).toBe("Vybráno 8 z 8");
  await frame().get("[data-proposal-confirm]").trigger("click");
  await new Promise((resolve) => setTimeout(resolve, 600)); await flushPromises();
  expect(frame().get("[data-result-banner]").text()).toContain("Provedeno 8 změn");
  await scene("proposal-40");
  expect(frame().get("[data-proposal-count]").text()).toBe("Vybráno 40 z 40");
  await scene("proposal-done");
  expect(frame().get("[data-result-banner]").text()).toContain("Provedeno 6 změn, 1 se nepodařila");
  await scene("proposal-conflict");
  await frame().get("[data-proposal-confirm]").trigger("click");
  await new Promise((resolve) => setTimeout(resolve, 600)); await flushPromises();
  expect(frame().get("[data-proposal-error]").text()).toContain("vypršel");
  expect(frame().findAll("[data-proposal-status]")).toHaveLength(0);
});

it("shows the move and publication proposal scenes", async () => {
  wrapper = mount(withProviders(AssistantPanelExample), { attachTo: document.body });
  await flushPromises();
  const frame = () => wrapper!.get("[data-asp-frame]");
  const scene = async (value: string) => { await wrapper!.get(`[data-value="${value}"]`).trigger("click"); await flushPromises(); };
  await scene("proposal-move");
  expect(frame().get("[data-proposal-move]").text()).toContain("IT a infrastruktura › Kancelář › Tiskárny");
  expect(frame().get("[data-proposal-move-facts]").text()).toBe("S ní se přesune 2 podstránky");
  await scene("proposal-menu");
  expect(frame().findAll("[data-proposal-move]").map((row) => row.text())).toHaveLength(3);
  expect(frame().get("[data-proposal-count]").text()).toBe("Vybráno 3 z 3");
  expect(frame().findAll("[data-proposal-move]")[1]!.text()).toContain("Kancelář › Wi-Fi pro hosty (před Tiskárny)");
  await scene("proposal-publish");
  expect(frame().get("[data-proposal-publish]").text()).toContain("Verze v7 (publikovaná v5)");
  expect(frame().get("[data-proposal-findings]").text()).toContain("Certifikáty zařízení");
  await frame().get("[data-proposal-confirm]").trigger("click");
  await new Promise((resolve) => setTimeout(resolve, 600)); await flushPromises();
  expect(frame().get("[data-proposal-status]").text()).toBe("Provedeno");
});

it("shows the draft proposal scenes: a section change, a reformat and a lost component", async () => {
  wrapper = mount(withProviders(AssistantPanelExample), { attachTo: document.body });
  await flushPromises();
  const frame = () => wrapper!.get("[data-asp-frame]");
  const scene = async (value: string) => { await wrapper!.get(`[data-value="${value}"]`).trigger("click"); await flushPromises(); };
  await scene("proposal-section");
  expect(frame().get("[data-proposal-diff]").text()).toContain("Změněné sekce: Kontakty");
  await frame().get("[data-proposal-confirm]").trigger("click");
  await new Promise((resolve) => setTimeout(resolve, 600)); await flushPromises();
  await frame().get("[data-proposal-open-editor]").trigger("click");
  expect(wrapper.get("[data-asp-editor]").text()).toContain("page-1");
  await scene("proposal-reformat");
  expect(frame().get("[data-proposal-diff]").text()).toContain("Postup, Řešení potíží, Kontakty, Připojení");
  await scene("proposal-removed");
  expect(frame().get("[data-proposal-status]").text()).toContain("Komponenta by se ztratila");
  expect(frame().find("[data-proposal-confirm]").exists()).toBe(false);
});
