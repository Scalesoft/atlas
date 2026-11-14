import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AiEvaluationSection from "./AiEvaluationSection.vue";
import type { AiEvaluationReport } from "../../../../ai/reports";
let view: VueWrapper | undefined;
afterEach(() => view?.unmount());
it("runs only explicitly and labels synthetic checks without claiming factual quality", async () => {
 const run = vi.fn(async () => ({ setVersion: "answer-diagnostic@1", promptVersion: "answer@1", items: [{ id: "vpn", question: "Jak připojit VPN?", expectedAnswerable: true, answerable: true, passed: true, invalidCitations: 0, code: null, latencyMs: 30 }] }));
 view = mount(AiEvaluationSection, { props: { run } });
 expect(run).not.toHaveBeenCalled();
 expect(view.text()).toContain("tokeny");
 await view.get("[data-eval-run]").trigger("click"); await flushPromises();
 expect(run).toHaveBeenCalledTimes(1);
 expect(view.text()).toContain("1 z 1");
 expect(view.text()).toContain("faktickou správnost");
 expect(view.text()).toContain("answer-diagnostic@1");
});
it("cancels a pending run and does not present a late result", async () => {
 let finish!: (v: AiEvaluationReport) => void;
 const run = vi.fn((_signal: AbortSignal) => new Promise<AiEvaluationReport>(resolve => { finish = resolve; }));
 view = mount(AiEvaluationSection, { props: { run } });
 await view.get("[data-eval-run]").trigger("click");
 await view.get("[data-eval-cancel]").trigger("click");
 expect(run.mock.calls[0]![0].aborted).toBe(true);
 finish({ setVersion: "LATE-RESULT", promptVersion: "answer@1", items: [] }); await flushPromises();
 expect(view.text()).not.toContain("LATE-RESULT");
 expect(view.find("[data-eval-run]").exists()).toBe(true);
});
