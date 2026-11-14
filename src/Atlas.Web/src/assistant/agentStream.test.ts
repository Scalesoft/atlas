import { describe, expect, it } from "vitest";
import { ApiError } from "../api";
import { applyStep, readAgentStream, stepLabel, type AgentStepEvent, type AgentStepView } from "./agentStream";

const message = { id: "m1", conversationId: "c1", role: "assistant", state: "answered", sentences: [], sources: [], createdAt: "2026-10-16T10:00:00Z" };
const line = (value: unknown) => `${JSON.stringify(value)}\n`;
/** A body that delivers the text in the given pieces, as the network would (UTF-8 bytes, cut anywhere). */
function body(...pieces: (string | Uint8Array)[]) {
  const encoder = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(controller) { for (const piece of pieces) controller.enqueue(typeof piece === "string" ? encoder.encode(piece) : piece); controller.close(); },
  });
}

describe("readAgentStream", () => {
  it("reads steps and the message even when lines and characters are cut across chunks", async () => {
    const text = line({ type: "step", id: "s1", phase: "start", tool: "atlas_get_page", target: "Příručka" }) + line({ type: "step", id: "s1", phase: "end", tool: "atlas_get_page", ok: true }) + line({ type: "message", message });
    const bytes = new TextEncoder().encode(text);
    const cut = [7, 40, 61, bytes.length].map((end, i, all) => bytes.slice(i ? all[i - 1] : 0, end)); // cuts inside "ř"/"í" too
    const steps: AgentStepEvent[] = [];
    const result = await readAgentStream(body(...cut), { onStep: (event) => steps.push(event) });
    expect(result.id).toBe("m1");
    expect(steps.map((s) => [s.id, s.phase, s.target ?? null])).toEqual([["s1", "start", "Příručka"], ["s1", "end", null]]);
  });

  it("accepts a last line without a newline, blank lines and unknown line types or fields", async () => {
    const proposals: unknown[] = [];
    const result = await readAgentStream(body("\n", line({ type: "future", x: 1 }), line({ type: "proposal", proposal: { id: "p1" } }), JSON.stringify({ type: "message", message, extra: true })), { onProposal: (p) => proposals.push(p) });
    expect(result.state).toBe("answered");
    expect(proposals).toEqual([{ id: "p1" }]);
  });

  it("turns a stream that ends without a message into an interrupted answer error", async () => {
    const failure = await readAgentStream(body(line({ type: "step", id: "s1", phase: "start", tool: "atlas_search" }), `{"type":"mess`)).catch((e) => e);
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 502, code: "stream_interrupted" });
    await expect(readAgentStream(body(line({ type: "step", id: "s1", phase: "start", tool: "atlas_search" })))).rejects.toMatchObject({ code: "stream_interrupted" });
    await expect(readAgentStream(null)).rejects.toMatchObject({ code: "stream_interrupted" });
  });

  it("throws the error line with its code and status", async () => {
    await expect(readAgentStream(body(line({ type: "error", code: "ai_unavailable", status: 503 })))).rejects.toMatchObject({ status: 503, code: "ai_unavailable" });
    await expect(readAgentStream(body(line({ type: "error", code: "x" })))).rejects.toMatchObject({ status: 502, code: "x" });
  });

  it("fails on a line that is not JSON", async () => {
    await expect(readAgentStream(body("nonsense\n", line({ type: "message", message })))).rejects.toMatchObject({ code: "stream_interrupted" });
  });
});

describe("steps", () => {
  it("names tools with Czech verbs and never shows the tool name", () => {
    expect(stepLabel("atlas_search")).toBe("Hledám");
    expect(stepLabel("atlas_get_page", "Instalace VPN")).toBe("Čtu stránku Instalace VPN");
    expect(stepLabel("atlas_replace_page", "Kontakty")).toBe("Navrhuji změnu stránky Kontakty");
    expect(stepLabel("atlas_get_space_tree", "IT")).toBe("Procházím strom prostoru IT");
    expect(stepLabel("atlas_find_mentions")).toBe("Hledám zmínky");
    expect(stepLabel("atlas_explain_access")).toBe("Zjišťuji přístupy");
    expect(stepLabel("atlas_unknown_tool")).toBe("Zjišťuji informace");
  });

  it("runs a step and closes it with its end, a failed one as failed", () => {
    let steps: AgentStepView[] = [];
    steps = applyStep(steps, { id: "a", phase: "start", tool: "atlas_search" });
    steps = applyStep(steps, { id: "b", phase: "start", tool: "atlas_get_page", target: "VPN" });
    steps = applyStep(steps, { id: "a", phase: "end", tool: "atlas_search", ok: true });
    steps = applyStep(steps, { id: "b", phase: "end", tool: "atlas_get_page", target: "VPN", ok: false });
    expect(steps).toEqual([{ id: "a", label: "Hledám", state: "done" }, { id: "b", label: "Čtu stránku VPN", state: "failed" }]);
  });
});
