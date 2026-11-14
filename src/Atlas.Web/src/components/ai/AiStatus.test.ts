import { afterEach, expect, it, vi } from "vitest";
import { mount } from "@vue/test-utils";
import AiStatus from "./AiStatus.vue";
import { answerTurnStatus, formatElapsed } from "../../ai/aiStatus";

afterEach(() => { vi.useRealTimers(); });

// The one status line of a running model task (board „AI znak – velikosti, stavy a průběh“).
it("names only the two real phases of a turn, with what is known of them", () => {
  expect(answerTurnStatus("retrieving")).toMatchObject({ glyph: "searching", label: "Hledám ve wiki…", meta: null });
  expect(answerTurnStatus("preparing", { sources: 3, scope: { kind: "space", spaceId: "s" }, scopeName: "IT" })).toEqual({
    glyph: "writing", label: "Píšu odpověď", meta: "3 zdroje",
    steps: [{ label: "Hledám v prostoru IT", meta: "3 zdroje", state: "done" }, { label: "Píšu odpověď", state: "now" }],
  });
  expect(answerTurnStatus("retrieving", { kind: "summary" })!.label).toBe("Čtu stránku…");
  expect(answerTurnStatus("preparing", { kind: "summary", sources: 1 })!.label).toBe("Píšu shrnutí");
  for (const state of ["done", "error", "offer", "canceled"]) expect(answerTurnStatus(state)).toBeNull();
  expect([formatElapsed(400), formatElapsed(4200), formatElapsed(65_000), formatElapsed(120_000)]).toEqual(["0 s", "4 s", "1 min 5 s", "2 min"]);
});

it("announces the verb, keeps the running time from assistive technology and stops only where asked", async () => {
  vi.useFakeTimers();
  const line = mount(AiStatus, { props: { size: "s", state: "writing", label: "Píšu odpověď", meta: "3 zdroje", elapsed: true, progress: true } });
  expect(line.get("[role=status]").text()).toBe("Píšu odpověď · 3 zdroje");
  expect(line.get("[data-ai-glyph]").attributes("data-state")).toBe("writing");
  expect(line.find("[data-ai-stop]").exists()).toBe(false);
  expect(line.find(".ai-status-bar").exists()).toBe(true);
  await vi.advanceTimersByTimeAsync(4100);
  expect(line.get("[data-ai-elapsed]").attributes("aria-hidden")).toBe("true");
  expect(line.get("[data-ai-elapsed]").text()).toBe("4 s");
  await line.setProps({ stoppable: true });
  await line.get("[data-ai-stop]").trigger("click");
  expect(line.emitted("stop")).toHaveLength(1);
  line.unmount();
});

it("lists the steps in the assistant size with the glyph on the current one", () => {
  const status = answerTurnStatus("preparing", { sources: 2 })!;
  const line = mount(AiStatus, { props: { size: "m", state: status.glyph, label: status.label, meta: status.meta, steps: status.steps, note: "Odpovídá jen ze zdrojů, které smíte číst", stoppable: true } });
  const steps = line.findAll("[data-step]");
  expect(steps.map((step) => step.attributes("data-step"))).toEqual(["done", "now"]);
  expect(steps[1]!.find("[data-ai-glyph][data-state=writing]").exists()).toBe(true);
  expect(steps[0]!.find("[data-ai-glyph]").exists()).toBe(false);
  expect(line.get("[role=status]").text()).toBe("Píšu odpověď, 2 zdroje");
  expect(line.text()).toContain("Odpovídá jen ze zdrojů, které smíte číst");
  expect(line.find("[data-ai-stop]").exists()).toBe(true);
});
